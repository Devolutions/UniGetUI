using System.Diagnostics;
using System.Security.Cryptography;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Installs a feed package: stages a private copy, checks it is the package the feed announced, then,
/// as root behind the macOS administrator prompt, runs /usr/sbin/installer or copies a .dmg's app.
/// </summary>
internal static class PkgInstaller
{
    internal const string InstallerPath = "/usr/sbin/installer";
    private const string OsaScriptPath = "/usr/bin/osascript";
    private const int UserCanceledAppleScriptError = -128;

    /// <summary>Runs a process and returns its exit code with its combined output. Replaceable for tests.</summary>
    internal static Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> RunProcess
    { get; set; } = RunProcessAsync;

    /// <summary>Whether UniGetUI already runs as root, so no prompt is needed. Replaceable for tests.</summary>
    internal static Func<bool> IsRoot { get; set; } = CoreTools.IsAdministrator;

    public static async Task<OperationVeredict> InstallAsync(PkgFeedItem item, string displayName,
        Action<string> log, Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        string stagingDirectory = Path.Combine(Path.GetTempPath(), "UniGetUI-pkg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // A fixed name keeps the vendor's file name out of the elevated command line.
            string staged = Path.Combine(stagingDirectory, item.IsDiskImage ? "package.dmg" : "package.pkg");
            await StageAsync(item, staged, log, progress, cancellationToken);
            VerifyHash(item, staged);

            progress(OperationProgress.ForStage(OperationProgressStage.Installing));
            log($"Installing {item.Id} {item.Version}...");
            (int code, string output) result;
            if (item.IsDiskImage)
            {
                using var image = DiskImage.Mount(staged);
                string app = image.FindApp();
                VerifyIdentity(item, DiskImage.FromApp(app));
                string destination = AppBundle.OnDisk("/Applications/" + SafeAppName(app));
                log($"Copying {Path.GetFileName(app)} to {destination}...");
                result = await RunAsRootAsync(CopyAppCommand(app, destination), displayName, cancellationToken);
            }
            else
            {
                VerifyIdentity(item, PkgMetadata.Read(staged));
                result = await RunAsRootAsync(InstallPkgCommand(staged), displayName, cancellationToken);
            }
            var (code, output) = result;
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                log(line);
            if (code == 0)
                return OperationVeredict.Success;
            if (output.Contains($"({UserCanceledAppleScriptError})", StringComparison.Ordinal))
            {
                log("The administrator prompt was canceled.");
                return OperationVeredict.Canceled;
            }
            log($"The installer exited with code {code}.");
            return OperationVeredict.Failure;
        }
        finally
        {
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies a folder-feed package, or downloads a web-feed one, to the given path.</summary>
    public static async Task StageAsync(PkgFeedItem item, string destination, Action<string> log,
        Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress(OperationProgress.ForStage(OperationProgressStage.Downloading));
        if (item.Location.IsFile)
        {
            log($"Copying {item.Location.LocalPath}...");
            await using var source = File.OpenRead(item.Location.LocalPath);
            await using var target = File.Create(destination);
            await CopyAsync(source, target, source.Length, progress, cancellationToken);
            return;
        }

        log($"Downloading {item.Location}...");
        using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        using var response = await client.GetAsync(item.Location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(destination))
            await CopyAsync(body, target, response.Content.Headers.ContentLength ?? item.Size, progress, cancellationToken);
    }

    private static async Task CopyAsync(Stream source, Stream target, long total, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress(OperationProgress.FromDownload((ulong)copied, (ulong)Math.Max(total, 0)));
        }
    }

    /// <summary>Refuses a staged file whose hash differs from the one the feed index announces.</summary>
    internal static void VerifyHash(PkgFeedItem item, string staged)
    {
        if (item.Sha256 is not { } expected)
            return;
        using var stream = File.OpenRead(staged);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded package's SHA-256 is {actual}, but the feed index announces {expected}.");
    }

    /// <summary>Refuses a file that is no longer what the feed listed, such as a folder feed's file replaced since.</summary>
    internal static void VerifyIdentity(PkgFeedItem item, PkgMetadata staged)
    {
        if (staged.Id != item.Id || staged.Version != item.Version)
            throw new InvalidDataException(
                $"The package file is {staged.Id} {staged.Version}, but the feed announced {item.Id} {item.Version}. Reload the feed, then try again.");
    }

    internal static string SafeAppName(string app)
    {
        string name = Path.GetFileName(app.TrimEnd('/'));
        if (name.StartsWith('.') || !name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || name.Any(char.IsControl))
            throw new InvalidDataException($"\"{name}\" is not an app name UniGetUI can install.");
        return name;
    }

    internal static string InstallPkgCommand(string stagedPackage) =>
        $"{InstallerPath} -pkg {ShellQuote(stagedPackage)} -target / 2>&1";

    // The app is copied beside its destination first, so a failed copy leaves the installed app untouched.
    internal static string CopyAppCommand(string sourceApp, string destinationApp)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destinationApp)!, $".unigetui-{Guid.NewGuid():N}.app");
        string source = ShellQuote(sourceApp), destination = ShellQuote(destinationApp), staging = ShellQuote(temporary);
        return $"/usr/bin/ditto {source} {staging} && /bin/rm -rf {destination} && /bin/mv {staging} {destination}"
            + $" || {{ /bin/rm -rf {staging}; exit 1; }}";
    }

    private static Task<(int, string)> RunAsRootAsync(string command, string displayName, CancellationToken cancellationToken) =>
        IsRoot()
            ? RunProcess("/bin/sh", ["-c", command], cancellationToken)
            : RunProcess(OsaScriptPath, ["-e", ElevatedScript(command, displayName)], cancellationToken);

    // osascript's "with administrator privileges" shows the standard macOS password dialog and
    // runs the shell command as root.
    internal static string ElevatedScript(string command, string displayName) =>
        $"do shell script \"{AppleScriptEscape(command)}\""
        + $" with prompt \"{AppleScriptEscape(CoreTools.Translate("UniGetUI wants to install {0}.", displayName))}\""
        + " with administrator privileges";

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static string AppleScriptEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task<(int, string)> RunProcessAsync(string fileName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
