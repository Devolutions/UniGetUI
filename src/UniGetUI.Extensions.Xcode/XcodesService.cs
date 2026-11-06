using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

/// <summary>
/// Installs, updates and removes Xcode through the xcodes command line tool (https://www.xcodes.app). It has no
/// user interface of its own, so the page and the tests can share it; the commands run through the runners it is
/// given, so the tests need no real xcodes.
/// </summary>
public sealed class XcodesService
{
    internal delegate Task<XcodesResult> CommandRunner(
        IReadOnlyList<string> arguments, TimeSpan? timeout, CancellationToken cancellationToken);

    internal delegate Task<XcodesResult> TerminalCommandRunner(
        IReadOnlyList<string> arguments, XcodesPromptHandler? prompts, Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken);

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Refreshing the list of releases downloads it, so it may take a while on a slow connection.</summary>
    private static readonly TimeSpan ReleasesTimeout = TimeSpan.FromMinutes(3);

    private readonly CommandRunner _run;
    private readonly TerminalCommandRunner _runInTerminal;
    private readonly Func<string?> _locate;

    public XcodesService()
        : this(new XcodesRunner(), new XcodesTerminalRunner())
    {
    }

    public XcodesService(XcodesRunner runner, XcodesTerminalRunner terminal, Func<string?>? locate = null)
        : this(runner.RunAsync, terminal.RunAsync, locate ?? XcodesLocator.Find)
    {
    }

    internal XcodesService(CommandRunner run, TerminalCommandRunner runInTerminal, Func<string?> locate)
    {
        _run = run;
        _runInTerminal = runInTerminal;
        _locate = locate;
    }

    /// <summary>Where xcodes is, or null when it is not installed. Looked up each time, so installing it needs no restart.</summary>
    public string? FindExecutable() => _locate();

    /// <summary>The installed xcodes' version, or null when there is none or it cannot be run.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            XcodesResult result = await _run(["version"], VersionTimeout, cancellationToken);
            if (result.Success)
                return XcodesOutputParser.ParseVersion(result.StandardOutput);
            Logger.Warn($"xcodes version failed: {XcodesError.Summarize(result.Output, result.ExitCode)}");
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"xcodes could not be run: {ex.Message}");
        }
        return null;
    }

    /// <summary>Every Xcode that can be installed on this Mac, newest first.</summary>
    /// <param name="refresh">Download the list again instead of using xcodes' copy of it.</param>
    public async Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        XcodesResult result = await _run([refresh ? "update" : "list"], ReleasesTimeout, cancellationToken);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));

        // A few versions are listed twice, with different builds; a build with the same architecture is one row.
        return XcodesOutputParser.ParseReleases(result.StandardOutput)
            .GroupBy(release => (release.Version, release.Build, release.Architecture))
            .Select(group => group.First())
            .OrderByDescending(release => release.Version, XcodeVersionComparer.Instance)
            .ToList();
    }

    /// <summary>The Xcode apps on this Mac, newest first.</summary>
    public async Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        XcodesResult result = await _run(["installed"], VersionTimeout, cancellationToken);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));

        return XcodesOutputParser.ParseInstalled(result.StandardOutput)
            .OrderByDescending(xcode => xcode.Version, XcodeVersionComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Downloads and installs an Xcode next to any others; it is never replaced. Asks <paramref name="prompts"/>
    /// whatever it needs to know: the Apple ID, a two-factor code, the macOS password.
    /// </summary>
    /// <param name="version">A version as <see cref="GetReleasesAsync"/> returns it, such as <c>26.4</c> or <c>26.1 Beta 2</c>.</param>
    public async Task InstallAsync(
        string version,
        XcodeInstallOptions options,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        // Every command takes the version first, so one that looked like an option could not be read as a version.
        version = RequireVersion(version);
        List<string> arguments = ["install", version];
        if (options.Select)
            arguments.Add("--select");
        if (options.SkipAdministratorSteps)
            arguments.Add("--no-superuser");
        if (options.DeleteArchive)
            arguments.Add("--empty-trash");
        if (options.FastUnarchive)
            arguments.Add("--experimental-unxip");

        await RunToCompletionAsync(arguments, prompts, output, cancellationToken);
    }

    /// <summary>Makes an installed Xcode the one command line tools use. This may ask for the macOS password.</summary>
    public async Task SelectAsync(
        string version,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
        => await RunToCompletionAsync(["select", RequireVersion(version)], prompts, output, cancellationToken);

    /// <summary>Makes this specific installed Xcode the one command line tools use.</summary>
    public async Task SelectAsync(
        InstalledXcode xcode,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xcode);
        await RunToCompletionAsync(["select", RequireInstalledPath(xcode.Path)], prompts, output, cancellationToken);
    }

    /// <summary>Moves an installed Xcode to the Trash, or deletes it.</summary>
    public async Task UninstallAsync(string version, bool deleteCompletely, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["uninstall", RequireVersion(version)];
        if (deleteCompletely)
            arguments.Add("--empty-trash");

        // It asks nothing when it is given the version of an Xcode that is there.
        XcodesResult result = await _run(arguments, ReleasesTimeout, cancellationToken);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
    }

    /// <summary>Forgets the Apple ID xcodes signed in with, and the password it kept in the keychain.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        XcodesResult result = await _run(["signout"], VersionTimeout, cancellationToken);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
    }

    /// <summary>
    /// The Xcode to offer as an update: the newest one xcodes lists that is newer than every Xcode installed. A
    /// beta is only offered when <paramref name="includePrereleases"/> says so. Nothing is offered to a Mac with no
    /// Xcode, since there is nothing there to update.
    /// </summary>
    public static XcodeRelease? FindUpdate(
        IEnumerable<XcodeRelease> releases,
        IReadOnlyCollection<InstalledXcode> installed,
        bool includePrereleases)
    {
        if (installed.Count == 0)
            return null;

        XcodeRelease[] all = [.. releases];
        XcodeRelease? newest = null;
        foreach (XcodeRelease release in all)
        {
            if (release.IsPrerelease && !includePrereleases)
                continue;
            if (newest is null || XcodeVersion.Compare(release.Version, newest.Version) > 0)
                newest = release;
        }
        if (newest is null)
            return null;

        // The list can have more than one row for a version, and xcodes marks only the one it matched as installed.
        if (all.Any(release => release.IsInstalled && XcodeVersion.Compare(release.Version, newest.Version) == 0))
            return null;

        string newestInstalled = installed
            .Select(xcode => xcode.Version)
            .Aggregate((left, right) => XcodeVersion.Compare(left, right) >= 0 ? left : right);
        return XcodeVersion.Compare(newest.Version, newestInstalled) > 0 ? newest : null;
    }

    private async Task RunToCompletionAsync(
        IReadOnlyList<string> arguments,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        XcodesResult result = await _runInTerminal(arguments, prompts, output, cancellationToken);
        if (!result.Success)
            throw new XcodesException(XcodesError.Summarize(result.Output, result.ExitCode));
    }

    private static string RequireVersion(string version)
    {
        string trimmed = version?.Trim() ?? "";
        if (!XcodeVersion.TryParse(trimmed, out _))
            throw new ArgumentException($"'{version}' is not an Xcode version.", nameof(version));
        return trimmed;
    }

    private static string RequireInstalledPath(string path)
    {
        string trimmed = path?.Trim() ?? "";
        if (!Path.IsPathFullyQualified(trimmed) || !trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{path}' is not an installed Xcode app path.", nameof(path));
        return trimmed;
    }

    private sealed class XcodeVersionComparer : IComparer<string>
    {
        public static readonly XcodeVersionComparer Instance = new();

        public int Compare(string? x, string? y) => XcodeVersion.Compare(x, y);
    }
}
