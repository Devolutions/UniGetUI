using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>"Reset WinGet" (desktop <c>AvaloniaPackageOperationHelper.HandleBrokenWinGetAsync</c>). Windows only.</summary>
internal static class TuiWinGetRepair
{
    public static async Task RepairAsync()
    {
        if (!OperatingSystem.IsWindows() || TuiEngine.IsFakeData) return;
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CoreData.PowerShell5,
                    Arguments =
                        "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {"
                        + "cmd.exe /C \"rmdir /Q /S `\"%temp%\\WinGet`\"\"; "
                        + "cmd.exe /C \"`\"%localappdata%\\Microsoft\\WindowsApps\\winget.exe`\" source reset --force\"; "
                        + "taskkill /im winget.exe /f; "
                        + "taskkill /im WindowsPackageManagerServer.exe /f; "
                        + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force; "
                        + "Install-Module Microsoft.WinGet.Client -Force -AllowClobber; "
                        + "Import-Module Microsoft.WinGet.Client; "
                        + "Repair-WinGetPackageManager -Force -Latest; "
                        + "Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' | Reset-AppxPackage; "
                        + "}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                },
            };
            p.Start();
            await p.WaitForExitAsync();
            if (string.Equals(Settings.GetValue(Settings.K.WinGetCliToolPreference), "pinget", StringComparison.OrdinalIgnoreCase))
                Settings.SetValue(Settings.K.WinGetCliToolPreference, "default");

            TuiNotifications.Success(CoreTools.Translate("WinGet was repaired successfully"),
                CoreTools.Translate("It is recommended to restart UniGetUI after WinGet has been repaired"));
            _ = UpgradablePackagesLoader.Instance.ReloadPackages();
            _ = InstalledPackagesLoader.Instance.ReloadPackages();
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while trying to repair WinGet");
            Logger.Error(ex);
            TuiNotifications.Error(CoreTools.Translate("WinGet could not be repaired"),
                CoreTools.Translate("An unexpected issue occurred while attempting to repair WinGet. Please try again later") + " — " + ex.Message);
        }
    }
}
