using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class SourceOperationsTests
{
    [Fact]
    public void RetryAsAdminSetsForceAsAdministrator()
    {
        var source = CreateSource();
        using var operation = new InspectableAddSourceOperation(source);

        operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);

        Assert.True(operation.ForceAsAdministrator);
        Assert.Throws<InvalidOperationException>(
            () => operation.Retry(AbstractOperation.RetryMode.Retry_Interactive)
        );
    }

    [Fact]
    public void CreateInstallAndUninstallPreOpsSkipNonWingetManagers()
    {
        var source = CreateSource();

        Assert.Empty(SourceOperation.CreateInstallPreOps(source, forceLocalWinGet: false));
        Assert.Empty(SourceOperation.CreateInstallPreOps(source, forceLocalWinGet: true));
        Assert.Empty(SourceOperation.CreateUninstallPreOps(source, forceLocalWinGet: false));
        Assert.Empty(SourceOperation.CreateUninstallPreOps(source, forceLocalWinGet: true));
    }

    [Fact]
    public void AddSourcePrepareProcessStartInfoUsesManagerExecutableWithoutAdmin()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\tools\\sources.exe";
                manager.ExecutableArguments = "--cli";
            })
            .ConfigureSources(helper => helper.AddParametersFactory = source => ["source", "add", source.Name])
            .Build();
        var source = new SourceBuilder().WithManager(manager).WithName("community").Build();
        using var operation = new InspectableAddSourceOperation(source);
        AbstractOperation.BadgeCollection? badges = null;
        operation.BadgesChanged += (_, updatedBadges) => badges = updatedBadges;

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal("C:\\tools\\sources.exe", startInfo.FileName);
        Assert.Equal("--cli source add community", startInfo.Arguments.Trim());
        Assert.NotNull(badges);
        Assert.False(badges!.AsAdministrator);
    }

    [Fact]
    public void RemoveSourcePrepareProcessStartInfoUsesElevatorWhenAdminRequired()
    {
        using var prohibitElevation = new BooleanSettingScope(Settings.K.ProhibitElevation, false);
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                var sourceCapabilities = capabilities.Sources;
                sourceCapabilities.MustBeInstalledAsAdmin = true;
                capabilities.Sources = sourceCapabilities;
                return capabilities;
            })
            .ConfigureManager(manager =>
            {
                manager.ExecutablePath = "C:\\tools\\sources.exe";
                manager.ExecutableArguments = "--cli";
            })
            .ConfigureSources(helper =>
                helper.RemoveParametersFactory = source => ["source", "remove", source.Name]
            )
            .Build();
        var source = new SourceBuilder().WithManager(manager).WithName("community").Build();
        using var operation = new InspectableRemoveSourceOperation(source);
        AbstractOperation.BadgeCollection? badges = null;
        operation.BadgesChanged += (_, updatedBadges) => badges = updatedBadges;

        var startInfo = operation.PrepareProcessStartInfoForTests();

        Assert.Equal(CoreData.ElevatorPath, startInfo.FileName);
        Assert.Equal("\"C:\\tools\\sources.exe\" --cli source remove community", startInfo.Arguments);
        Assert.NotNull(badges);
        Assert.True(badges!.AsAdministrator);
    }

    private static IManagerSource CreateSource()
    {
        return new SourceBuilder().WithManager(new PackageManagerBuilder().Build()).Build();
    }

    private sealed class InspectableAddSourceOperation : AddSourceOperation
    {
        public InspectableAddSourceOperation(IManagerSource source, bool forceLocalWinGet = false)
            : base(source, forceLocalWinGet) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            InitializeProcessStartInfoDefaults();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        private void InitializeProcessStartInfoDefaults()
        {
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardInputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            process.StartInfo.FileName = "lol";
            process.StartInfo.Arguments = "lol";
        }
    }

    private sealed class InspectableRemoveSourceOperation : RemoveSourceOperation
    {
        public InspectableRemoveSourceOperation(IManagerSource source, bool forceLocalWinGet = false)
            : base(source, forceLocalWinGet) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            InitializeProcessStartInfoDefaults();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }

        private void InitializeProcessStartInfoDefaults()
        {
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.StandardInputEncoding = System.Text.Encoding.UTF8;
            process.StartInfo.WorkingDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile
            );
            process.StartInfo.FileName = "lol";
            process.StartInfo.Arguments = "lol";
        }
    }

    private sealed class BooleanSettingScope : IDisposable
    {
        private readonly Settings.K _key;
        private readonly bool _originalValue;

        public BooleanSettingScope(Settings.K key, bool value)
        {
            _key = key;
            _originalValue = Settings.Get(key);
            Settings.Set(key, value);
        }

        public void Dispose()
        {
            Settings.Set(_key, _originalValue);
        }
    }
}
