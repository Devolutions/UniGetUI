#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShellManagerTests
{
    [Fact]
    public void ParseSources_KeepsLocalAndUncRepositoryLocations()
    {
        var manager = new PowerShell();
        var helper = Assert.IsType<PowerShellSourceHelper>(manager.SourcesHelper);

        var sources = helper.ParseSources(
            [
                "",
                "Name".PadRight(25) + "SourceLocation",
                "----".PadRight(25) + "--------------",
                "PSGallery".PadRight(25) + "https://www.powershellgallery.com/api/v2",
                "Internal Modules".PadRight(25) + @"\\files\ps\modules",
                "Local Drop".PadRight(25) + @"C:\Shared Packages\PowerShell",
                "",
            ]
        );

        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("PSGallery", source.Name);
                Assert.Equal("https://www.powershellgallery.com/api/v2", source.Url.ToString());
            },
            source =>
            {
                Assert.Equal("Internal Modules", source.Name);
                Assert.True(source.Url.IsUnc);
                Assert.Equal(@"\\files\ps\modules", source.Url.LocalPath);
            },
            source =>
            {
                Assert.Equal("Local Drop", source.Name);
                Assert.True(source.Url.IsFile);
                Assert.Equal(@"C:\Shared Packages\PowerShell", source.Url.LocalPath);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_BuildsPackagesFromModuleList()
    {
        var manager = new PowerShell();
        var packages = PowerShell.ParseInstalledPackages(
            [
                "Pester\t5.5.0\tPSGallery",
                "PSReadLine\t2.2.5\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Pester", package.Id);
                Assert.Equal("5.5.0", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
            },
            package =>
            {
                Assert.Equal("PSReadLine", package.Id);
                Assert.Equal("2.2.5", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_SkipsMalformedLines()
    {
        var manager = new PowerShell();

        var package = Assert.Single(
            PowerShell.ParseInstalledPackages(
                [
                    "not-enough-columns",
                    "Pester\t5.5.0\tPSGallery",
                ],
                manager
            )
        );

        Assert.Equal("Pester", package.Id);
    }

    [Fact]
    public void ParseInstalledPackages_PreservesLongNamesAndVersionsVerbatim()
    {
        var manager = new PowerShell();

        var packages = PowerShell.ParseInstalledPackages(
            [
                "VMware.PowerCLI.VCenter.Types.ApplianceService\t12.6.0.19600125\tPSGallery",
                "Microsoft.Graph.Beta.DeviceManagement.Administration\t2.34.0\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("VMware.PowerCLI.VCenter.Types.ApplianceService", package.Id);
                Assert.Equal("12.6.0.19600125", package.VersionString);
            },
            package =>
            {
                Assert.Equal(
                    "Microsoft.Graph.Beta.DeviceManagement.Administration",
                    package.Id
                );
                Assert.Equal("2.34.0", package.VersionString);
            }
        );
    }

    private static UniGetUI.PackageEngine.Interfaces.IPackage BuildInstalledPackage(PowerShell manager)
        => Assert.Single(PowerShell.ParseInstalledPackages(
            [
                "Devolutions.PowerShell\t1.0.0\tPSGallery",
            ],
            manager));

    [Fact]
    public void GetParameters_InstallRespectsExplicitScope()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        Assert.Contains("-Scope", parameters);
        Assert.Contains("AllUsers", parameters);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/5110:
    // Update-Module (Windows PowerShell 5.x / PowerShellGet 1.0.0.1) has no -Scope parameter,
    // so no scope must be emitted for an update regardless of the selected scope.
    [Fact]
    public void GetParameters_UpdateOmitsScope()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.DoesNotContain("-Scope", parameters);
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void GetParameters_PropagatesNonTerminatingErrorsToTheExitCode(OperationType operation)
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(package, new InstallOptions(), operation);

        var errorVariableIndex = parameters.ToList().IndexOf("-ErrorVariable");
        Assert.True(errorVariableIndex >= 0);
        Assert.Equal(PowerShellPkgOperationHelper.ErrorVariableName, parameters[errorVariableIndex + 1]);
        Assert.Equal(
            $";if(${PowerShellPkgOperationHelper.ErrorVariableName}){{exit(1)}}",
            parameters[^1]
        );
    }

    [Theory]
    [InlineData("-ErrorVariable")]
    [InlineData("-ev")]
    [InlineData("-errorvariable:mine")]
    public void GetParameters_YieldsToACustomErrorVariable(string customParameter)
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { CustomParameters_Update = [customParameter, "mine"] };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.DoesNotContain(PowerShellPkgOperationHelper.ErrorVariableName, parameters);
        Assert.DoesNotContain(
            $";if(${PowerShellPkgOperationHelper.ErrorVariableName}){{exit(1)}}",
            parameters
        );
    }

    [Fact]
    public void GetParameters_KeepsCustomParametersBoundToTheCmdlet()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { CustomParameters_Update = ["-Proxy", "http://proxy"] };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.Equal("-Proxy", parameters[^3]);
        Assert.Equal("http://proxy", parameters[^2]);
    }

    private static readonly string[] ClobberFailureOutput =
    [
        "PackageManagement\\Install-Package : The following commands are already available on this ",
        "system:'Find-Package,Install-Package,Uninstall-Package'. This module 'PackageManagement' may override the existing ",
        "commands. If you still want to install this module 'PackageManagement', use -AllowClobber parameter.",
        "    + FullyQualifiedErrorId : CommandAlreadyAvailable,Validate-ModuleCommandAlreadyAvailable,Microsoft.PowerShell.Pack ",
        "   ageManagement.Cmdlets.InstallPackage",
    ];

    [Fact]
    public void GetResult_RetriesWithAllowClobberOnAClobberFailure()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.AutoRetry, veredict);
        Assert.True(package.OverridenOptions.PowerShell_AllowClobber);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains("-AllowClobber", parameters);
    }

    [Fact]
    public void GetResult_DoesNotRetryAClobberFailureTwice()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);
        package.OverridenOptions.PowerShell_AllowClobber = true;

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
    }

    [Fact]
    public void GetParameters_DoesNotSendAllowClobberOnUpdate()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);
        package.OverridenOptions.PowerShell_AllowClobber = true;

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Update
        );

        Assert.DoesNotContain("-AllowClobber", parameters);
    }

    [Fact]
    public void Capabilities_ScopeAppliesToInstallOnly()
    {
        var manager = new PowerShell();
        Assert.True(manager.Capabilities.SupportsCustomScopes);
        Assert.False(manager.Capabilities.SupportsCustomScopesOnUpdate);
        Assert.False(manager.Capabilities.SupportsCustomScopesOnUninstall);
    }
}
#endif
