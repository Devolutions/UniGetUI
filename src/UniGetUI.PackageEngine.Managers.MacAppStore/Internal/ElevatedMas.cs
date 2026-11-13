using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// Runs UniGetUI's built-in Mac App Store helper via the Touch ID LaunchDaemon, configured sudo
/// Touch ID, or macOS's native administrator-password dialog.
/// </summary>
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

    private static string[] PerformRootArguments(string helperPath, IEnumerable<string> arguments, uint userId) =>
    [
        helperPath,
        "perform-root",
        userId.ToString(CultureInfo.InvariantCulture),
        .. arguments,
    ];

    // Authorization Services starts the command from the system-wide authtrampoline, outside the
    // user's login session, where the App Store's per-user services cannot be reached. asuser runs
    // the helper in the user's bootstrap context while preserving real root for installer/Trash.
    internal static string Command(string helperPath, IEnumerable<string> arguments, uint userId) =>
        string.Join(' ', new[] { LaunchctlPath, "asuser", userId.ToString(CultureInfo.InvariantCulture) }
            .Concat(PerformRootArguments(helperPath, arguments, userId))
            .Select(CoreTools.QuoteForPosixShell)) + " 2>&1";

    internal static string CommandWithHelperBootstrap(
        string helperPath,
        IEnumerable<string> arguments,
        uint userId) =>
        $"{CoreTools.QuoteForPosixShell(helperPath)} install-daemon >/dev/null 2>&1 || true; "
        + Command(helperPath, arguments, userId);

    private static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        CreateNoWindow = true,
    };

    public static Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string helperPath,
        IEnumerable<string> operationArguments,
        string prompt,
        Action<string> log,
        CancellationToken cancellationToken) =>
        RunAsync(helperPath, operationArguments, prompt, log, _ => { }, cancellationToken);

    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string helperPath,
        IEnumerable<string> operationArguments,
        string prompt,
        Action<string> log,
        Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (userId, _) = UserIds();
        string[] arguments = operationArguments.ToArray();

        // Killing an elevation process does not reliably stop its privileged child. Hold the manager
        // gate rather than orphan an installer or report cancellation while it is still running.
        using var registration = cancellationToken.Register(() => log(CoreTools.Translate(
            "Cancellation was requested. Waiting for the authorized Mac App Store operation to finish; check its state in the App Store.")));
        List<string> output = [];
        void OnLine(string line)
        {
            if (NativeMas.TryParseProgressLine(line, out OperationProgress parsed))
            {
                if (parsed != OperationProgress.Unknown)
                    progress(parsed);
                return;
            }
            lock (output)
                output.Add(line);
            log(line);
        }

        if (NativeMas.StartInfo(arguments, prompt) is { } native)
        {
            log(CoreTools.Translate("Checking Touch ID authorization for the Mac App Store."));
            cancellationToken.ThrowIfCancellationRequested();
            int nativeExit = await RunProcess(native, OnLine, CancellationToken.None);
            if (nativeExit == NativeMas.Canceled)
                return (NativeMas.CancellationResult, output.ToArray());
            // Unavailable is emitted only before the store operation starts. Transport errors or a
            // failed store operation never trigger a retry.
            if (nativeExit != NativeMas.Unavailable)
                return (nativeExit, output.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
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
                .. PerformRootArguments(helperPath, arguments, userId),
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

        string? bootstrapHelper = await NativeMas.GetBootstrapHelperPathAsync(arguments);
        string shellCommand = bootstrapHelper is not null
            ? CommandWithHelperBootstrap(bootstrapHelper, arguments, userId)
            : Command(helperPath, arguments, userId);
        if (bootstrapHelper is not null)
            log(CoreTools.Translate(
                "Touch ID is not set up yet. After you enter your administrator password once, future Mac App Store operations can use Touch ID."));

        ProcessStartInfo startInfo = StartInfo(OsaScriptPath);
        foreach (string argument in new[] { "-e", AuthorizationScript, "--", shellCommand, prompt })
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
