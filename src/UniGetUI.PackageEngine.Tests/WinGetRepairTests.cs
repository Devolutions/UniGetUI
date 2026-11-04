#if WINDOWS
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(WinGetManagerTestCollection.Name)]
public sealed class WinGetRepairTests
{
    [Theory]
    [InlineData("pinget", 23)]
    [InlineData("custom-cli", 23)]
    [InlineData("pinget", -1)]
    public async Task FailedExitThrowsAndKeepsPreference(string preference, int exitCode)
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, preference);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WinGetRepair.RunAsync(() => Task.FromResult(exitCode))
            );

            Assert.Equal($"WinGet repair exited with code {exitCode}.", error.Message);
            Assert.Equal(preference, Settings.GetValue(Settings.K.WinGetCliToolPreference));
        }
        finally
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }

    [Fact]
    public async Task RunnerFailurePropagatesAndKeepsPreference()
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, "pinget");

            await Assert.ThrowsAsync<IOException>(() =>
                WinGetRepair.RunAsync(() => Task.FromException<int>(new IOException("runner failed")))
            );

            Assert.Equal("pinget", Settings.GetValue(Settings.K.WinGetCliToolPreference));
        }
        finally
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }

    [Theory]
    [InlineData("pinget", "default")]
    [InlineData("PiNgEt", "default")]
    [InlineData("custom-cli", "custom-cli")]
    [InlineData("", "")]
    public async Task SuccessfulExitResetsOnlyAPingetPreference(string preference, string expected)
    {
        string old = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        int calls = 0;
        try
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, preference);

            await WinGetRepair.RunAsync(() =>
            {
                calls++;
                return Task.FromResult(0);
            });

            Assert.Equal(1, calls);
            Assert.Equal(expected, Settings.GetValue(Settings.K.WinGetCliToolPreference));
        }
        finally
        {
            Settings.SetValue(Settings.K.WinGetCliToolPreference, old);
        }
    }

    [Fact]
    public void RepairScriptTurnsCmdletErrorsIntoAFailedExitCode()
    {
        Assert.DoesNotContain("ErrorActionPreference", WinGetRepair.Arguments);
        Assert.Contains("-ErrorVariable +repairErrors", WinGetRepair.Arguments);
        Assert.Contains("if ($sourceResetFailed -or $repairErrors) { exit 1 }", WinGetRepair.Arguments);
    }

    [Fact]
    public void ExecutableActionsAreHiddenUntilTheManagerIsFound()
    {
        var actions = new UniGetUI.PackageEngine.Managers.ChocolateyManager.Chocolatey().GetMaintenanceActions();

        Assert.DoesNotContain(actions, action => action.Id == "clear-cache");
        Assert.Contains(actions, action => action.Id == "reload");
    }

    [Fact]
    public void WinGetOffersTheSharedRepairAction()
    {
        var actions = new WinGet().GetMaintenanceActions();

        Assert.Contains(actions, action => action.Id == "repair-winget" && action.RequiresConfirmation);
        Assert.Contains(actions, action => action.Id == "reset-sources" && action.RequiresElevation);
        Assert.Contains(actions, action => action.Id == "reload" && !action.RequiresConfirmation);
        Assert.Contains(actions, action => action.Id == "stop-stuck-listings");
    }
}
#endif
