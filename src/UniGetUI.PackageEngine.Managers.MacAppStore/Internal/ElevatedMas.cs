using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>Authorizes mas with the system administrator dialog; UniGetUI never handles a password.</summary>
internal static partial class ElevatedMas
{
    internal const string OsaScriptPath = "/usr/bin/osascript";
    internal const string AuthorizationScript = """
        on run argv
            return do shell script (item 1 of argv) with prompt (item 2 of argv) with administrator privileges without altering line endings
        end run
        """;

    internal static Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> RunProcess { get; set; } =
        RunAndReadAsync;

    internal static Func<(uint UserId, uint GroupId)> UserIds { get; set; } = GetUserIds;

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

    internal static string Command(string masPath, IEnumerable<string> arguments, uint userId, uint groupId)
    {
        // mas drops its effective identity to SUDO_UID/GID for Store services. Authorization
        // Services does not set these; preserve the requesting user, not the authorizing admin.
        string[] command =
        [
            "/usr/bin/env",
            "SUDO_UID=" + userId.ToString(CultureInfo.InvariantCulture),
            "SUDO_GID=" + groupId.ToString(CultureInfo.InvariantCulture),
            masPath,
            .. arguments,
        ];
        return string.Join(' ', command.Select(CoreTools.QuoteForPosixShell)) + " 2>&1";
    }

    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string masPath,
        IEnumerable<string> masArguments,
        string prompt,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (userId, groupId) = UserIds();
        ProcessStartInfo startInfo = new(OsaScriptPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-e", AuthorizationScript, "--", Command(masPath, masArguments, userId, groupId), prompt })
            startInfo.ArgumentList.Add(argument);

        log(CoreTools.Translate(
            "Waiting for macOS authorization and the Mac App Store. Command output will appear when the operation finishes."));
        cancellationToken.ThrowIfCancellationRequested();

        // Killing osascript does not stop its privileged child. Keep waiting (and hold the manager
        // gate) rather than orphan an installer or report cancellation while it is still running.
        // Cancel in the system authorization dialog itself is returned as AppleScript error -128.
        using var registration = cancellationToken.Register(() => log(CoreTools.Translate(
            "Cancellation was requested. Waiting for the authorized Mac App Store operation to finish; check its state in the App Store.")));
        List<string> output = [];
        int exitCode = await RunProcess(startInfo, line =>
        {
            lock (output)
                output.Add(line);
            log(line);
        }, CancellationToken.None);
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
