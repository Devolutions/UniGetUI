using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiWinGetRepair
{
    public static Task RepairAsync()
    {
        if (!OperatingSystem.IsWindows() || TuiEngine.IsFakeData) return Task.CompletedTask;
        var winget = TuiEngine.FindManager("WinGet");
        var repair = winget?.FindAction("repair-winget");
        if (winget is null || repair is null) return Task.CompletedTask;
        return RepairAsync(() => winget.RunAsync(repair), () =>
        {
            _ = InstalledPackagesLoader.Instance.ReloadPackages();
            _ = UpgradablePackagesLoader.Instance.ReloadPackages();
        });
    }

    internal static async Task<bool> RepairAsync(Func<Task> runRepair, Action reloadPackages)
    {
        try
        {
            await runRepair();
            TuiNotifications.Success(CoreTools.Translate("WinGet was repaired successfully"),
                CoreTools.Translate("It is recommended to restart UniGetUI after WinGet has been repaired"));
            reloadPackages();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while trying to repair WinGet");
            Logger.Error(ex);
            TuiNotifications.Error(CoreTools.Translate("WinGet could not be repaired"),
                CoreTools.Translate("An unexpected issue occurred while attempting to repair WinGet. Please try again later") + " — " + ex.Message);
            return false;
        }
    }
}
