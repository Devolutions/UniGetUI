using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>Uses the opt-in native helper, configured sudo Touch ID, or the administrator-password dialog.</summary>
internal static partial class ElevatedMas
{
    internal const string OsaScriptPath = "/usr/bin/osascript";
    internal const string SudoPath = "/usr/bin/sudo";
    internal const string LaunchctlPath = "/bin/launchctl";
    internal const string SudoCommandScript = """
        printf '%s\n' "$1" || exit
        shift
        exec "$@"
        """;
    internal const string AuthorizationScript = """
        on run argv
            return do shell script (item 1 of argv) with prompt (item 2 of argv) with administrator privileges without altering line endings
        end run
        """;

    internal static Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> RunProcess { get; set; } =
        RunAndReadAsync;

    internal static Func<(uint UserId, uint GroupId)> UserIds { get; set; } = GetUserIds;
    internal static Func<bool> TouchIdSudoEnabled { get; set; } = ReadTouchIdConfiguration;

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getgid")]
    private static partial uint GetGid();

    private static (uint, uint) GetUserIds()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Mac App Store authorization requires macOS.");
        return (GetUid(), GetGid());
    }

    private static bool ReadTouchIdConfiguration()
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        try
        {
            return HasTouchIdConfiguration(File.ReadAllText("/etc/pam.d/sudo"),
                File.Exists("/etc/pam.d/sudo_local") ? File.ReadAllText("/etc/pam.d/sudo_local") : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read sudo's Touch ID configuration; using native password authorization: {ex.Message}");
            return false;
        }
    }

    internal static bool HasTouchIdConfiguration(string sudoConfiguration, string localConfiguration)
    {
        static IEnumerable<string[]> Rows(string text) => text.Split('\n')
            .Select(line => line.Split('#', 2)[0].Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries));

        static bool HasModule(IEnumerable<string[]> rows) => rows.Any(row =>
            row.Length >= 3 && row[0] == "auth" && row[1] == "sufficient"
            && Path.GetFileName(row[2]) == "pam_tid.so");

        var sudo = Rows(sudoConfiguration).ToArray();
        return HasModule(sudo) || (sudo.Any(row =>
            row.Length >= 3 && row[0] == "auth" && row[1] == "include" && row[2] == "sudo_local")
            && HasModule(Rows(localConfiguration)));
    }

    private static string[] CommandArguments(string masPath, IEnumerable<string> arguments, uint userId, uint groupId)
    {
        // mas drops its effective identity to SUDO_UID/GID for Store services. Authorization
        // Services does not set these; preserve the requesting user, not the authorizing admin.
        return
        [
            "/usr/bin/env",
            "SUDO_UID=" + userId.ToString(CultureInfo.InvariantCulture),
            "SUDO_GID=" + groupId.ToString(CultureInfo.InvariantCulture),
            masPath,
            .. arguments,
        ];
    }

    // Authorization Services starts the command from the system-wide authtrampoline, outside the
    // user's login session, where the App Store's per-user services cannot be reached and mas waits
    // forever for its purchase request. asuser runs it in the user's bootstrap context, as sudo would.
    internal static string Command(string masPath, IEnumerable<string> arguments, uint userId, uint groupId) =>
        string.Join(' ', new[] { LaunchctlPath, "asuser", userId.ToString(CultureInfo.InvariantCulture) }
            .Concat(CommandArguments(masPath, arguments, userId, groupId))
            .Select(CoreTools.QuoteForPosixShell)) + " 2>&1";

    private static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true,
    };

    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string masPath,
        IEnumerable<string> masArguments,
        string prompt,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (userId, groupId) = UserIds();
        string[] arguments = masArguments.ToArray();

        // Killing an elevation process does not reliably stop its privileged child. Hold the manager
        // gate rather than orphan an installer or report cancellation while it is still running.
        // Cancel in the system authorization dialog itself is returned as AppleScript error -128.
        using var registration = cancellationToken.Register(() => log(CoreTools.Translate(
            "Cancellation was requested. Waiting for the authorized Mac App Store operation to finish; check its state in the App Store.")));
        List<string> output = [];
        void OnLine(string line)
        {
            lock (output)
                output.Add(line);
            log(line);
        }

        if (NativeMas.StartInfo(masPath, arguments, prompt) is { } native)
        {
            log(CoreTools.Translate("Checking native Touch ID authorization for the Mac App Store."));
            cancellationToken.ThrowIfCancellationRequested();
            int nativeExit = await RunProcess(native, OnLine, CancellationToken.None);
            if (nativeExit == NativeMas.Canceled)
                return (NativeMas.CancellationResult, output.ToArray());
            // Both fallback outcomes are emitted only before mas starts. Transport errors or a
            // failed mas command never trigger a retry.
            if (nativeExit is not (NativeMas.Unavailable or NativeMas.SigningRequired))
                return (nativeExit, output.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            log(nativeExit == NativeMas.SigningRequired
                ? CoreTools.Translate("Native Touch ID requires a UniGetUI build signed with an Apple-issued certificate. This ad-hoc development build can use password authorization instead.")
                : CoreTools.Translate("Native Touch ID is not available. Enable its helper in the Mac App Store manager settings, or authorize with a password."));
        }

        if (TouchIdSudoEnabled())
        {
            log(prompt);
            log(CoreTools.Translate("Waiting for sudo authorization. macOS can offer Touch ID if it is available."));
            string marker = "unigetui-mas-start-" + Guid.NewGuid().ToString("N");
            bool commandStarted = false;
            ProcessStartInfo sudo = StartInfo(SudoPath);
            // pam_tid explicitly disables its UI in askpass (-A) mode. -S with closed stdin lets
            // PAM offer Touch ID but never asks UniGetUI to collect or send a password.
            sudo.Environment.Remove("SUDO_ASKPASS");
            string[] sudoArguments =
            [
                "-S", "-p", "", "--", "/bin/sh", "-c", SudoCommandScript, "unigetui-mas", marker,
                .. CommandArguments(masPath, arguments, userId, groupId),
            ];
            foreach (string argument in sudoArguments)
                sudo.ArgumentList.Add(argument);

            cancellationToken.ThrowIfCancellationRequested();
            int sudoExit = await RunProcess(sudo, line =>
            {
                if (line == marker)
                    commandStarted = true;
                else
                    OnLine(line);
            }, CancellationToken.None);
            // An installer failure must never cause a second install through the password path.
            // The root shell emits this marker before exec, including when exec itself fails.
            if (commandStarted)
            {
                lock (output)
                    return (sudoExit, output.ToArray());
            }
            if (sudoExit == 0)
                throw new InvalidOperationException("sudo exited successfully without confirming that the Mac App Store command started.");

            cancellationToken.ThrowIfCancellationRequested();
            log(CoreTools.Translate(
                "Touch ID did not authorize the operation. Falling back to the macOS administrator-password dialog."));
        }

        ProcessStartInfo startInfo = StartInfo(OsaScriptPath);
        foreach (string argument in new[] { "-e", AuthorizationScript, "--", Command(masPath, arguments, userId, groupId), prompt })
            startInfo.ArgumentList.Add(argument);
        log(CoreTools.Translate(
            "Waiting for the macOS administrator-password dialog and the Mac App Store. Command output will appear when the operation finishes."));
        cancellationToken.ThrowIfCancellationRequested();
        int exitCode = await RunProcess(startInfo, OnLine, CancellationToken.None);
        lock (output)
            return (exitCode, output.ToArray());
    }

    private static async Task<int> RunAndReadAsync(
        ProcessStartInfo startInfo,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        process.StandardInput.Close();
        Task stdout = PumpAsync(process.StandardOutput, onLine);
        Task stderr = PumpAsync(process.StandardError, onLine);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Trim().Length > 0)
                onLine(line);
    }
}
