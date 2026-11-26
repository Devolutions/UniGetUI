using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.UvManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

public sealed class InstallOptionsDialogTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(InstallOptionsDialogTests).Assembly);

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(InstallOptionsDialogTests),
        Guid.NewGuid().ToString("N")
    );

    public InstallOptionsDialogTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public Task PythonSectionRendersAndPersistsForUvPackages() => _session.Dispatch<bool>(async () =>
    {
        var manager = new Uv();
        IPackage package = new Package(
            "ruff", "ruff", "0.17.5", "0.17.5",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());
        var options = new InstallOptions();

        var (vm, control, window) = ShowDialog(package, options);

        TextBlock label = control.FindControl<TextBlock>("PythonLabelBlock")
            ?? throw new InvalidOperationException("PythonLabelBlock is missing from InstallOptionsControl");
        StackPanel section = label.GetVisualAncestors().OfType<StackPanel>().First();
        ComboBox managedCombo = ComboBoxBeside(control, "ManagedPythonLabelBlock");
        ComboBox versionCombo = ComboBoxBeside(control, "VersionLabelBlock");

        Assert.True(label.IsVisible);
        Assert.True(section.IsVisible);
        Assert.Equal(3, managedCombo.ItemCount);
        Assert.Equal(vm.SelectedManagedPython, managedCombo.SelectedItem);
        Assert.Contains(CoreTools.Translate("PreRelease"), versionCombo.Items.Cast<object>().Select(i => i.ToString()));

        vm.FollowGlobal = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(managedCombo.IsEffectivelyEnabled);
        await WaitUntil(() => versionCombo.IsEffectivelyEnabled, "the version controls");

        vm.PythonEnabled = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(section.IsVisible);
        vm.PythonEnabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(section.IsVisible);

        string uvManagedLabel = CoreTools.Translate("uv-managed Python");
        Assert.Contains(uvManagedLabel, vm.ManagedPythonOptions);

        vm.SelectedManagedPython = uvManagedLabel;
        Assert.Equal(uvManagedLabel, vm.SelectedManagedPython);
        vm.ApplyChanges();
        Assert.Equal("true", options.Uv_ManagedPython);
        Assert.True(options.OverridesNextLevelOpts);

        InstallOptionsFactory.SaveForPackage(options, package);
        InstallOptions reloaded = InstallOptionsFactory.LoadApplicable(package);
        Assert.Equal("true", reloaded.Uv_ManagedPython);

        var reloadedVm = new InstallOptionsViewModel(package, OperationType.Install, reloaded);
        Assert.Equal(uvManagedLabel, reloadedVm.SelectedManagedPython);

        manager.OperationHelper.ApplyOperationOverrides(package, options, OperationType.Install);
        string command = await vm.BuildCurrentCommandAsync();

        Assert.StartsWith("uv tool", command);
        Assert.Contains("--managed-python", command);

        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task PythonInterpreterDropdownLoadsFromUvWhenAvailable() => _session.Dispatch<bool>(async () =>
    {
        var manager = new Uv();
        manager.Initialize();
        IPackage package = new Package(
            "httpie", "httpie", "3.2.4", "3.2.4",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());

        IReadOnlyList<PythonInterpreterChoice> interpreters =
            await Task.Run(manager.OperationHelper.ListPythonInterpreters);

        var options = new InstallOptions();
        var (vm, _, window) = ShowDialog(package, options);

        if (!manager.Status.Found)
        {
            await WaitUntil(() => vm.PythonOptions.Count > 0, "the Python dropdown");
            Assert.Equal(CoreTools.Translate("Default"), Assert.Single(vm.PythonOptions));
            window.Close();
            return true;
        }

        await WaitUntil(() => vm.PythonOptions.Count > 1, "the uv interpreter list");
        PythonInterpreterChoice target = interpreters[0];
        Assert.Contains(target.DisplayName, vm.PythonOptions);

        vm.SelectedPython = target.DisplayName;
        vm.FollowGlobal = false;
        vm.ApplyChanges();
        Assert.Equal(target.Request, options.Uv_PythonRequest);

        manager.OperationHelper.ApplyOperationOverrides(package, options, OperationType.Install);
        string command = await vm.BuildCurrentCommandAsync();
        Assert.Contains("--python", command);
        Assert.Contains(target.Request, command);

        InstallOptionsFactory.SaveForPackage(options, package);
        InstallOptions reloaded = InstallOptionsFactory.LoadApplicable(package);
        Assert.Equal(target.Request, reloaded.Uv_PythonRequest);

        var reloadedVm = new InstallOptionsViewModel(package, OperationType.Install, reloaded);
        await WaitUntil(
            () => reloadedVm.PythonOptions.Contains(target.DisplayName),
            "the persisted interpreter on reload");
        Assert.Equal(target.DisplayName, reloadedVm.SelectedPython);

        reloadedVm.FollowGlobal = false;
        reloadedVm.ApplyChanges();
        Assert.Equal(target.Request, reloaded.Uv_PythonRequest);

        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ThePreviewShowsThePythonChoicesPickedInTheDialog() => _session.Dispatch<bool>(async () =>
    {
        var manager = new Uv();
        IPackage package = new Package(
            "cowsay", "cowsay", "6.1", "6.1",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());

        var (vm, _, window) = ShowDialog(package, new InstallOptions());

        vm.FollowGlobal = false;
        Dispatcher.UIThread.RunJobs();
        vm.SelectedManagedPython = CoreTools.Translate("uv-managed Python");
        vm.NoPythonDownloadsChecked = true;
        Dispatcher.UIThread.RunJobs();

        string command = await vm.BuildCurrentCommandAsync();

        Assert.Contains("--managed-python", command);
        Assert.Contains("--no-python-downloads", command);

        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SavingBeforeTheInterpreterListLoadsKeepsTheSavedRequest() => _session.Dispatch<bool>(async () =>
    {
        var manager = new Uv();
        IPackage package = new Package(
            "ruff", "ruff", "0.17.5", "0.17.5",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());
        var options = new InstallOptions
        {
            Uv_PythonRequest = "3.12",
            Uv_ManagedPython = "false",
            Uv_PythonOptionsChosen = true,
        };

        var (vm, _, window) = ShowDialog(package, options);

        // `uv python list` may not have answered yet; saving must still keep the request.
        vm.ApplyChanges();

        Assert.Equal("3.12", options.Uv_PythonRequest);
        Assert.Equal("false", options.Uv_ManagedPython);

        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task TheDialogStartsFromTheInterpreterTheToolWasInstalledWith() => _session.Dispatch<bool>(async () =>
    {
        WriteInstallRecord("ruff", pythonRequest: "3.12", managedPython: false, noPythonDownloads: true);
        var manager = new Uv();
        IPackage package = new Package(
            "ruff", "ruff", "0.17.5", "0.17.5",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());
        var options = new InstallOptions();

        var (vm, _, window) = ShowDialog(package, options);

        Assert.Equal(CoreTools.Translate("system Python"), vm.SelectedManagedPython);
        Assert.True(vm.NoPythonDownloadsChecked);

        vm.ApplyChanges();

        Assert.Equal("3.12", options.Uv_PythonRequest);
        Assert.Equal("false", options.Uv_ManagedPython);
        Assert.True(options.Uv_NoPythonDownloads);
        Assert.True(options.Uv_PythonOptionsChosen);

        window.Close();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task AnExplicitDefaultInTheSavedOptionsIsNotReplacedByTheRecord() => _session.Dispatch<bool>(async () =>
    {
        WriteInstallRecord("ruff", pythonRequest: "3.12", managedPython: null, noPythonDownloads: false);
        var manager = new Uv();
        IPackage package = new Package(
            "ruff", "ruff", "0.17.5", "0.17.5",
            manager.Properties.DefaultSource, manager, new OverridenInstallationOptions());
        var options = new InstallOptions { Uv_PythonOptionsChosen = true };

        var (vm, _, window) = ShowDialog(package, options);

        Assert.Equal(CoreTools.Translate("Default"), vm.SelectedPython);
        Assert.Equal(CoreTools.Translate("Default"), vm.SelectedManagedPython);
        Assert.False(vm.NoPythonDownloadsChecked);

        vm.ApplyChanges();

        Assert.Equal("", options.Uv_PythonRequest);
        Assert.True(options.Uv_PythonOptionsChosen);

        window.Close();
        return true;
    }, CancellationToken.None);

    private static void WriteInstallRecord(
        string id,
        string? pythonRequest,
        bool? managedPython,
        bool noPythonDownloads)
    {
        var record = new JsonObject
        {
            ["id"] = id,
            ["version"] = "0.17.5",
            ["source"] = "uv",
            ["schemaVersion"] = 1,
            ["noPythonDownloads"] = noPythonDownloads,
        };
        if (pythonRequest is not null)
            record["pythonRequest"] = pythonRequest;
        if (managedPython is bool managed)
            record["managedPython"] = managed;

        Directory.CreateDirectory(CoreData.UniGetUIDataDirectory);
        File.WriteAllText(
            Path.Combine(CoreData.UniGetUIDataDirectory, "uv_tool_install_records.json"),
            new JsonArray(record).ToJsonString()
        );
    }

    private (InstallOptionsViewModel Vm, InstallOptionsControl Control, Window Window) ShowDialog(
        IPackage package, InstallOptions options)
    {
        var vm = new InstallOptionsViewModel(package, OperationType.Install, options);
        var control = new InstallOptionsControl { DataContext = vm };
        var window = new Window { Width = 900, Height = 700, Content = control };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (vm, control, window);
    }

    private static ComboBox ComboBoxBeside(Control control, string labelName)
    {
        TextBlock label = control.FindControl<TextBlock>(labelName)
            ?? throw new InvalidOperationException($"{labelName} is missing from InstallOptionsControl");
        return label.GetVisualAncestors().OfType<Grid>().First()
            .GetVisualDescendants().OfType<ComboBox>().Single();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{what} did not settle within 10 seconds");
            await Task.Delay(100);
        }
    }
}
