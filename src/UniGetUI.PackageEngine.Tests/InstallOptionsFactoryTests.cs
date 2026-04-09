using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class InstallOptionsFactoryTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(InstallOptionsFactoryTests),
        Guid.NewGuid().ToString("N")
    );

    public InstallOptionsFactoryTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCLIArguments),
            false
        );
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowPrePostOpCommand),
            false
        );
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
    public void LoadApplicable_UsesManagerDefaultsAndExpandsPackageToken()
    {
        var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso:Tool").Build();
        var managerOptions = new InstallOptions
        {
            CustomInstallLocation = @"C:\Apps\%PACKAGE%",
            InteractiveInstallation = true,
        };

        InstallOptionsFactory.SaveForManager(managerOptions, manager);
        InstallOptionsFactory.SaveForPackage(new InstallOptions(), package);

        var resolved = InstallOptionsFactory.LoadApplicable(package);

        Assert.Equal(
            $@"C:\Apps\{CoreTools.MakeValidFileName(package.Id)}",
            resolved.CustomInstallLocation
        );
        Assert.True(resolved.InteractiveInstallation);
    }

    [Fact]
    public void LoadApplicable_AppliesExplicitOverridesAndRemovesDisallowedSecureOptions()
    {
        var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
        var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
        var packageOptions = new InstallOptions
        {
            OverridesNextLevelOpts = true,
            CustomParameters_Install = ["--keep&drop|;<>\n"],
            CustomParameters_Update = ["--update"],
            CustomParameters_Uninstall = ["--remove"],
            PreInstallCommand = "echo pre",
            PostInstallCommand = "echo post",
            PreUpdateCommand = "echo pre-update",
            PostUpdateCommand = "echo post-update",
            PreUninstallCommand = "echo pre-uninstall",
            PostUninstallCommand = "echo post-uninstall",
        };

        InstallOptionsFactory.SaveForPackage(packageOptions, package);

        var resolved = InstallOptionsFactory.LoadApplicable(
            package,
            elevated: true,
            interactive: true,
            no_integrity: true,
            remove_data: true
        );

        Assert.True(resolved.RunAsAdministrator);
        Assert.True(resolved.InteractiveInstallation);
        Assert.True(resolved.SkipHashCheck);
        Assert.True(resolved.RemoveDataOnUninstall);
        Assert.Empty(resolved.CustomParameters_Install);
        Assert.Empty(resolved.CustomParameters_Update);
        Assert.Empty(resolved.CustomParameters_Uninstall);
        Assert.Equal("", resolved.PreInstallCommand);
        Assert.Equal("", resolved.PostInstallCommand);
        Assert.Equal("", resolved.PreUpdateCommand);
        Assert.Equal("", resolved.PostUpdateCommand);
        Assert.Equal("", resolved.PreUninstallCommand);
        Assert.Equal("", resolved.PostUninstallCommand);
    }

    [Fact]
    public void LoadApplicable_SanitizesCustomParametersWhenCliArgumentsAreAllowed()
    {
        var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
        var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
        var packageOptions = new InstallOptions
        {
            OverridesNextLevelOpts = true,
            CustomParameters_Install = ["--keep&drop|;<>\n"],
        };

        SecureSettings.ApplyForUser(Environment.UserName, SecureSettings.ResolveKey(SecureSettings.K.AllowCLIArguments), true);
        InstallOptionsFactory.SaveForPackage(packageOptions, package);

        var resolved = InstallOptionsFactory.LoadApplicable(package);

        Assert.Equal(["--keepdrop"], resolved.CustomParameters_Install);
    }

    [Fact]
    public void SaveAndLoadForPackage_RoundTripsPersistedOptions()
    {
        var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
        var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
        var expected = new InstallOptions
        {
            OverridesNextLevelOpts = true,
            Architecture = "x64",
            CustomInstallLocation = @"D:\Tools",
            InteractiveInstallation = true,
            SkipMinorUpdates = true,
        };
        expected.CustomParameters_Install.Add("--quiet");

        InstallOptionsFactory.SaveForPackage(expected, package);

        var actual = InstallOptionsFactory.LoadForPackage(package);

        Assert.True(actual.OverridesNextLevelOpts);
        Assert.Equal("x64", actual.Architecture);
        Assert.Equal(@"D:\Tools", actual.CustomInstallLocation);
        Assert.True(actual.InteractiveInstallation);
        Assert.True(actual.SkipMinorUpdates);
        Assert.Equal(["--quiet"], actual.CustomParameters_Install);
    }
}
