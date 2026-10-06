using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Changes a secure setting. On Windows the secure settings live under Program Files, so the desktop
/// app relaunches itself elevated with <c>--enable-secure-setting-for-user</c>; the TUI reuses that
/// shared command and cache invalidation. In fake-data mode the sandbox store is written directly.
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
            return await SecureSettings.TrySet(key, enabled);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return false;
        }
    }
}
