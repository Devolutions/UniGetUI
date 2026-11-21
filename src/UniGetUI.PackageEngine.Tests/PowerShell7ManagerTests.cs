#if WINDOWS
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShell7ManagerTests : IDisposable
{
    private readonly Func<bool> _processIsElevated = PowerShell7PkgOperationHelper.ProcessIsElevated;

    public PowerShell7ManagerTests()
    {
        PowerShell7PkgOperationHelper.ProcessIsElevated = static () => false;
    }

    public void Dispose()
    {
        PowerShell7PkgOperationHelper.ProcessIsElevated = _processIsElevated;
    }

    [Fact]
    public void ParseSources_KeepsRepositoriesThatAreNotHttpUrls()
    {
        var manager = new PowerShell7();
        var helper = Assert.IsType<PowerShell7SourceHelper>(manager.SourcesHelper);

        var sources = helper.ParseSources(
            [
                "",
                "Name".PadRight(14) + "Uri",
                "----".PadRight(14) + "---",
                "PSGallery".PadRight(14) + "https://www.powershellgallery.com/api/v2",
                "LocalRepo".PadRight(14) + "file:///C:/Shared Packages/PSRepo/",
                "UncRepo".PadRight(14) + @"\\files\ps\modules",
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
                Assert.Equal("LocalRepo", source.Name);
                Assert.True(source.Url.IsFile);
                Assert.Equal(@"C:\Shared Packages\PSRepo\", source.Url.LocalPath);
            },
            source =>
            {
                Assert.Equal("UncRepo", source.Name);
                Assert.True(source.Url.IsUnc);
                Assert.Equal(@"\\files\ps\modules", source.Url.LocalPath);
            }
        );
    }

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

    [Fact]
    public void MalformedPackageRowsKeepOtherRowsNormallyButFailListingOnly()
    {
        var manager = new PowerShell7();
        string[] output =
        [
            "##SCOPE:AllUsers##",
            "First\t1.0\tPSGallery",
            "Broken\t1.0",
            "Last\t1.0\tPSGallery",
        ];
        List<Exception> failures = [];

        using (PackageListingScope.TrackFailures(failures.Add))
            Assert.Equal(["First", "Last"], PowerShell7.ParseInstalledPackages(output, manager).Select(package => package.Id));
        Assert.IsType<InvalidDataException>(Assert.Single(failures));

        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidDataException>(() => PowerShell7.ParseInstalledPackages(output, manager));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledListingPreservesNormalNonTerminatingErrorsButStopsInListingOnly(bool listingOnly)
    {
        using var scope = listingOnly ? PackageListingScope.EnterListingOnly() : null;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "function Get-InstalledPSResource { param($Scope)"
            + " if ($Scope -eq 'AllUsers') {"
            + " [pscustomobject]@{Name='Before';Version='1.0';Repository='PSGallery'};"
            + " Write-Error 'unreadable resource';"
            + " [pscustomobject]@{Name='After';Version='1.0';Repository='PSGallery'}"
            + " } else { [pscustomobject]@{Name='Current';Version='1.0';Repository='PSGallery'} } };"
            + PowerShell7.InstalledListingCommand
        );

        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("unreadable resource", stderr.GetAwaiter().GetResult());
        var packages = PowerShell7.ParseInstalledPackages(output.Split('\n'), new PowerShell7());
        Assert.Contains(packages, p => p.Id == "Before");
        Assert.Equal(!listingOnly, packages.Any(p => p.Id == "After"));
        Assert.Equal(!listingOnly, packages.Any(p => p.Id == "Current"));
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

    [Theory]
    [InlineData("AllUsers", PackageScope.User, "AllUsers", "CurrentUser")]
    [InlineData("CurrentUser", PackageScope.Machine, "CurrentUser", "AllUsers")]
    [InlineData("AllUsers", "", "AllUsers", "CurrentUser")]
    public void GetParameters_UninstallTargetsDetectedScope(
        string listedScope,
        string optionsScope,
        string expected,
        string unexpected)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            [$"##SCOPE:{listedScope}##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var options = new InstallOptions { InstallationScope = optionsScope };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);

        int scopeIndex = parameters.ToList().IndexOf("-Scope");
        Assert.True(scopeIndex >= 0);
        Assert.Equal(expected, parameters[scopeIndex + 1]);
        Assert.DoesNotContain(unexpected, parameters);
        Assert.Contains("2026.2.4", parameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetParameters_UninstallWithoutDetectedScopeIgnoresOptionsScope(string? detectedScope)
    {
        var manager = new PowerShell7();
        var package = new Package(
            "Devolutions.PowerShell",
            "Devolutions.PowerShell",
            "2026.2.4",
            manager.DefaultSource,
            manager,
            new OverridenInstallationOptions(detectedScope));

        var options = new InstallOptions { InstallationScope = PackageScope.Machine };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);

        Assert.Contains("CurrentUser", parameters);
        Assert.DoesNotContain("AllUsers", parameters);
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void GetParameters_CustomScopeArgumentIsNotDuplicated(OperationType operation)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var options = new InstallOptions
        {
            CustomParameters_Install = ["-Scope", "AllUsers"],
            CustomParameters_Update = ["-Scope", "AllUsers"],
            CustomParameters_Uninstall = ["-Scope", "AllUsers"],
        };
        var parameters = manager.OperationHelper.GetParameters(package, options, operation);

        Assert.Single(parameters, p => p == "-Scope");
        Assert.Equal("AllUsers", parameters[parameters.ToList().IndexOf("-Scope") + 1]);
        Assert.DoesNotContain("CurrentUser", parameters);
    }

    [Fact]
    public void GetResult_AccessDeniedUninstallRetriesAsAdministrator()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Uninstall,
            [
                "Uninstall-PSResource: Parent directory 'C:\\Program Files\\PowerShell\\Modules\\Devolutions.PowerShell\\2026.2.4' could not be deleted: Access to the path 'Devolutions.PowerShell.psd1' is denied.",
            ],
            1);

        Assert.Equal(OperationVeredict.AutoRetry, result);
        Assert.True(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void GetResult_LocalizedDeleteFailureStillRetriesAsAdministrator()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Uninstall,
            [
                "Uninstall-PSResource: Parent directory 'C:\\Program Files\\PowerShell\\Modules\\Devolutions.PowerShell\\2026.2.4' could not be deleted: Der Zugriff auf den Pfad wurde verweigert.",
            ],
            1);

        Assert.Equal(OperationVeredict.AutoRetry, result);
    }

    [Fact]
    public void GetResult_CurrentUserDeleteFailureDoesNotElevate()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Uninstall,
            [
                "Uninstall-PSResource: Parent directory 'C:\\Users\\me\\Documents\\PowerShell\\Modules\\Devolutions.PowerShell\\2026.2.4' could not be deleted: The process cannot access the file because it is being used by another process.",
            ],
            1);

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.NotEqual(true, package.OverridenOptions.RunAsAdministrator);
    }

    [Theory]
    [InlineData("Uninstall-PSResource: Parent directory 'C:\\Program Files\\PowerShell\\Modules\\Devolutions.PowerShell' could not be deleted: Access to the path 'C:\\Program Files\\PowerShell\\Modules\\Devolutions.PowerShell' is denied.")]
    [InlineData("Uninstall-PSResource: Script metadata file 'C:\\Program Files\\PowerShell\\Scripts\\InstalledScriptInfos\\Devolutions.PowerShell_InstalledScriptInfo.xml' could not be deleted: Access to the path is denied.")]
    public void GetResult_LeftoverOnlyAfterRemovingTheVersionIsSuccess(string output)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(package, OperationType.Uninstall, [output], 1);

        Assert.Equal(OperationVeredict.Success, result);
        Assert.NotEqual(true, package.OverridenOptions.RunAsAdministrator);
    }

    [Theory]
    [InlineData("-Scop")]
    [InlineData("-sc")]
    [InlineData("-SCOPE")]
    public void GetParameters_AbbreviatedCustomScopeArgumentIsNotDuplicated(string scopeArgument)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var options = new InstallOptions { CustomParameters_Update = [scopeArgument, "AllUsers"] };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        Assert.DoesNotContain("-Scope", parameters);
        Assert.DoesNotContain("CurrentUser", parameters);
        Assert.Contains(scopeArgument, parameters);
    }

    [Fact]
    public void GetParameters_SkipDependencyCheckIsNotTakenForScope()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));

        var options = new InstallOptions { CustomParameters_Uninstall = ["-SkipDependencyCheck"] };
        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);

        Assert.Contains("-Scope", parameters);
        Assert.Contains("AllUsers", parameters);
    }

    [Fact]
    public void GetResult_AccessDeniedUninstallAlreadyElevatedFails()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "Devolutions.PowerShell\t2026.2.4\tPSGallery"], manager));
        package.OverridenOptions.RunAsAdministrator = true;

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Uninstall,
            ["Parent directory 'x' could not be deleted: Access to the path 'x' is denied."],
            1);

        Assert.Equal(OperationVeredict.Failure, result);
    }

    private static readonly string[] ReporterUpdateOutput =
    [
        "\u001b[31;1mUpdate-PSResource: \u001b[0mC:\\Users\\Admin\\AppData\\Local\\Programs\\WingetUI\\Assets\\Utilities\\unigetui_ps_operation.ps1:112\u001b[0m",
        "\u001b[31;1m\u001b[0m\u001b[36;1m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[31;1m\u001b[31;1m\u001b[36;1m     | \u001b[31;1mUnable to successfully install package 'PowerShellGet': 'Access to the path 'C:\\Program\u001b[0m",
        "\u001b[31;1m\u001b[0m\u001b[36;1m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[31;1m\u001b[31;1m\u001b[36;1m\u001b[31;1m\u001b[36;1m     | \u001b[31;1mFiles\\PowerShell\\Modules\\PowerShellGet\\3.0.23' is denied.'\u001b[0m",
        "\u001b[31;1m\u001b[0m\u001b[36;1m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[0m\u001b[36;1m\u001b[31;1m\u001b[31;1m\u001b[36;1m     | \u001b[31;1mPackage(s) 'ExchangeOnlineManagement' could not be installed from repository 'PSGallery'.\u001b[0m",
    ];

    private static readonly string[] AllUsersUpdateDeniedOutput =
        [.. ReporterUpdateOutput, PowerShell7PkgOperationHelper.AccessDeniedMarker];

    private static readonly string[] AllUsersInstallDeniedOutput =
    [
        "Install-PSResource: C:\\Users\\me\\AppData\\LocalLow\\ugtest5755\\unigetui_ps_operation.ps1:131",
        "Line |",
        " 131 |      & $command @named @rest",
        "     |      ~~~~~~~~~~~~~~~~~~~~~~~",
        "     | Unable to successfully install package 'PackageManagement': 'Access to the path 'C:\\Program",
        "     | Files\\PowerShell\\Modules\\PackageManagement' is denied.'",
        "",
        "Install-PSResource: C:\\Users\\me\\AppData\\LocalLow\\ugtest5755\\unigetui_ps_operation.ps1:131",
        "Line |",
        " 131 |      & $command @named @rest",
        "     |      ~~~~~~~~~~~~~~~~~~~~~~~",
        "     | Operation is not valid due to the current state of the object.",
        "",
        "Install-PSResource: C:\\Users\\me\\AppData\\LocalLow\\ugtest5755\\unigetui_ps_operation.ps1:131",
        "Line |",
        " 131 |      & $command @named @rest",
        "     |      ~~~~~~~~~~~~~~~~~~~~~~~",
        "     | Package(s) 'ExchangeOnlineManagement' could not be installed from repository 'PSGallery'.",
        "",
        "UniGetUI: the operation was denied access to a path.",
    ];

    private static readonly string[] AllUsersInstallLockedOutput =
    [
        "Install-PSResource: C:\\App\\Assets\\Utilities\\unigetui_ps_operation.ps1:131",
        "     | Unable to successfully install package 'ExchangeOnlineManagement': 'The process cannot access the file",
        "     | 'C:\\Program Files\\PowerShell\\Modules\\ExchangeOnlineManagement\\3.11.1\\Microsoft.Exchange.Management.dll' because it is being used by another process.'",
        "     | Package(s) 'ExchangeOnlineManagement' could not be installed from repository 'PSGallery'.",
    ];

    public static TheoryData<string[], OperationType> DeniedAllUsersOutputs =>
        new()
        {
            { AllUsersUpdateDeniedOutput, OperationType.Update },
            { AllUsersInstallDeniedOutput, OperationType.Install },
        };

    public static TheoryData<string[]> NonPermissionAllUsersFailures =>
        new()
        {
            ReporterUpdateOutput,
            AllUsersInstallLockedOutput,
        };

    [Theory]
    [MemberData(nameof(NonPermissionAllUsersFailures))]
    public void GetResult_InstallFailureWithoutAccessDeniedMarkerDoesNotElevate(string[] output)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(package, OperationType.Update, output, 1, new InstallOptions());

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.NotEqual(true, package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void GetResult_AllUsersUpdateDeniedWithRunAsAdministratorOptionDoesNotRetry()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(
            package, OperationType.Update, AllUsersUpdateDeniedOutput, 1, new InstallOptions { RunAsAdministrator = true });

        Assert.Equal(OperationVeredict.Failure, result);
    }

    [Fact]
    public void GetResult_AllUsersUpdateDeniedInElevatedProcessDoesNotRetry()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));
        PowerShell7PkgOperationHelper.ProcessIsElevated = static () => true;

        var result = manager.OperationHelper.GetResult(
            package, OperationType.Update, AllUsersUpdateDeniedOutput, 1, new InstallOptions());

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.NotEqual(true, package.OverridenOptions.RunAsAdministrator);
    }

    [Theory]
    [MemberData(nameof(DeniedAllUsersOutputs))]
    public void GetResult_AllUsersWriteDeniedRetriesAsAdministrator(string[] output, OperationType operation)
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(package, operation, output, 1, new InstallOptions());

        Assert.Equal(OperationVeredict.AutoRetry, result);
        Assert.True(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void AllUsersUpdateDenied_RetryIsLaunchedThroughTheElevator()
    {
        if (CoreTools.IsAdministrator())
            return;

        string previousElevator = CoreData.ElevatorPath;
        string previousElevatorArgs = CoreData.ElevatorArgs;
        CoreData.ElevatorPath = @"C:\App\Assets\Utilities\UniGetUI Elevator.exe";
        CoreData.ElevatorArgs = "";

        try
        {
            var manager = new PowerShell7
            {
                Status = new ManagerStatus
                {
                    Found = true,
                    ExecutablePath = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    ExecutableCallArgs = "",
                    OperationCallArgs =
                    [
                        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1", "plain",
                    ],
                },
            };
            var package = Assert.Single(PowerShell7.ParseInstalledPackages(
                ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));
            using var operation = new InspectableUpdate(package, new InstallOptions());

            var firstAttempt = operation.PrepareProcessStartInfoForTests();
            Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", firstAttempt.FileName);
            Assert.Contains("AllUsers", firstAttempt.ArgumentList);

            var result = manager.OperationHelper.GetResult(
                package, OperationType.Update, AllUsersUpdateDeniedOutput, 1, operation.Options);
            Assert.Equal(OperationVeredict.AutoRetry, result);

            var retry = operation.PrepareProcessStartInfoForTests();
            Assert.Equal(CoreData.ElevatorPath, retry.FileName);
            Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", retry.ArgumentList[0]);
            Assert.Contains("Update-PSResource", retry.ArgumentList);
            Assert.Contains("AllUsers", retry.ArgumentList);
        }
        finally
        {
            CoreData.ElevatorPath = previousElevator;
            CoreData.ElevatorArgs = previousElevatorArgs;
        }
    }

    private sealed class InspectableUpdate : UpdatePackageOperation
    {
        public InspectableUpdate(IPackage package, InstallOptions options)
            : base(package, options, true) { }

        public ProcessStartInfo PrepareProcessStartInfoForTests()
        {
            process.StartInfo.FileName = "unset";
            process.StartInfo.Arguments = "unset";
            process.StartInfo.ArgumentList.Clear();
            PrepareProcessStartInfo();
            return process.StartInfo;
        }
    }

    [Fact]
    public void GetResult_InstallFailureForCurrentUserDoesNotElevate()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:CurrentUser##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));

        var result = manager.OperationHelper.GetResult(
            package, OperationType.Update, AllUsersUpdateDeniedOutput, 1, new InstallOptions());

        Assert.Equal(OperationVeredict.Failure, result);
        Assert.NotEqual(true, package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void GetResult_AllUsersUpdateDeniedAlreadyElevatedFails()
    {
        var manager = new PowerShell7();
        var package = Assert.Single(PowerShell7.ParseInstalledPackages(
            ["##SCOPE:AllUsers##", "ExchangeOnlineManagement\t3.10.1\tPSGallery"], manager));
        package.OverridenOptions.RunAsAdministrator = true;

        var result = manager.OperationHelper.GetResult(
            package, OperationType.Update, AllUsersUpdateDeniedOutput, 1, new InstallOptions());

        Assert.Equal(OperationVeredict.Failure, result);
    }

    [Theory]
    [InlineData(new[] { "-Scope", "AllUsers" }, true)]
    [InlineData(new[] { "-Scope:AllUsers" }, true)]
    [InlineData(new[] { "-sc", "allusers" }, true)]
    [InlineData(new[] { "-Scope", "CurrentUser" }, false)]
    public void GetResult_CustomScopeArgumentDecidesElevationOnInstall(string[] custom, bool elevates)
    {
        var manager = new PowerShell7();
        var package = new Package(
            "ExchangeOnlineManagement", "ExchangeOnlineManagement", "3.11.1",
            manager.DefaultSource, manager);
        var options = new InstallOptions
        {
            InstallationScope = elevates ? PackageScope.Local : PackageScope.Global,
            CustomParameters_Install = [.. custom],
        };

        var result = manager.OperationHelper.GetResult(
            package, OperationType.Install, AllUsersUpdateDeniedOutput, 1, options);

        Assert.Equal(elevates ? OperationVeredict.AutoRetry : OperationVeredict.Failure, result);
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

    // For a module installed in both scopes, the resolved update scope must track the same
    // enumerated package as the installed version (last-wins), so the two never disagree.
    // Independent per-scope updates aren't representable in the scope-blind upgrade loader.
    [Fact]
    public void BuildInstalledScopeMap_TracksSameEnumeratedPackageAsVersion()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.1.0\tPSGallery",
                "##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2025.2.0\tPSGallery",
            ], manager);

        var idScope = BaseNuGet.BuildInstalledScopeMap(installed);

        // CurrentUser is enumerated last, so both the version (2025.2.0) and the scope resolve to it
        Assert.Equal(PackageScope.User, idScope["devolutions.powershell"]);
        Assert.Equal("2025.2.0", installed[^1].VersionString);
        Assert.Equal(PackageScope.User, installed[^1].OverridenOptions.Scope);
    }

    // Update-PSResource installs side by side and Get-InstalledPSResource lists the versions
    // sorted as strings, so the last enumerated copy is not the newest. The update must start
    // from the newest copy, or it keeps reporting the same stale installed version after
    // every successful update.
    [Fact]
    public void SelectNewestInstalled_PicksHighestVersionNotLastEnumerated()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##",
                "##SCOPE:CurrentUser##",
                "XKCD\t1.9.0\tPSGallery",
                "XKCD\t1.8.0\tPSGallery",
                "XKCD\t1.12.6\tPSGallery",
                "XKCD\t1.11.0\tPSGallery",
                "XKCD\t1.10.1\tPSGallery",
            ], manager);

        var newest = Assert.Single(manager.SelectNewestInstalled(installed));
        Assert.Equal("xkcd", newest.Key);
        Assert.Equal("1.12.6", newest.Value.VersionString);

        var xml = "<entry><d:Id>XKCD</d:Id><d:Version>1.12.7</d:Version></entry>";
        var update = Assert.Single(BaseNuGet.ParseUpdatesResponse(
            xml,
            new Dictionary<string, string> { [newest.Key] = newest.Value.VersionString },
            BaseNuGet.BuildInstalledScopeMap([newest.Value]),
            installed[0].Source,
            manager));

        Assert.Equal("1.12.6", update.VersionString);
        Assert.Equal("1.12.7", update.NewVersionString);
        Assert.Equal(PackageScope.User, update.OverridenOptions.Scope);
    }

    [Fact]
    public void SelectNewestInstalled_ScopeFollowsNewestCopy()
    {
        var manager = new PowerShell7();
        var installed = PowerShell7.ParseInstalledPackages(
            [
                "##SCOPE:AllUsers##", "Devolutions.PowerShell\t2025.2.0\tPSGallery",
                "##SCOPE:CurrentUser##", "Devolutions.PowerShell\t2025.1.0\tPSGallery",
            ], manager);

        var newest = manager.SelectNewestInstalled(installed);
        var idScope = BaseNuGet.BuildInstalledScopeMap(newest.Values);

        Assert.Equal("2025.2.0", newest["devolutions.powershell"].VersionString);
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
