#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShell7ManagerTests
{
    [Fact]
    public void ParseInstalledPackages_BuildsPackagesFromTabDelimitedOutput()
    {
        var manager = new PowerShell7();

        var packages = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##",
                "Pester\t5.7.1\tPSGallery",
                "##SCOPE:CurrentUser##",
                "PSReadLine\t2.2.5\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Pester", package.Id);
                Assert.Equal("5.7.1", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
                Assert.Equal(PackageScope.Machine, package.OverridenOptions.Scope);
            },
            package =>
            {
                Assert.Equal("PSReadLine", package.Id);
                Assert.Equal("2.2.5", package.VersionString);
                Assert.Equal("PSGallery", package.Source.Name);
                Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackages_SkipsBlankAndMalformedLines()
    {
        var manager = new PowerShell7();

        var package = Assert.Single(
            PowerShell7.ParseInstalledPackages(
                [
                    "##SCOPE:AllUsers##",
                    "",
                    "not-enough-columns",
                    "only\ttwo",
                    "\t\t",
                    "Pester\t5.7.1\tPSGallery",
                ],
                manager
            )
        );

        Assert.Equal("Pester", package.Id);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/5110:
    // a scope explicitly chosen in the options dialog must override the auto-detected
    // install scope, otherwise the command never changes for an installed module.
    [Fact]
    public void GetParameters_ExplicitScopeOverridesDetectedScope()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.1.0\tPSGallery"], manager));
        Assert.Equal(PackageScope.Machine, package.OverridenOptions.Scope);

        var options = new InstallOptions { InstallationScope = PackageScope.User };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.Contains("CurrentUser", parameters);
        Assert.DoesNotContain("AllUsers", parameters);
    }

    [Fact]
    public void GetParameters_ExplicitGlobalOverridesDetectedUserScope()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2025.1.0\tPSGallery"], manager));
        Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.Contains("AllUsers", parameters);
        Assert.DoesNotContain("CurrentUser", parameters);
    }

    [Fact]
    public void GetParameters_DefaultScopeFallsBackToDetectedScope()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.1.0\tPSGallery"], manager));

        var options = new InstallOptions();
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.Contains("AllUsers", parameters);
        Assert.DoesNotContain("CurrentUser", parameters);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/5163:
    // the update-list package produced from the GetUpdates() response must carry the
    // installed scope, otherwise Update-PSResource silently defaults to CurrentUser and
    // the AllUsers copy never updates.
    [Fact]
    public void ParseUpdatesResponse_CarriesAllUsersScopeOntoUpdate()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.1.0\tPSGallery"], manager);
        var source = installed[0].Source;
        var idVersion = new Dictionary<string, string> { ["devolutions.powershell"] = "2025.1.0" };
        var idScope = BaseNuGet.BuildInstalledScopeMap(installed);

        var xml = "<entry><d:Id>Devolutions.PowerShell</d:Id><d:Version>2025.2.0</d:Version></entry>";
        var update = Assert.Single(
            BaseNuGet.ParseUpdatesResponse(xml, idVersion, idScope, source, manager));

        Assert.Equal("2025.1.0", update.VersionString);
        Assert.Equal("2025.2.0", update.NewVersionString);
        Assert.Equal(PackageScope.Machine, update.OverridenOptions.Scope);

        var parameters = manager.OperationHelper.GetParameters(update, new InstallOptions(), OperationType.Update);
        Assert.Contains("AllUsers", parameters);
        Assert.DoesNotContain("CurrentUser", parameters);
    }

    [Fact]
    public void ParseUpdatesResponse_CurrentUserModuleStaysCurrentUser()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2025.1.0\tPSGallery"], manager);
        var source = installed[0].Source;
        var idVersion = new Dictionary<string, string> { ["devolutions.powershell"] = "2025.1.0" };
        var idScope = BaseNuGet.BuildInstalledScopeMap(installed);

        var xml = "<entry><d:Id>Devolutions.PowerShell</d:Id><d:Version>2025.2.0</d:Version></entry>";
        var update = Assert.Single(
            BaseNuGet.ParseUpdatesResponse(xml, idVersion, idScope, source, manager));

        Assert.Equal(PackageScope.User, update.OverridenOptions.Scope);

        var parameters = manager.OperationHelper.GetParameters(update, new InstallOptions(), OperationType.Update);
        Assert.Contains("CurrentUser", parameters);
        Assert.DoesNotContain("AllUsers", parameters);
    }

    // A module installed in both scopes must update its system-wide (AllUsers) copy so it
    // never stays stale, regardless of the order the scopes are reported in.
    [Fact]
    public void BuildInstalledScopeMap_PrefersMachineWhenInstalledInBothScopes()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.1.0\tPSGallery",
                "##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2025.1.0\tPSGallery",
            ], manager);

        var idScope = BaseNuGet.BuildInstalledScopeMap(installed);

        Assert.Equal(PackageScope.Machine, idScope["devolutions.powershell"]);
    }

    // Regression for https://github.com/Devolutions/UniGetUI/issues/4781:
    // the previous Format-Table-based pipeline truncated long names (e.g.
    // "Microsoft.Graph.Beta.DeviceManagement.Administration" → "Microsoft.Graph.Beta..")
    // which then poisoned the GetUpdates() URL sent to PSGallery, returning
    // NotFound and silently dropping every PS7 update from the list.
    [Fact]
    public void ParseInstalledPackages_PreservesLongNamesAndVersionsVerbatim()
    {
        var manager = new PowerShell7();

        var packages = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:CurrentUser##",
                "Microsoft.Graph.Beta.DeviceManagement.Administration\t2.34.0\tPSGallery",
                "Az.RedisEnterpriseCache\t1.6.0\tPSGallery",
                "Microsoft.PowerShell.SecretManagement\t1.1.2\tPSGallery",
            ],
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("Microsoft.Graph.Beta.DeviceManagement.Administration", package.Id);
                Assert.Equal("2.34.0", package.VersionString);
            },
            package =>
            {
                Assert.Equal("Az.RedisEnterpriseCache", package.Id);
                Assert.Equal("1.6.0", package.VersionString);
            },
            package =>
            {
                Assert.Equal("Microsoft.PowerShell.SecretManagement", package.Id);
                Assert.Equal("1.1.2", package.VersionString);
            }
        );
    }
}
#endif
