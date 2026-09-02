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

    [Theory]
    [InlineData(@"..\..\..\PWNED")]
    [InlineData("../../../PWNED")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void SaveForPackage_NeverWritesOutsideTheInstallOptionsDirectory(string packageId)
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var package = new PackageBuilder().WithManager(manager).WithId(packageId).Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "MARKER-CONTENT" },
            package
        );

        string optionsDirectory = Path.GetFullPath(
            CoreData.UniGetUIInstallationOptionsDirectory
        );

        foreach (string written in Directory.GetFiles(
            _testRoot,
            "*",
            SearchOption.AllDirectories
        ))
        {
            if (Path.GetFileName(written).Contains("PWNED", StringComparison.Ordinal))
            {
                Assert.Equal(
                    optionsDirectory,
                    Path.GetDirectoryName(Path.GetFullPath(written))
                );
            }
        }
    }

    [Fact]
    public void SaveForPackage_StillRoundTripsAnOrdinaryPackageId()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso:Tool").Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = @"C:\Apps\Contoso" },
            package
        );

        Assert.Equal(
            @"C:\Apps\Contoso",
            InstallOptionsFactory.LoadForPackage(package).CustomInstallLocation
        );
    }

    [Fact]
    public void SaveForPackage_DoesNotLetSanitisationCollideDistinctIds()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var colonId = new PackageBuilder().WithManager(manager).WithId("Contoso:Tool").Build();
        var plainId = new PackageBuilder().WithManager(manager).WithId("ContosoTool").Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "FOR-COLON" },
            colonId
        );
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "FOR-PLAIN" },
            plainId
        );

        Assert.Equal(
            "FOR-COLON",
            InstallOptionsFactory.LoadForPackage(colonId).CustomInstallLocation
        );
        Assert.Equal(
            "FOR-PLAIN",
            InstallOptionsFactory.LoadForPackage(plainId).CustomInstallLocation
        );
    }

    [Fact]
    public void SaveForPackage_KeepsSameIdFromDifferentSourcesApart()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var fromA = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithSource(new SourceBuilder().WithManager(manager).WithName("winget").Build())
            .Build();
        var fromB = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithSource(new SourceBuilder().WithManager(manager).WithName("msstore").Build())
            .Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "FROM-A" },
            fromA
        );
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "FROM-B" },
            fromB
        );

        Assert.Equal(
            "FROM-A",
            InstallOptionsFactory.LoadForPackage(fromA).CustomInstallLocation
        );
        Assert.Equal(
            "FROM-B",
            InstallOptionsFactory.LoadForPackage(fromB).CustomInstallLocation
        );
    }

    [Fact]
    public void SaveForPackage_KeepsAmbiguousSourceAndIdSplitsApart()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var dottedSource = new PackageBuilder()
            .WithManager(manager)
            .WithId("c")
            .WithSource(new SourceBuilder().WithManager(manager).WithName("a.b").Build())
            .Build();
        var dottedId = new PackageBuilder()
            .WithManager(manager)
            .WithId("b.c")
            .WithSource(new SourceBuilder().WithManager(manager).WithName("a").Build())
            .Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "DOTTED-SOURCE" },
            dottedSource
        );
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { CustomInstallLocation = "DOTTED-ID" },
            dottedId
        );

        Assert.Equal(
            "DOTTED-SOURCE",
            InstallOptionsFactory.LoadForPackage(dottedSource).CustomInstallLocation
        );
        Assert.Equal(
            "DOTTED-ID",
            InstallOptionsFactory.LoadForPackage(dottedId).CustomInstallLocation
        );
    }

    [Fact]
    public void LoadForPackage_DoesNotThrowOnPackageIdsWithControlCharacters()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso\0Tool")
            .Build();

        var loaded = InstallOptionsFactory.LoadForPackage(package);

        Assert.NotNull(loaded);
    }

    [Fact]
    public void AutoUpdatesMigration_IgnoresIdentityScopedOptionFiles()
    {
        var manager = new PackageManagerBuilder().WithName("WinGet").Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { AutoUpdatePackage = true },
            package
        );

        string written = Directory
            .GetFiles(CoreData.UniGetUIInstallationOptionsDirectory, "*.json")
            .Select(Path.GetFileName)
            .First(name => !name!.StartsWith("GlobalValues.", StringComparison.Ordinal));

        Assert.True(InstallOptionsFactory.IsIdentityScopedOptionsFile(written!));
        Assert.False(
            InstallOptionsFactory.IsIdentityScopedOptionsFile(
                "WinGet.foo_0123456789abcdef.json"
            )
        );
        Assert.False(InstallOptionsFactory.IsIdentityScopedOptionsFile("WinGet.Contoso.Tool.json"));
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
        Assert.False(resolved.CustomInstallLocationIsExplicit);
    }

    [Fact]
    public void LoadApplicable_MarksLocationExplicitOnlyForPerPackageOverrides()
    {
        var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();

        var explicitPackage = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { OverridesNextLevelOpts = true, CustomInstallLocation = @"D:\dev\app" },
            explicitPackage
        );

        var inheritedPackage = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
        InstallOptionsFactory.SaveForManager(new InstallOptions { CustomInstallLocation = @"D:\Apps\%PACKAGE%" }, manager);
        InstallOptionsFactory.SaveForPackage(new InstallOptions(), inheritedPackage);

        Assert.True(InstallOptionsFactory.LoadApplicable(explicitPackage).CustomInstallLocationIsExplicit);
        Assert.False(InstallOptionsFactory.LoadApplicable(inheritedPackage).CustomInstallLocationIsExplicit);
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
    public void LoadApplicable_ExpandsAngleBracketEnvironmentVariablesByDefault()
    {
        var varName = $"UNIGETUI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, @"C:\Expanded");
        try
        {
            var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
            var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
            var packageOptions = new InstallOptions
            {
                OverridesNextLevelOpts = true,
                CustomInstallLocation = $"<{varName}>\\app",
                CustomParameters_Install = [$"--location=<{varName}>\\app"],
                CustomParameters_Update = [$"--location=<{varName}>"],
                CustomParameters_Uninstall = ["--purge"],
            };

            SecureSettings.ApplyForUser(Environment.UserName, SecureSettings.ResolveKey(SecureSettings.K.AllowCLIArguments), true);
            InstallOptionsFactory.SaveForPackage(packageOptions, package);

            var resolved = InstallOptionsFactory.LoadApplicable(package);

            Assert.Equal(@"C:\Expanded\app", resolved.CustomInstallLocation);
            Assert.Equal([@"--location=C:\Expanded\app"], resolved.CustomParameters_Install);
            Assert.Equal([@"--location=C:\Expanded"], resolved.CustomParameters_Update);
            Assert.Equal(["--purge"], resolved.CustomParameters_Uninstall);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void LoadApplicable_DoesNotExpandPercentSyntaxByDefault()
    {
        var varName = $"UNIGETUI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, @"C:\Expanded");
        try
        {
            var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
            var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
            var packageOptions = new InstallOptions
            {
                OverridesNextLevelOpts = true,
                CustomInstallLocation = $"%{varName}%\\app",
            };

            InstallOptionsFactory.SaveForPackage(packageOptions, package);

            var resolved = InstallOptionsFactory.LoadApplicable(package);

            Assert.Equal($"%{varName}%\\app", resolved.CustomInstallLocation);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void LoadApplicable_ExpandsPercentSyntaxWhenSettingEnabled()
    {
        var varName = $"UNIGETUI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, @"C:\Expanded");
        try
        {
            Settings.Set(Settings.K.ExpandEnvVarsWithPercentSyntax, true);
            var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
            var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
            var packageOptions = new InstallOptions
            {
                OverridesNextLevelOpts = true,
                CustomInstallLocation = $"%{varName}%\\app",
            };

            InstallOptionsFactory.SaveForPackage(packageOptions, package);

            var resolved = InstallOptionsFactory.LoadApplicable(package);

            Assert.Equal(@"C:\Expanded\app", resolved.CustomInstallLocation);
        }
        finally
        {
            Settings.Set(Settings.K.ExpandEnvVarsWithPercentSyntax, false);
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void LoadApplicable_SanitizesMetacharactersIntroducedByEnvironmentVariableExpansion()
    {
        var varName = $"UNIGETUI_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, "safe & rm -rf");
        try
        {
            var manager = new PackageManagerBuilder().WithName($"Manager{Guid.NewGuid():N}").Build();
            var package = new PackageBuilder().WithManager(manager).WithId($"Pkg{Guid.NewGuid():N}").Build();
            var packageOptions = new InstallOptions
            {
                OverridesNextLevelOpts = true,
                CustomParameters_Install = [$"--flag=<{varName}>"],
            };

            SecureSettings.ApplyForUser(Environment.UserName, SecureSettings.ResolveKey(SecureSettings.K.AllowCLIArguments), true);
            InstallOptionsFactory.SaveForPackage(packageOptions, package);

            var resolved = InstallOptionsFactory.LoadApplicable(package);

            Assert.Equal(["--flag=safe  rm -rf"], resolved.CustomParameters_Install);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
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
