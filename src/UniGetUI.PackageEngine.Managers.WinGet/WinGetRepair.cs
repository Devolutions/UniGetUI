using System.Diagnostics;
using UniGetUI.Core.Data;
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
            + "taskkill /im winget.exe /f; "
            + "taskkill /im WindowsPackageManagerServer.exe /f; "
            + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force; "
            + "Install-Module Microsoft.WinGet.Client -Force -AllowClobber; "
            + "Import-Module Microsoft.WinGet.Client; "
            + "Repair-WinGetPackageManager -Force -Latest; "
            + "Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' | Reset-AppxPackage; "
            + "}\"";

        public static async Task RunAsync(Func<Task<int>>? runRepair = null)
        {
            int exitCode = await (runRepair ?? RunRepairProcessAsync)();
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

        private static async Task<int> RunRepairProcessAsync()
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("WinGet can only be repaired on Windows.");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CoreData.PowerShell5,
                    Arguments = Arguments,
                    UseShellExecute = true,
                    Verb = "runas",
                },
            };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}
