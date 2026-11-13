using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

public readonly record struct MasTouchIdStatus(bool HelperReady, bool BiometricsAvailable, bool DisabledByUser);

internal static class NativeMas
{
    internal const string InstalledHelperPath = "/Library/PrivilegedHelperTools/com.devolutions.unigetui.mashelper";
    internal const string DaemonPlistPath = "/Library/LaunchDaemons/com.devolutions.unigetui.mashelper.plist";
    internal const int Unavailable = 77;
    internal const int Canceled = 128;
    internal const int CancellationResult = -128;

    internal static Func<string?> HelperPath { get; set; } = FindHelper;
    internal static Func<string, Task<bool>> CanBootstrapWithHelper { get; set; } = CheckCanOfferTouchIdAsync;
    internal static Func<bool> HasInstalledDaemonFiles { get; set; } =
        () => File.Exists(InstalledHelperPath) || File.Exists(DaemonPlistPath);

    private static string? FindHelper()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(12))
            return null;
        string path = Path.Join(CoreData.UniGetUIExecutableDirectory, "UniGetUIMasHelper");
        return File.Exists(path) ? path : null;
    }

    internal static bool SupportsOperationArguments(IReadOnlyList<string> arguments) =>
        arguments.Count == 2
        || (arguments.Count == 3 && arguments[1] == "--force");

    internal static ProcessStartInfo? StartInfo(string masPath, IReadOnlyList<string> arguments, string prompt)
    {
        // Custom command-line switches stay on the password/sudo path, not the narrow helper protocol.
        if (Settings.Get(Settings.K.DisableMasTouchIdHelper)
            || !SupportsOperationArguments(arguments)
            || HelperPath() is not { } helper)
            return null;

        ProcessStartInfo start = new(helper)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(masPath);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(prompt);
        return start;
    }

    internal static async Task<string?> GetBootstrapHelperPathAsync(IReadOnlyList<string> arguments)
    {
        if (Settings.Get(Settings.K.DisableMasTouchIdHelper)
            || !SupportsOperationArguments(arguments)
            || HelperPath() is not { } helper)
            return null;

        return await CanBootstrapWithHelper(helper) ? helper : null;
    }

    private static async Task<bool> CheckCanOfferTouchIdAsync(string helperPath)
    {
        try
        {
            var (exitCode, _) = await RunHelperVerbAsync(helperPath, "can-touch-id");
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Logger.Warn($"Could not query Touch ID capability from the Mac App Store helper: {ex.Message}");
            return false;
        }
    }

    public static async Task<MasTouchIdStatus> GetStatusAsync()
    {
        bool disabled = Settings.Get(Settings.K.DisableMasTouchIdHelper);
        if (HelperPath() is not { } path)
            return new MasTouchIdStatus(HelperReady: false, BiometricsAvailable: false, DisabledByUser: disabled);

        var (exitCode, output) = await RunHelperVerbAsync(path, "status");
        bool biometrics = output.Contains("biometrics=1", StringComparison.Ordinal);
        return new MasTouchIdStatus(HelperReady: exitCode == 0, BiometricsAvailable: biometrics, DisabledByUser: disabled);
    }

    public static async Task<string> ConfigureAsync(bool enable)
    {
        if (enable)
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
                throw new InvalidOperationException(CoreTools.Translate(
                    "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings."));

            string path = HelperPath() ?? throw new InvalidOperationException(CoreTools.Translate(
                "Touch ID authorization requires macOS 12 or later and the bundled UniGetUI helper."));

            Settings.Set(Settings.K.DisableMasTouchIdHelper, false);
            MasTouchIdStatus before = await GetStatusAsync();
            if (before.HelperReady)
                return DescribeStatus(before);

            await RunRootHelperCommandAsync(
                path,
                "install-daemon",
                CoreTools.Translate("UniGetUI wants to enable Touch ID authorization for the Mac App Store."));

            MasTouchIdStatus after = await GetStatusAsync();
            if (!after.HelperReady)
                throw new InvalidOperationException(CoreTools.Translate("The Mac App Store Touch ID helper did not finish starting."));

            return DescribeStatus(after);
        }

        bool wasDisabled = Settings.Get(Settings.K.DisableMasTouchIdHelper);
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        if (!HasInstalledDaemonFiles())
            return CoreTools.Translate("Touch ID authorization is disabled. Mac App Store operations will ask for an administrator password.");

        if (Settings.Get(Settings.K.ProhibitElevation))
        {
            Settings.Set(Settings.K.DisableMasTouchIdHelper, wasDisabled);
            throw new InvalidOperationException(CoreTools.Translate(
                "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings."));
        }

        string removeHelper = HelperPath() ?? InstalledHelperPath;
        if (!File.Exists(removeHelper))
            return CoreTools.Translate("Touch ID authorization is disabled. Mac App Store operations will ask for an administrator password.");

        try
        {
            await RunRootHelperCommandAsync(
                removeHelper,
                "remove-daemon",
                CoreTools.Translate("UniGetUI wants to remove the Mac App Store Touch ID helper."));
        }
        catch
        {
            Settings.Set(Settings.K.DisableMasTouchIdHelper, wasDisabled);
            throw;
        }

        return CoreTools.Translate("Touch ID authorization is disabled. Mac App Store operations will ask for an administrator password.");
    }

    public static string DescribeStatus(MasTouchIdStatus status)
    {
        if (status.DisabledByUser)
            return CoreTools.Translate("Touch ID authorization is disabled. Mac App Store operations will ask for an administrator password.");
        if (status.HelperReady)
            return status.BiometricsAvailable
                ? CoreTools.Translate("Touch ID authorization is enabled. Mac App Store operations can be authorized with Touch ID or your password, with live progress output.")
                : CoreTools.Translate("The Mac App Store helper is enabled. Operations stream live progress output and will use Touch ID automatically whenever it is available.");
        return CoreTools.Translate("Install the UniGetUI helper once with your administrator password to authorize Mac App Store operations with Touch ID and stream live progress. This does not change sudo settings.");
    }

    private static async Task RunRootHelperCommandAsync(string helperPath, string verb, string prompt)
    {
        string command = $"{CoreTools.QuoteForPosixShell(helperPath)} {verb} 2>&1";
        ProcessStartInfo startInfo = new(ElevatedMas.OsaScriptPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-e", ElevatedMas.AuthorizationScript, "--", command, prompt })
            startInfo.ArgumentList.Add(argument);

        List<string> lines = [];
        int exitCode = await ElevatedMas.RunProcess(startInfo, lines.Add, CancellationToken.None);
        string output = string.Join('\n', lines).Trim();
        if (output.Length > 0)
            Logger.Info(output);
        if (exitCode == 0)
            return;
        if (lines.Any(line => line.TrimEnd().EndsWith("(-128)", StringComparison.Ordinal)))
            throw new OperationCanceledException(CoreTools.Translate("Administrator authorization was canceled."));

        throw new InvalidOperationException(output.Length > 0
            ? output
            : CoreTools.Translate("Could not configure Touch ID authorization."));
    }

    private static async Task<(int ExitCode, string Output)> RunHelperVerbAsync(string helperPath, string verb)
    {
        ProcessStartInfo start = new(helperPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(verb);
        using Process process = Process.Start(start)
            ?? throw new IOException("Could not start the Mac App Store helper.");
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = (await stdout + "\n" + await stderr).Trim();
        return (process.ExitCode, output);
    }
}
