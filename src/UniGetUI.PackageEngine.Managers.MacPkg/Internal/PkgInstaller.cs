using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

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

    /// <summary>
    /// UniGetUI's own executable, when it can be run as root to stage and check a package there; null when
    /// it is not UniGetUI or a standard user could replace it. Replaceable for tests.
    /// </summary>
    internal static Func<string?> RootExecutable { get; set; } = () =>
        Environment.ProcessPath is { } path
        && Path.GetFileName(path).StartsWith("UniGetUI", StringComparison.OrdinalIgnoreCase)
        && TrustedPath.WhyUntrusted(path) is null
            ? path
            : null;

    /// <summary>
    /// Installs through the root helper when IT set it up (no password asked), else as root behind the
    /// administrator prompt. <paramref name="feedId"/> names the feed for the helper, which looks it up itself.
    /// </summary>
    public static async Task<OperationVeredict> InstallAsync(PkgFeedItem item, string displayName,
        Action<string> log, Action<OperationProgress> progress, CancellationToken cancellationToken,
        bool allowUnsigned = false, string? feedId = null)
    {
        // Finding the package may have outlasted a cancel; past this point an install cannot be called back.
        cancellationToken.ThrowIfCancellationRequested();
        if (feedId is not null && !IsRoot() && PkgHelperClient.IsAvailable)
        {
            progress(OperationProgress.ForStage(OperationProgressStage.Installing));
            if (await PkgHelperClient.TryInstallAsync(feedId, item, log) is { } verdict)
                return verdict;
            log("The UniGetUI package helper did not install this package; asking for administrator rights instead.");
        }
        if (!IsRoot() && RootExecutable() is { } executable)
            return await InstallAsRootProcessAsync(executable, item, displayName, allowUnsigned, log, progress);

        // Without a protected UniGetUI to run as root, the package is checked here, before the prompt.
        (int code, string output) result;
        using (var prepared = await PrepareAsync(item, allowUnsigned, log, progress, cancellationToken))
        {
            // From here on the operation cannot be canceled: a root installer or copy cut short leaves
            // a half-installed app, and the staged file and mounted image must outlive it.
            cancellationToken.ThrowIfCancellationRequested();
            log(prepared.Description);
            result = await RunAsRootAsync(prepared.Command, displayName, CancellationToken.None);
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

    // The package is staged and checked by root, in a folder the user's own processes cannot reach, so
    // none of them can swap it between the checks and the install.
    private static async Task<OperationVeredict> InstallAsRootProcessAsync(string executable, PkgFeedItem item,
        string displayName, bool allowUnsigned, Action<string> log, Action<OperationProgress> progress)
    {
        progress(OperationProgress.ForStage(OperationProgressStage.Installing));
        log("Asking for administrator rights; the package is then downloaded, checked and installed as root...");
        string order = FeedJson.Serialize(PkgElevatedInstall.From(item, allowUnsigned),
            PkgHelperJsonContext.Default.PkgElevatedInstall);
        string command = $"{CoreTools.QuoteForPosixShell(executable)} {PkgHelperCommand.Command} "
            + $"{PkgHelperCommand.InstallItemVerb} {CoreTools.QuoteForPosixShell(order)} 2>&1";
        var (code, output) = await RunAsRootAsync(command, displayName, CancellationToken.None);
        // do shell script ends its lines with \r.
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (code != 0 && output.Contains($"({UserCanceledAppleScriptError})", StringComparison.Ordinal))
        {
            log("The administrator prompt was canceled.");
            return OperationVeredict.Canceled;
        }
        string? result = null;
        foreach (string line in lines)
            result = PkgHelperClient.Interpret(line, log) ?? result;
        if (result == PkgHelperMessage.Success)
            return OperationVeredict.Success;
        if (result is null)
            log($"UniGetUI did not finish installing as root (exit code {code}).");
        return OperationVeredict.Failure;
    }

    /// <summary>A verified package ready to install: the root shell command that installs it, and what
    /// it needs to stay in place (the staged file, a mounted image) until that command is done.</summary>
    internal sealed class PreparedInstall : IDisposable
    {
        private readonly string _stagingDirectory;
        private DiskImage? _image;

        public PreparedInstall(string stagingDirectory) => _stagingDirectory = stagingDirectory;

        public string Command { get; set; } = "";
        public string Description { get; set; } = "";

        public DiskImage Mount(string image) => _image = DiskImage.Mount(image);

        public void Dispose()
        {
            _image?.Dispose();
            try { Directory.Delete(_stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Stages a private copy of the package and checks it is what the feed listed, signed as Gatekeeper
    /// requires; then gives the command that installs it. The root helper runs the same steps as root.
    /// </summary>
    internal static async Task<PreparedInstall> PrepareAsync(PkgFeedItem item, bool allowUnsigned, Action<string> log,
        Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        if (allowUnsigned && item.Location.IsFile)
            RefuseFilesOthersCanWrite(item.Location.LocalPath);
        // Created at once with mode 0700 (mkdtemp), so no other user can own or swap any folder on its path.
        string stagingDirectory = Directory.CreateTempSubdirectory("UniGetUI-pkg-").FullName;
        var prepared = new PreparedInstall(stagingDirectory);
        try
        {
            // A fixed name keeps the vendor's file name out of the elevated command line.
            string staged = Path.Combine(stagingDirectory, "package" + item.Kind switch
            {
                FeedFileKind.DiskImage => ".dmg",
                FeedFileKind.Archive => ".zip",
                _ => ".pkg",
            });
            await StageAsync(item, staged, log, progress, cancellationToken);
            VerifyHash(item, staged);

            progress(OperationProgress.ForStage(OperationProgressStage.Installing));
            log($"Installing {item.Id} {item.Version}...");
            string? package = null, app = null;
            switch (item.Kind)
            {
                case FeedFileKind.Package:
                    package = staged;
                    break;
                case FeedFileKind.DiskImage:
                    var (content, isPackage) = prepared.Mount(staged).FindContent();
                    if (isPackage)
                        package = content;
                    else
                        app = content;
                    break;
                case FeedFileKind.Archive:
                    app = await ExtractAppAsync(staged, Path.Combine(stagingDirectory, "extracted"), cancellationToken);
                    break;
            }

            if (package is not null)
            {
                VerifyIdentity(item, PkgMetadata.Read(package));
                await VerifySignatureAsync(package, "install", allowUnsigned, log, cancellationToken);
                prepared.Command = LockedCommand(InstallPkgCommand(package));
                prepared.Description = $"Running the installer for {Uri.UnescapeDataString(Path.GetFileName(item.Location.AbsolutePath))}...";
            }
            else
            {
                VerifyIdentity(item, DiskImage.FromApp(app!));
                await VerifySignatureAsync(app!, "execute", allowUnsigned, log, cancellationToken);
                string destination = AppBundle.OnDisk("/Applications/" + SafeAppName(app!));
                prepared.Command = LockedCommand(CopyAppCommand(app!, destination));
                prepared.Description = $"Copying {Path.GetFileName(app)} to {destination}...";
            }
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    // ditto unpacks as macOS does, keeping the symlinks and permissions an app bundle relies on.
    internal static async Task<string> ExtractAppAsync(string zip, string destination, CancellationToken cancellationToken)
    {
        string app;
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
            app = ZipApp.FindApp(archive.Entries.Select(e => e.FullName));
        var result = await Tool.RunAsync("/usr/bin/ditto", ["-x", "-k", zip, destination], cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidDataException($"The zip archive could not be extracted: {result.Output.Trim()}");
        string path = Path.Combine(destination, app);
        if (!Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidDataException($"The zip archive did not extract {app}.");
        return path;
    }

    /// <summary>Copies a folder-feed package, or downloads a web-feed one, to the given path.</summary>
    public static Task StageAsync(PkgFeedItem item, string destination, Action<string> log,
        Action<OperationProgress> progress, CancellationToken cancellationToken) =>
        FeedStaging.StageAsync(item.Location, item.Size, destination, log, progress, cancellationToken);

    /// <summary>Refuses a staged file whose hash differs from the one the feed index announces.</summary>
    internal static void VerifyHash(PkgFeedItem item, string staged) => FeedStaging.VerifyHash(item.Sha256, staged);

    /// <summary>Refuses a file that is no longer what the feed listed, such as a folder feed's file replaced since.</summary>
    internal static void VerifyIdentity(PkgFeedItem item, PkgMetadata staged)
    {
        if (staged.Id != item.Id || staged.Version != item.Version)
            throw new InvalidDataException(
                $"The package file is {staged.Id} {staged.Version}, but the feed announced {item.Id} {item.Version}. Reload the feed, then try again.");
    }

    internal const string SpctlPath = "/usr/sbin/spctl";

    /// <summary>
    /// Asks Gatekeeper, as macOS does for downloads, whether the package or app is signed by an identified
    /// developer and notarized; only a catalog that allows unsigned packages lets a rejected one through.
    /// </summary>
    internal static async Task VerifySignatureAsync(string path, string type, bool allowUnsigned, Action<string> log,
        CancellationToken cancellationToken)
    {
        var (code, output) = await RunProcess(SpctlPath, ["--assess", "--type", type, "-vv", path], cancellationToken);
        string verdict = string.Join("; ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1));
        if (code == 0)
            log($"Gatekeeper accepts the package: {verdict}");
        else if (allowUnsigned)
            log($"Gatekeeper rejects the package ({verdict}), but its catalog allows unsigned packages.");
        else
            throw new InvalidDataException($"macOS Gatekeeper rejects this package ({verdict}). Publish a package signed "
                + "with a Developer ID and notarized by Apple, or set allowUnsignedPackages on its catalog.");
    }

    // Unsigned packages are trusted only for where they come from: a file, or folder, that any user
    // on the Mac can write to could hold anyone's package, so it is refused.
    internal static void RefuseFilesOthersCanWrite(string file)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string path in new[] { file, Path.GetDirectoryName(file)! })
        {
            if (File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherWrite))
                throw new InvalidDataException($"{path} can be written by any user on this Mac, so its unsigned package "
                    + "is not installed. Keep a feed that allows unsigned packages in a folder only administrators can change.");
        }
    }

    internal static string SafeAppName(string app)
    {
        string name = Path.GetFileName(app.TrimEnd('/'));
        if (name.StartsWith('.') || !name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || name.Any(char.IsControl))
            throw new InvalidDataException($"\"{name}\" is not an app name UniGetUI can install.");
        return name;
    }

    /// <summary>Where root installs take their machine-wide lock. Replaceable for tests.</summary>
    internal static string LockDirectory { get; set; } = PkgHelperCommand.RootDirectory;

    // One root install at a time on the Mac, whether the helper or a prompt started it, so two installs
    // of one app never share its working copies. lockf holds the lock while the whole command runs.
    internal static string LockedCommand(string command)
    {
        string directory = CoreTools.QuoteForPosixShell(LockDirectory);
        string lockFile = CoreTools.QuoteForPosixShell(Path.Combine(LockDirectory, "install.lock"));
        return $"/bin/mkdir -p -m 700 {directory} && /usr/bin/lockf -k -t 1800 {lockFile} /bin/sh -c {CoreTools.QuoteForPosixShell(command)}";
    }

    internal static string InstallPkgCommand(string stagedPackage) =>
        $"{InstallerPath} -pkg {CoreTools.QuoteForPosixShell(stagedPackage)} -target / 2>&1";

    // The old app is moved aside, not deleted, until the new one is in place; fixed non-.app working
    // names let the next update restore an app that a cut-off swap left aside, then clear the rest.
    internal static string CopyAppCommand(string sourceApp, string destinationApp)
    {
        string directory = Path.GetDirectoryName(destinationApp)!;
        string name = Path.GetFileNameWithoutExtension(destinationApp);
        string source = CoreTools.QuoteForPosixShell(sourceApp), destination = CoreTools.QuoteForPosixShell(destinationApp);
        string staging = CoreTools.QuoteForPosixShell(Path.Combine(directory, $".{name}.unigetui-new"));
        string previous = CoreTools.QuoteForPosixShell(Path.Combine(directory, $".{name}.unigetui-old"));
        return $"if [ ! -e {destination} ] && [ -e {previous} ]; then /bin/mv {previous} {destination}; fi; "
            + $"/bin/rm -rf {staging} {previous}; "
            + $"/usr/bin/ditto {source} {staging} || {{ /bin/rm -rf {staging}; exit 1; }}; "
            + $"if [ -e {destination} ]; then /bin/mv {destination} {previous} || {{ /bin/rm -rf {staging}; exit 1; }}; fi; "
            + $"if /bin/mv {staging} {destination}; then /bin/rm -rf {previous}; "
            + $"else /bin/rm -rf {staging}; [ -e {previous} ] && /bin/mv {previous} {destination}; exit 1; fi";
    }

    private static Task<(int, string)> RunAsRootAsync(string command, string displayName, CancellationToken cancellationToken) =>
        IsRoot()
            ? RunProcess("/bin/sh", ["-c", command], cancellationToken)
            : RunProcess(OsaScriptPath, ["-e", ElevatedScript(command, displayName)], cancellationToken);

    // osascript's "with administrator privileges" shows the standard macOS password dialog and
    // runs the shell command as root.
    internal static string ElevatedScript(string command, string displayName) =>
        $"do shell script \"{CoreTools.EscapeAppleScriptString(command)}\""
        + $" with prompt \"{CoreTools.EscapeAppleScriptString(CoreTools.Translate("UniGetUI wants to install {0}.", displayName))}\""
        + " with administrator privileges";

    private static async Task<(int, string)> RunProcessAsync(string fileName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await Tool.RunAsync(fileName, arguments, cancellationToken: cancellationToken);
        return (result.ExitCode, result.Output);
    }
}
