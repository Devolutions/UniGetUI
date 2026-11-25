#if WINDOWS
using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PowerShellManagerTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void InstalledListingRetainsRowsButReportsNonTerminatingErrors(bool emitError, int expectedExit)
    {
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
            "function Get-InstalledModule {"
            + " [pscustomobject]@{Name='Before';Version='1.0';Repository='PSGallery'};"
            + (emitError ? " Write-Error 'unreadable module';" : "")
            + " [pscustomobject]@{Name='After';Version='1.0';Repository='PSGallery'} };"
            + PowerShell.InstalledListingCommand
        );
        process.Start();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(expectedExit, process.ExitCode);
        Assert.Equal(emitError, stderr.GetAwaiter().GetResult().Contains("unreadable module"));
        Assert.Equal(["Before", "After"],
            PowerShell.ParseInstalledPackages(output.Split('\n'), new PowerShell()).Select(package => package.Id));
    }

    [Theory]
    [InlineData("$InstalledDate = $null", 0)]
    [InlineData("$UpdatedDate = $null", 0)]
    [InlineData("$InstalledDate = 'not a date'", 1)]
    public void InstalledListingToleratesOnlyPowerShellGetDateResets(string statement, int expectedExit)
    {
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
            "function New-PSGetItemInfo { param([string]$Name, [DateTime]$InstalledDate, [DateTime]$UpdatedDate)"
            + " $InstalledDate = Get-Date; $UpdatedDate = Get-Date;\n"
            + "                " + statement + "\n"
            + " [pscustomobject]@{Name=$Name;Version='1.0';Repository='PSGallery'} };"
            + " function Get-InstalledModule { New-PSGetItemInfo -Name 'Before'; New-PSGetItemInfo -Name 'After' };"
            + PowerShell.InstalledListingCommand
        );
        process.Start();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(expectedExit, process.ExitCode);
        Assert.Contains("ArgumentTransformationMetadataException", stderr.GetAwaiter().GetResult());
        Assert.Equal(["Before", "After"],
            PowerShell.ParseInstalledPackages(output.Split('\n'), new PowerShell()).Select(package => package.Id));
    }

    [Fact]
    public void InstalledListingAcceptsNoInstalledModules()
    {
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
        process.StartInfo.ArgumentList.Add("function Get-InstalledModule {};" + PowerShell.InstalledListingCommand);
        process.Start();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.Equal("", process.StandardOutput.ReadToEnd());
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stderr.GetAwaiter().GetResult());
    }

    [PowerShellModuleFact]
    public async Task InstalledListingIgnoresHandledErrorsFromModuleAutoload()
    {
        DirectoryInfo moduleRoot = Directory.CreateTempSubdirectory("unigetui-powershell-");
        DirectoryInfo moduleDirectory = moduleRoot.CreateSubdirectory("UniGetUITestModules");
        string modulePath = Path.Combine(moduleDirectory.FullName, "UniGetUITestModules.psm1");
        try
        {
            File.WriteAllText(
                modulePath,
                """
                try { throw 'Optional module initialization failed' } catch {}
                function Get-InstalledModule {
                    [pscustomobject]@{Name='Fixture'; Version='1.2.3'; Repository='Local'}
                }
                Export-ModuleMember -Function Get-InstalledModule
                """
            );

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
            process.StartInfo.Environment["UNIGETUI_TEST_MODULE_PATH"] = moduleRoot.FullName;
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(
                "$env:PSModulePath = $env:UNIGETUI_TEST_MODULE_PATH; " + PowerShell.InstalledListingCommand
            );
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill();
            }

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("Fixture\t1.2.3\tLocal", (await stdout).Trim());
            Assert.Equal("", await stderr);
        }
        finally
        {
            File.Delete(modulePath);
            moduleDirectory.Delete();
            moduleRoot.Delete();
        }
    }

    private sealed class PowerShellModuleFactAttribute : FactAttribute
    {
        public PowerShellModuleFactAttribute()
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$env:PSModulePath = Join-Path $PSHOME Modules; Get-ExecutionPolicy"
            );
            using Process? process = Process.Start(startInfo);
            if (process is not null
                && process.WaitForExit(10000)
                && process.ExitCode == 0
                && process.StandardOutput.ReadToEnd().Trim() is "Restricted" or "AllSigned")
                Skip = "The active Windows PowerShell policy disallows the unsigned local test module.";
        }
    }

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

    [Fact]
    public void GetParameters_UpdateKeepsUpdateModuleWhenIntegrityChecksAreNotSkipped()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Update
        );

        Assert.Contains("Update-Module", parameters);
        Assert.DoesNotContain("Install-Module", parameters);
        Assert.DoesNotContain("-SkipPublisherCheck", parameters);
    }

    [Fact]
    public void GetParameters_UpdateRunsThroughInstallModuleWhenIntegrityChecksAreSkipped()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        Assert.Contains("Install-Module", parameters);
        Assert.DoesNotContain("Update-Module", parameters);
        Assert.Contains("-SkipPublisherCheck", parameters);
        Assert.Contains("-Name", parameters);
        Assert.Contains(package.Id, parameters);
    }

    [Fact]
    public void GetParameters_UpdateThroughInstallModuleCarriesTheSelectedScope()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions
        {
            SkipHashCheck = true,
            InstallationScope = PackageScope.Machine,
        };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        Assert.Contains("-Scope", parameters);
        Assert.Contains("AllUsers", parameters);
    }

    [Fact]
    public void GetParameters_UpdateThroughInstallModuleOmitsScopeWhenNoneIsKnown()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        Assert.DoesNotContain("-Scope", parameters);
        Assert.DoesNotContain("CurrentUser", parameters);
    }

    [Fact]
    public void GetParameters_UpdateThroughInstallModuleKeepsTheInstalledRepository()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        int repositoryIndex = parameters.ToList().IndexOf("-Repository");
        Assert.NotEqual(-1, repositoryIndex);
        Assert.Contains(package.Source.Name, parameters[repositoryIndex + 1]);
    }

    [Fact]
    public void GetStandaloneParameters_UpdateThroughInstallModuleQuotesTheRepository()
    {
        var manager = new PowerShell();
        var package = Assert.Single(
            PowerShell.ParseInstalledPackages(
                ["Devolutions.PowerShell\t1.0.0\tInternal Modules"],
                manager
            )
        );

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetStandaloneParameters(
            package,
            options,
            OperationType.Update
        );

        int repositoryIndex = parameters.ToList().IndexOf("-Repository");
        Assert.NotEqual(-1, repositoryIndex);
        Assert.Equal("'Internal Modules'", parameters[repositoryIndex + 1]);
    }

    [Fact]
    public void GetParameters_UpdateThroughInstallModuleKeepsTheRepositoryRawForTheArgumentVector()
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = LauncherVector;

        var package = Assert.Single(
            PowerShell.ParseInstalledPackages(
                ["Devolutions.PowerShell\t1.0.0\tInternal Modules"],
                manager
            )
        );

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        int repositoryIndex = parameters.ToList().IndexOf("-Repository");
        Assert.NotEqual(-1, repositoryIndex);
        Assert.Equal("Internal Modules", parameters[repositoryIndex + 1]);
    }

    private static readonly string[] LauncherVector =
    [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1",
        "tls12",
    ];

    [Fact]
    public void GetStandaloneParameters_DoesNotRecordTheRoutingDecisionOnThePackage()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        manager.OperationHelper.GetStandaloneParameters(package, options, OperationType.Update);

        Assert.False(package.OverridenOptions.PowerShell_UpdateThroughInstall);

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.False(package.OverridenOptions.PowerShell_AllowClobber);
    }

    [Fact]
    public void GetResult_ClearsTheRoutingDecisionSoItCannotLeakIntoALaterOperation()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        manager.OperationHelper.GetParameters(package, options, OperationType.Update);
        Assert.True(package.OverridenOptions.PowerShell_UpdateThroughInstall);

        manager.OperationHelper.GetResult(package, OperationType.Update, ["done"], 0);

        Assert.False(package.OverridenOptions.PowerShell_UpdateThroughInstall);

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
    }

    [Fact]
    public void GetStandaloneParameters_UpdateThroughInstallModuleEscapesAQuotedRepository()
    {
        var manager = new PowerShell();
        var package = Assert.Single(
            PowerShell.ParseInstalledPackages(
                ["Devolutions.PowerShell\t1.0.0\tBob's Modules"],
                manager
            )
        );

        var options = new InstallOptions { SkipHashCheck = true };
        var parameters = manager.OperationHelper.GetStandaloneParameters(
            package,
            options,
            OperationType.Update
        );

        int repositoryIndex = parameters.ToList().IndexOf("-Repository");
        Assert.NotEqual(-1, repositoryIndex);
        Assert.Equal("'Bob''s Modules'", parameters[repositoryIndex + 1]);
    }

    [Fact]
    public void GetParameters_UpdateThroughInstallModuleIgnoresAPinnedVersion()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true, Version = "1.0.0" };
        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        Assert.DoesNotContain("-RequiredVersion", parameters);
        Assert.DoesNotContain("1.0.0", parameters);
    }

    [Fact]
    public void GetResult_RetriesWithAllowClobberOnAnUpdateRoutedThroughInstallModule()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        var options = new InstallOptions { SkipHashCheck = true };
        manager.OperationHelper.GetParameters(package, options, OperationType.Update);

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.AutoRetry, veredict);
        Assert.True(package.OverridenOptions.PowerShell_AllowClobber);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            options,
            OperationType.Update
        );

        Assert.Contains("-AllowClobber", parameters);
    }

    [Fact]
    public void GetResult_DoesNotRetryWithAllowClobberOnAPlainUpdate()
    {
        var manager = new PowerShell();
        var package = BuildInstalledPackage(manager);

        manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Update
        );

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ClobberFailureOutput,
            1
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.False(package.OverridenOptions.PowerShell_AllowClobber);
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
