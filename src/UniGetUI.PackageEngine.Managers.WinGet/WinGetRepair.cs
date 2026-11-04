using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.WingetManager
{
    public static class WinGetRepair
    {
        internal const string Arguments =
            "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {"
            + "cmd.exe /C \"rmdir /Q /S `\"%temp%\\WinGet`\"\"; "
            + "cmd.exe /C \"`\"%localappdata%\\Microsoft\\WindowsApps\\winget.exe`\" source reset --force\"; "
            + "$sourceResetFailed = $LASTEXITCODE -ne 0; "
            + "taskkill /im winget.exe /f; "
            + "taskkill /im WindowsPackageManagerServer.exe /f; "
            + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -ErrorVariable +repairErrors; "
            + "Install-Module Microsoft.WinGet.Client -Force -AllowClobber -ErrorVariable +repairErrors; "
            + "Import-Module Microsoft.WinGet.Client -ErrorVariable +repairErrors; "
            + "Repair-WinGetPackageManager -Force -Latest -ErrorVariable +repairErrors; "
            + "Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' | Reset-AppxPackage -ErrorVariable +repairErrors; "
            + "if ($sourceResetFailed -or $repairErrors) { exit 1 }; "
            + "}\"";

        public static async Task RunAsync(Func<Task<int>> runRepair)
        {
            int exitCode = await runRepair();
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    CoreTools.Translate("WinGet repair exited with code {0}.", exitCode)
                );
            }

            if (
                string.Equals(
                    Settings.GetValue(Settings.K.WinGetCliToolPreference),
                    "pinget",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                Settings.SetValue(Settings.K.WinGetCliToolPreference, "default");
            }
        }
    }
}
