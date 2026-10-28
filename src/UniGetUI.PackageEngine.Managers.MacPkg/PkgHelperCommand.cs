using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// <c>unigetui pkg-helper</c>: a root daemon that installs packages the software catalog publishes, so
/// standard users install them without an administrator password, the way a management agent would.
/// </summary>
public static class PkgHelperCommand
{
    public const string Command = "pkg-helper";
    public const string Label = "com.devolutions.unigetui.pkghelper";
    internal const string SocketPath = "/var/run/" + Label + ".sock";
    internal const string DaemonPath = "/Library/LaunchDaemons/" + Label + ".plist";
    private const string LogPath = "/Library/Logs/UniGetUI-pkg-helper.log";
    private const int MaxFieldLength = 512;

    /// <summary>A folder only root can reach: the install lock, and the temporary files of root processes.</summary>
    internal const string RootDirectory = "/private/var/run/com.devolutions.unigetui";

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string verb = args.Length == 1 || (args.Length == 2 && args[0] == InstallItemVerb) ? args[0] : "";
        if (verb is not ("serve" or "install-daemon" or "remove-daemon" or InstallItemVerb))
        {
            output.WriteLine($"Usage: unigetui {Command} install-daemon | remove-daemon | serve");
            output.WriteLine("  install-daemon  Lets standard users install catalog packages without an administrator password.");
            output.WriteLine("  remove-daemon   Stops and removes that helper.");
            output.WriteLine("  serve           Runs the helper (what launchd starts; run the two commands above instead).");
            output.WriteLine($"  {InstallItemVerb} <order>  Installs one package as root (what UniGetUI runs behind the password prompt).");
            return args.Length == 1 && args[0] is "-h" or "--help" ? 0 : 2;
        }
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            error.WriteLine($"{Command} {verb} must run as root on macOS (sudo).");
            return 1;
        }
        try
        {
            // Before anything logs: the session log and staging files go in TMPDIR, which must not be a
            // folder a standard user shares (/tmp) or owns (the TMPDIR of the user who started this).
            UseRootTemporaryDirectory();
            return verb switch
            {
                "install-daemon" => InstallDaemon(output, error),
                "remove-daemon" => RemoveDaemon(output),
                InstallItemVerb => InstallItem(args[1], output),
                _ => Serve(output, error),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SocketException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void UseRootTemporaryDirectory()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string temporary = Path.Combine(RootDirectory, "tmp");
        foreach (string directory in new[] { RootDirectory, temporary })
        {
            var info = Directory.CreateDirectory(directory, RootOnly);
            if (info.LinkTarget is not null)
                throw new IOException($"{directory} is a link; remove it, then try again.");
            File.SetUnixFileMode(directory, RootOnly);
        }
        Environment.SetEnvironmentVariable("TMPDIR", temporary + "/");
    }

    // ── Daemon setup ───────────────────────────────────────────────────────

    // The daemon runs this very executable as root, so a standard user able to replace it, or the catalog
    // naming what it installs, would own the Mac: both must be out of their reach.
    private static string? WhyUnsafeToServe()
    {
        string executable = Environment.ProcessPath ?? "";
        return TrustedPath.WhyUntrusted(executable) is { } program
            ? $"UniGetUI itself is not protected: {program} Install UniGetUI with its installer package, so that it belongs to root."
            : null;
    }

    private static int InstallDaemon(TextWriter output, TextWriter error)
    {
        if (WhyUnsafeToServe() is { } reason)
        {
            error.WriteLine(reason);
            return 1;
        }
        File.WriteAllText(DaemonPath, DaemonPlist(Environment.ProcessPath!));
        File.SetUnixFileMode(DaemonPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Tool.Run("/bin/launchctl", ["bootout", $"system/{Label}"]);
        var started = Tool.Run("/bin/launchctl", ["bootstrap", "system", DaemonPath]);
        if (started.ExitCode != 0)
        {
            error.WriteLine($"launchctl could not start the helper: {started.Output.Trim()}");
            return 1;
        }
        output.WriteLine($"The UniGetUI package helper is installed ({DaemonPath}) and running. Its log is {LogPath}.");
        return 0;
    }

    private static int RemoveDaemon(TextWriter output)
    {
        Tool.Run("/bin/launchctl", ["bootout", $"system/{Label}"]);
        File.Delete(DaemonPath);
        File.Delete(SocketPath);
        output.WriteLine("The UniGetUI package helper is removed; installs ask for an administrator password again.");
        return 0;
    }

    internal static string DaemonPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(executable)}</string>
                <string>{Command}</string>
                <string>serve</string>
            </array>
            <key>RunAtLoad</key><true/>
            <key>KeepAlive</key><true/>
            <key>StandardOutPath</key><string>{LogPath}</string>
            <key>StandardErrorPath</key><string>{LogPath}</string>
        </dict>
        </plist>
        """;

    // ── Installing behind the administrator prompt ─────────────────────────

    /// <summary>
    /// What UniGetUI runs as root behind the administrator prompt: stages and checks the package as root,
    /// then installs it, writing the helper's messages, one per line, for the user's UniGetUI to read.
    /// </summary>
    internal const string InstallItemVerb = "install-item";

    private static int InstallItem(string json, TextWriter output)
    {
        void Write(PkgHelperMessage message) =>
            output.WriteLine(FeedJson.Serialize(message, PkgHelperJsonContext.Default.PkgHelperMessage));

        PkgHelperMessage result;
        try
        {
            var order = FeedJson.Deserialize(json, PkgHelperJsonContext.Default.PkgElevatedInstall)
                ?? throw new InvalidDataException("The install order is empty.");
            result = InstallItemAsync(order.ToItem(), order.AllowUnsigned, text => Write(new PkgHelperMessage { Log = text }))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException)
        {
            result = new PkgHelperMessage { Result = PkgHelperMessage.Failure, Message = $"The install order is invalid: {ex.Message}" };
        }
        Write(result);
        return 0;
    }

    // ── Serving ────────────────────────────────────────────────────────────

    private static int Serve(TextWriter output, TextWriter error)
    {
        if (WhyUnsafeToServe() is { } reason)
        {
            error.WriteLine(reason);
            return 1;
        }
        output.WriteLine($"{DateTime.Now:s} Listening on {SocketPath}.");
        ServeAsync(SocketPath, line => output.WriteLine($"{DateTime.Now:s} {line}"), CancellationToken.None)
            .GetAwaiter().GetResult();
        return 0;
    }

    private static readonly SemaphoreSlim OneInstallAtATime = new(1, 1);
    private static readonly TimeSpan ClientWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Accepts connections until canceled; any local user may connect and ask.</summary>
    internal static async Task ServeAsync(string socketPath, Action<string> journal, CancellationToken cancellationToken)
    {
        File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        listener.Listen(16);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(cancellationToken);
                _ = Task.Run(() => HandleAsync(client, journal), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    private static async Task HandleAsync(Socket client, Action<string> journal)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            var writeLock = new SemaphoreSlim(1, 1);
            bool clientGone = false;
            async Task Send(PkgHelperMessage message)
            {
                byte[] line = Encoding.UTF8.GetBytes(
                    FeedJson.Serialize(message, PkgHelperJsonContext.Default.PkgHelperMessage) + "\n");
                await writeLock.WaitAsync();
                try
                {
                    if (clientGone)
                        return;
                    using var timeout = new CancellationTokenSource(ClientWriteTimeout);
                    await stream.WriteAsync(line, timeout.Token);
                }
                // The user's app went away or stopped reading: it gets no more lines; the install goes on, journaled.
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { clientGone = true; }
                finally { writeLock.Release(); }
            }

            PkgHelperMessage result;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                string line = await ReadLineAsync(stream, timeout.Token);
                var request = FeedJson.Deserialize(line, PkgHelperJsonContext.Default.PkgHelperRequest)
                    ?? throw new InvalidDataException("The request is empty.");
                journal($"Asked to install {request.Id} {request.Version} from catalog {request.Catalog}.");
                await OneInstallAtATime.WaitAsync();
                try
                {
                    result = await InstallAsync(request, text =>
                    {
                        journal("  " + text);
                        Send(new PkgHelperMessage { Log = text }).GetAwaiter().GetResult();
                    });
                }
                finally
                {
                    OneInstallAtATime.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                result = new PkgHelperMessage { Result = PkgHelperMessage.Refused, Message = $"The request was refused: {ex.Message}" };
            }
            catch (Exception ex)
            {
                // A request must always end with an answer and a trace, never in a silently faulted task.
                journal($"  Unexpected error: {ex}");
                result = new PkgHelperMessage { Result = PkgHelperMessage.Failure, Message = $"The helper failed: {ex.Message}" };
            }
            journal($"  {result.Result}: {result.Message}");
            await Send(result);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new MemoryStream();
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken) == 1 && one[0] != (byte)'\n')
        {
            if (line.Length >= PkgHelperClient.MaxLineLength)
                throw new InvalidDataException("The request is too long.");
            line.WriteByte(one[0]);
        }
        return Encoding.UTF8.GetString(line.ToArray());
    }

    /// <summary>
    /// Installs what a request names, found by the helper itself in a feed the root-owned catalog
    /// declares, after the same checks as any install; anything else is refused.
    /// </summary>
    internal static async Task<PkgHelperMessage> InstallAsync(PkgHelperRequest request, Action<string> log)
    {
        static PkgHelperMessage Refuse(string why) => new() { Result = PkgHelperMessage.Refused, Message = why };

        string version = request.Version ?? "";
        if (!IsField(request.Catalog) || !IsField(request.Id) || (version.Length > 0 && !IsField(version)))
            return Refuse("The request names no valid catalog, package or version.");
        if (TrustedPath.WhyUntrusted(CatalogFeeds.CatalogPath) is { } catalogReason)
            return Refuse($"The software catalog is not protected, so the helper does not act on it: {catalogReason}");
        var feeds = CatalogFeeds.Load()
            .Where(f => string.Equals(f.Id, request.Catalog, StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
        if (feeds.Length == 0)
            return Refuse($"No catalog with the id {request.Catalog} declares a package feed.");
        var feed = feeds[0];
        Uri location = feed.Location;
        // Any user can listen on a loopback port the real server left free, so only https is trusted here.
        if (!location.IsFile && location.Scheme != Uri.UriSchemeHttps)
            return Refuse($"The package feed {location} is not served over https, so the helper does not install from it.");
        if (location.IsFile && TrustedPath.WhyUntrusted(location.LocalPath) is { } feedReason)
            return Refuse($"The package feed is not protected, so the helper does not install from it: {feedReason}");

        try
        {
            PkgFeed.ForgetCachedIndexes();
            var machine = RuntimeInformation.OSArchitecture;
            var candidates = PkgFeed.List(location, currentOnly: true).Where(i => i.Id == request.Id).ToArray();
            var manager = new MacPkg();
            var item = version.Length == 0
                ? PkgFeed.Latest(candidates, manager.CompareVersions, machine).FirstOrDefault()
                : candidates.Where(i => i.Version == version && PkgMetadata.ArchitectureFit(i.Architectures, machine) > 0)
                    // The same choice as the user's UniGetUI: the newest app among releases of that version.
                    .OrderByDescending(i => i, Comparer<PkgFeedItem>.Create((a, b) => PkgFeed.CompareReleases(a, b, manager.CompareVersions)))
                    .ThenByDescending(i => PkgMetadata.ArchitectureFit(i.Architectures, machine)).FirstOrDefault();
            if (item is null)
                return Refuse($"The feed of {request.Catalog} does not publish {request.Id} {version} for this Mac.");
            if (!item.Location.IsFile && item.Location.Scheme != Uri.UriSchemeHttps)
                return Refuse($"The package {item.Location} is not served over https, so the helper does not install it.");
            if (item.Location.IsFile && TrustedPath.WhyUntrusted(item.Location.LocalPath) is { } fileReason)
                return Refuse($"The package file is not protected, so the helper does not install it: {fileReason}");
            return await InstallItemAsync(item, feed.AllowUnsignedPackages, log);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException
            or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new PkgHelperMessage { Result = PkgHelperMessage.Failure, Message = ex.Message };
        }
    }

    /// <summary>Stages, checks and installs one feed package; this process must already run as root.</summary>
    internal static async Task<PkgHelperMessage> InstallItemAsync(PkgFeedItem item, bool allowUnsigned, Action<string> log)
    {
        try
        {
            using var prepared = await PkgInstaller.PrepareAsync(item, allowUnsigned, log, _ => { },
                CancellationToken.None);
            log(prepared.Description);
            var (code, output) = await PkgInstaller.RunProcess("/bin/sh", ["-c", prepared.Command], CancellationToken.None);
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                log(line);
            return code == 0
                ? new PkgHelperMessage { Result = PkgHelperMessage.Success, Message = $"{item.Id} {item.Version} is installed." }
                : new PkgHelperMessage { Result = PkgHelperMessage.Failure, Message = $"The installer exited with code {code}." };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException
            or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new PkgHelperMessage { Result = PkgHelperMessage.Failure, Message = ex.Message };
        }
    }

    private static bool IsField(string? value) =>
        value is { Length: > 0 and <= MaxFieldLength } && !value.Any(char.IsControl);
}
