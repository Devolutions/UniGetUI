using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Changes a secure setting. On Windows the secure settings live under Program Files, so the desktop
/// app relaunches itself elevated with <c>--enable-secure-setting-for-user</c>; the TUI does the same
/// with its own executable. In fake-data mode the store is the sandbox and is written directly.
/// </summary>
internal static class TuiSecureSettings
{
    public static async Task<bool> TrySetAsync(SecureSettings.K key, bool enabled)
    {
        string setting = SecureSettings.ResolveKey(key);
        string user = CoreTools.MakeValidFileName(Environment.UserName);
        if (TuiEngine.IsFakeData || !OperatingSystem.IsWindows())
            return SecureSettings.ApplyForUser(user, setting, enabled) is 0;

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    FileName = Environment.ProcessPath ?? "UniGetUI.Tui.exe",
                    Verb = "runas",
                    ArgumentList =
                    {
                        enabled ? SecureSettings.Args.ENABLE_FOR_USER : SecureSettings.Args.DISABLE_FOR_USER,
                        user,
                        setting,
                    },
                },
            };
            p.Start();
            await p.WaitForExitAsync();
            // The elevated child wrote the file; drop our cached value so the next read sees it.
            SecureSettings.ApplyForUser(user, setting, enabled);
            return p.ExitCode is 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }
}
