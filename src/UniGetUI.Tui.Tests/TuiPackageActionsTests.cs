using Avalonia.Threading;
using NUnit.Framework;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Pages;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiPackageActionsTests : TuiE2ETestBase
{
    [TestCase(OperationType.Update, OperationType.Update)]
    [TestCase(OperationType.Install, OperationType.Install)]
    [TestCase(OperationType.Update, OperationType.Install)]
    public async Task PendingOperation_RefusesChainWithIntendedRoleAndUnchangedQueue(OperationType intended, OperationType pending)
    {
        await ResetAsync(TuiPageIds.Installed);
        var pkg = InstalledPackagesLoader.Instance.Packages.Single(p => p.Id == "Contoso.Notes");
        using PackageOperation existing = pending == OperationType.Update
            ? new UpdatePackageOperation(pkg, new InstallOptions())
            : new InstallPackageOperation(pkg, new InstallOptions());
        PackageTag tag = pkg.Tag;
        try
        {
            await OnUi(() =>
            {
                TuiOperationRegistry.Track(existing);
                AbstractOperation.OperationQueue.Add(existing);
                pkg.SetTag(PackageTag.OnQueue);
            });
            var before = await OnUi(TuiOperationRegistry.Snapshot);
            var queue = AbstractOperation.OperationQueue.ToArray();
            int logs = Logger.GetLogs().Length;
            bool accepted = intended == OperationType.Install
                ? await TuiPackageActions.UninstallThenInstallAsync(pkg)
                : await TuiPackageActions.UninstallThenInstallAsync(pkg, intended);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            NAssert.That(accepted, Is.False);
            NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.EqualTo(before));
            NAssert.That(AbstractOperation.OperationQueue, Is.EqualTo(queue));
            NAssert.That(existing.Started, Is.False);
            NAssert.That(Logger.GetLogs().Skip(logs).Select(log => log.Content).Where(line => line.StartsWith("Skipping ")),
                Is.EqualTo(new[] { $"Skipping {intended} of Contoso.Notes because an operation for this package is already queued or running" }));
            NAssert.That(State.OperationJournal, Is.Empty);
        }
        finally
        {
            await OnUi(() => TuiOperationRegistry.Remove(existing));
            pkg.SetTag(tag);
        }
    }

    [TestCase(TuiPageIds.Updates, OperationType.Update, "Uninstall package, then update it")]
    [TestCase(TuiPageIds.Installed, OperationType.Install, "Uninstall package, then reinstall it")]
    public async Task PageAction_ForwardsIntendedPendingRole(string pageId, OperationType intended, string label)
    {
        await ResetAsync(pageId);
        await EnterPage();
        await FilterPackagesAsync("Northwind Terminal");
        var page = await OnUi(() => Window.GetPage<PackageListPage>(pageId));
        var pkg = await OnUi(() => page.VisibleRows.Single().Package);
        var tag = pkg.Tag;
        try
        {
            pkg.SetTag(PackageTag.BeingProcessed);
            var before = await OnUi(TuiOperationRegistry.Snapshot);
            var queue = AbstractOperation.OperationQueue.ToArray();
            var action = await OnUi(() => page.Actions.Single(a => a.Label == label));
            NAssert.That(action.IsEnabled, Is.True);
            int logs = Logger.GetLogs().Length;
            // Await the real menu command itself (rather than a timing poll after a fire-and-forget keystroke).
            await (await OnUi(action.Run));
            NAssert.That(Logger.GetLogs().Skip(logs).Select(log => log.Content).Where(line => line.StartsWith("Skipping ")),
                Is.EqualTo(new[] { $"Skipping {intended} of Northwind.Terminal because an operation for this package is already queued or running" }));
            NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.EqualTo(before));
            NAssert.That(AbstractOperation.OperationQueue, Is.EqualTo(queue));
            NAssert.That(State.OperationJournal, Is.Empty);
        }
        finally { pkg.SetTag(tag); }
    }

    [TestCase(OperationType.Install)]
    [TestCase(OperationType.Update)]
    public async Task AcceptedChain_RunsPrerequisiteOnceBeforeInstallForBothPendingRoles(OperationType intended)
    {
        await ResetAsync(TuiPageIds.Installed);
        var pkg = InstalledPackagesLoader.Instance.Packages.Single(p => p.Id == "Contoso.Notes");
        bool retain = Settings.Get(Settings.K.MaintainSuccessfulInstalls);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Recorded(object? sender, EventArgs args)
        {
            if (OperationHistoryStore.GetAll().Count(r => r.PackageId == pkg.Id) == 2) complete.TrySetResult();
        }
        OperationHistoryStore.Changed += Recorded;
        try
        {
            Settings.Set(Settings.K.MaintainSuccessfulInstalls, true);
            bool accepted = await (await OnUi(() => TuiPackageActions.UninstallThenInstallAsync(pkg, intended)));
            NAssert.That(accepted, Is.True);
            await complete.Task.WaitAsync(DefaultTimeout);
            var operations = await OnUi(() => TuiOperationRegistry.Snapshot().OfType<PackageOperation>().ToArray());
            NAssert.That(operations.Select(op => op.Role), Is.EqualTo(new[] { OperationType.Uninstall, OperationType.Install }));
            NAssert.That(operations.Select(op => op.Package), Is.All.SameAs(pkg));
            NAssert.That(operations.Select(op => op.Status), Is.All.EqualTo(OperationStatus.Succeeded));
            // The durable fake CLI journal proves exactly-once execution and order, not merely two history kinds.
            NAssert.That(State.OperationJournal.Select(entry => string.Join(' ', entry.Split(' ').Skip(1))),
                Is.EqualTo(new[] { "Winget uninstall Contoso.Notes 2.0.0", "Winget install Contoso.Notes 2.0.0" }));
            NAssert.That(InstalledVersion("Winget", pkg.Id), Is.EqualTo("2.0.0"));
            var records = OperationHistoryStore.GetAll();
            NAssert.That(records.Select(record => record.Kind), Is.EquivalentTo(new[] { "uninstall-package", "install-package" }));
            NAssert.That(records.Select(record => record.Status), Is.All.EqualTo("succeeded"));
            var installOutput = operations[1].GetOutput().Select(line => line.Item1).ToArray();
            NAssert.That(installOutput.Count(line => line.Contains("fake uninstall requested")), Is.EqualTo(1));
            NAssert.That(installOutput.Count(line => line.Contains("fake install requested")), Is.EqualTo(1));
            NAssert.That(Array.FindIndex(installOutput, line => line.Contains("PreOperation 1 out of 1 finished with result Succeeded")),
                Is.LessThan(Array.FindIndex(installOutput, line => line.Contains("fake install requested"))).And.GreaterThanOrEqualTo(0));
        }
        finally
        {
            OperationHistoryStore.Changed -= Recorded;
            Settings.Set(Settings.K.MaintainSuccessfulInstalls, retain);
            await OnUi(TuiOperationRegistry.Reset);
        }
    }

    [Test]
    public async Task VirtualManager_RefusesWithoutQueueMutation()
    {
        await ResetAsync(TuiPageIds.Installed);
        var manager = TuiEngine.FindManager("Winget")!;
        var source = new ManagerSource(manager, "virtual-test", new Uri("https://example.invalid"), isVirtualManager: true);
        IPackage pkg = new Package("Virtual regression", "Virtual.Regression", "1.0", source, manager);
        var before = await OnUi(TuiOperationRegistry.Snapshot);
        var queue = AbstractOperation.OperationQueue.ToArray();
        NAssert.That(await TuiPackageActions.UninstallThenInstallAsync(pkg, OperationType.Update), Is.False);
        NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.EqualTo(before));
        NAssert.That(AbstractOperation.OperationQueue, Is.EqualTo(queue));
        NAssert.That(State.OperationJournal, Is.Empty);
    }
}
