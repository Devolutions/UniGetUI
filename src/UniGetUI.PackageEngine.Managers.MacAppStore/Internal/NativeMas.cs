using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

internal static class NativeMas
{
    internal const int Unavailable = 77;
    internal const int SigningRequired = 78;
    internal const int Canceled = 128;
    internal const int CancellationResult = -128;
    internal static Func<string?> HelperPath { get; set; } = FindHelper;

    private static string? FindHelper()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(13))
            return null;
        string path = Path.Join(CoreData.UniGetUIExecutableDirectory, "UniGetUIMasHelper");
        return File.Exists(path) ? path : null;
    }

    internal static ProcessStartInfo? StartInfo(string masPath, IReadOnlyList<string> arguments, string prompt)
    {
        // Custom command-line switches stay on the password/sudo path, not the narrow helper protocol.
        if (arguments.Count != 2 || HelperPath() is not { } helper)
            return null;
        ProcessStartInfo start = new(helper)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "run", masPath, arguments[0], arguments[1], prompt })
            start.ArgumentList.Add(argument);
        return start;
    }

    public static async Task<string> ConfigureAsync(bool enable)
    {
        if (enable && Settings.Get(Settings.K.ProhibitElevation))
            throw new InvalidOperationException(CoreTools.Translate(
                "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings."));
        string path = HelperPath() ?? throw new InvalidOperationException(CoreTools.Translate(
            "Native Touch ID requires macOS 13 or later and the bundled UniGetUI helper."));
        ProcessStartInfo start = new(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        start.ArgumentList.Add(enable ? "enable" : "disable");
        using Process process = Process.Start(start) ?? throw new IOException("Could not start the Mac App Store helper.");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout + "\n" + await stderr).Trim();
        if (output.Length > 0)
            Logger.Info(output);
        return process.ExitCode switch
        {
            0 => enable ? CoreTools.Translate("Native Touch ID authorization is enabled.")
                : CoreTools.Translate("Native Touch ID authorization is disabled."),
            Unavailable => CoreTools.Translate("Approve the UniGetUI helper in System Settings > General > Login Items & Extensions, then retry the operation."),
            SigningRequired => throw new InvalidOperationException(CoreTools.Translate(
                "Native Touch ID requires a UniGetUI build signed with an Apple-issued certificate. This ad-hoc development build can use password authorization instead.")),
            _ => throw new InvalidOperationException(CoreTools.Translate("Could not configure native Touch ID authorization.") + "\n" + output),
        };
    }
}
