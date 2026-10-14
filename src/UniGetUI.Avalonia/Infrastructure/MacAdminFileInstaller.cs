using System.Diagnostics;
using System.Runtime.Versioning;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Writes a file to a root-owned location on macOS through the system administrator prompt.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacAdminFileInstaller
{
    private const int UserCanceledError = -128;

    /// <summary>
    /// Copies <paramref name="contents"/> to <paramref name="destination"/> as root, creating its folder.
    /// <paramref name="prompt"/> is shown in the password dialog. Returns false when the user cancels it.
    /// </summary>
    public static async Task<bool> WriteAsync(string destination, string contents, string prompt)
    {
        string stagingDirectory = Directory.CreateTempSubdirectory("unigetui-admin-write-").FullName;
        try
        {
            string staged = Path.Combine(stagingDirectory, Path.GetFileName(destination));
            await File.WriteAllTextAsync(staged, contents);

            string folder = Path.GetDirectoryName(destination)!;
            string command = $"/bin/mkdir -p {ShellQuote(folder)} && /usr/bin/install -m 0644 {ShellQuote(staged)} {ShellQuote(destination)}";
            var startInfo = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add($"do shell script \"{AppleScriptEscape(command)}\" with prompt \"{AppleScriptEscape(prompt)}\" with administrator privileges");

            using var process = Process.Start(startInfo)
                ?? throw new IOException("Could not start the administrator prompt.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) return true;
            if (error.Contains($"({UserCanceledError})", StringComparison.Ordinal)) return false;
            throw new IOException(error.Trim());
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch { /* best effort cleanup of a temp folder */ }
        }
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    internal static string AppleScriptEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
