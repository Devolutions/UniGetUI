#if WINDOWS
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// The shell-interpreted managers emit different parameters depending on whether the launcher
/// argument vector is available. Both shapes are covered here: the vector form that real
/// installations use, and the concatenated -Command form kept as a fallback.
/// </summary>
public sealed class ShellManagerLaunchModeTests
{
    private static readonly string[] _launcherVector =
    [
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        @"C:\App\Assets\Utilities\unigetui_ps_operation.ps1",
        "tls12",
    ];

    private static IPackage PowerShellPackage(PowerShell manager) =>
        Assert.Single(
            PowerShell.ParseInstalledPackages(
                [
                    "Version Name Repository Description",
                    "------- ---- ---------- -----------",
                    "1.0.0 Devolutions.PowerShell PSGallery x",
                ],
                manager
            )
        );

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void WindowsPowerShell_EmitsNoScriptFragmentsWhenTheLauncherIsUsed(
        OperationType operation
    )
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = _launcherVector;
        var package = PowerShellPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            operation
        );

        Assert.DoesNotContain(parameters, parameter => parameter.Contains(';'));
        Assert.DoesNotContain(parameters, parameter => parameter.Contains("::"));
        Assert.DoesNotContain(parameters, parameter => parameter.Contains("exit("));
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void WindowsPowerShell_KeepsTheScriptFragmentsWhenFallingBackToCommand(
        OperationType operation
    )
    {
        var manager = new PowerShell();
        var package = PowerShellPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            operation
        );

        Assert.Equal(
            $";if(${PowerShellPkgOperationHelper.ErrorVariableName}){{exit(1)}}",
            parameters[^1]
        );

        if (operation is not OperationType.Uninstall)
            Assert.Equal(
                "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;",
                parameters[0]
            );
    }

    [Fact]
    public void WindowsPowerShell_StillBindsTheErrorVariableWhenTheLauncherIsUsed()
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = _launcherVector;
        var package = PowerShellPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        int index = parameters.ToList().IndexOf("-ErrorVariable");
        Assert.True(index >= 0);
        Assert.Equal(PowerShellPkgOperationHelper.ErrorVariableName, parameters[index + 1]);
    }

    [Fact]
    public void Npm_EmitsAnUnquotedSpecWhenTheLauncherIsUsed()
    {
        var manager = new Npm();
        manager.Status.OperationCallArgs =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            @"C:\Program Files\nodejs\npm.ps1",
        ];
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("@babel/core")
            .WithVersion("7.24.0")
            .Build();

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.Contains("@babel/core@7.24.0", parameters);
        Assert.DoesNotContain("'@babel/core@7.24.0'", parameters);
    }

    // The specifier is never quoted now: the exported install script runs commands through cmd,
    // where single quotes are not delimiters and npm received them as part of the package name.
    [Fact]
    public void Npm_NeverQuotesTheSpecInEitherLaunchMode()
    {
        var withLauncher = new Npm();
        withLauncher.Status.OperationCallArgs =
        [
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            @"C:\Program Files\nodejs\npm.ps1",
        ];
        var withoutLauncher = new Npm();

        foreach (var manager in new[] { withLauncher, withoutLauncher })
        {
            var package = new PackageBuilder()
                .WithManager(manager)
                .WithId("@babel/core")
                .WithVersion("7.24.0")
                .Build();

            var parameters = manager.OperationHelper.GetParameters(
                package,
                new InstallOptions(),
                OperationType.Install
            );

            Assert.Contains("@babel/core@7.24.0", parameters);
            Assert.DoesNotContain(parameters, parameter => parameter.Contains('\''));
        }
    }

    // "Latest" is more than one word in several languages, and an unpinned imported package falls
    // back to it. Unquoted that would split, and npm would try to install the trailing word as a
    // package of its own, so it has to be refused instead.
    [Theory]
    [InlineData("Piu recente")]
    [InlineData("Meest recent")]
    [InlineData("En son")]
    public void Npm_RefusesAVersionThatWouldSplitIntoASecondArgument(string version)
    {
        var manager = new Npm();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("express")
            .WithVersion(version)
            .Build();

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetParameters(
                    package,
                    new InstallOptions(),
                    OperationType.Install
                )
        );
    }

    [Fact]
    public void BothLaunchModesStillRejectAnInjectedVersion()
    {
        var withLauncher = new PowerShell();
        withLauncher.Status.OperationCallArgs = _launcherVector;
        var withoutLauncher = new PowerShell();

        foreach (var manager in new[] { withLauncher, withoutLauncher })
        {
            var package = PowerShellPackage(manager);
            var options = new InstallOptions { Version = "1.0.0; Start-Process calc" };

            Assert.Throws<InvalidOperationException>(
                () =>
                    manager.OperationHelper.GetParameters(
                        package,
                        options,
                        OperationType.Install
                    )
            );
        }
    }

    // The preview, the manual-install action and the exported script all run the command without
    // UniGetUI's launcher, so the fragments the launcher owns have to be back in the parameters.
    // Without them a PowerShellGet failure exits 0 and the exported script reports success.
    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    [InlineData(OperationType.Uninstall)]
    public void WindowsPowerShell_StandaloneParametersCarryTheScriptFragments(
        OperationType operation
    )
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = _launcherVector;
        var package = PowerShellPackage(manager);

        var parameters = manager.OperationHelper.GetStandaloneParameters(
            package,
            new InstallOptions(),
            operation
        );

        Assert.Equal(
            $";if(${PowerShellPkgOperationHelper.ErrorVariableName}){{exit(1)}}",
            parameters[^1]
        );

        if (operation is not OperationType.Uninstall)
            Assert.Equal(
                "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;",
                parameters[0]
            );
    }

    [Fact]
    public void WindowsPowerShell_TheLaunchedParametersStillOmitThemWhenTheLauncherIsUsed()
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = _launcherVector;
        var package = PowerShellPackage(manager);

        var parameters = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions(),
            OperationType.Install
        );

        Assert.DoesNotContain(parameters, parameter => parameter.Contains(';'));
    }

    [Fact]
    public void StandaloneParametersStillRejectAnInjectedVersion()
    {
        var manager = new PowerShell();
        manager.Status.OperationCallArgs = _launcherVector;
        var package = PowerShellPackage(manager);
        var options = new InstallOptions { Version = "1.0.0; Start-Process calc" };

        Assert.Throws<InvalidOperationException>(
            () =>
                manager.OperationHelper.GetStandaloneParameters(
                    package,
                    options,
                    OperationType.Install
                )
        );
    }

    [Fact]
    public void DirectExecManagers_ProduceTheSameParametersEitherWay()
    {
        var manager = new UniGetUI.PackageEngine.Managers.ChocolateyManager.Chocolatey();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Test")
            .WithVersion("1.0.0")
            .Build();
        var options = new InstallOptions { Version = "1.2.3" };

        Assert.Equal(
            manager.OperationHelper.GetParameters(package, options, OperationType.Install),
            manager.OperationHelper.GetStandaloneParameters(package, options, OperationType.Install)
        );
    }
}
#endif
