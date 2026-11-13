using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.AurManager;

public class Aur : PackageManager
{
    internal static readonly string[] HelperNames = ["paru", "yay"];

    internal const string DefaultFriendlyName = "paru";

    private volatile AurHelperKind _helperKind = AurHelperKind.Unknown;
    private volatile string _pacmanPath = "/usr/bin/pacman";

    public Aur()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = false,
            MustNotRunElevated = true,
            ElevatesItself = true,
            CanListDependencies = true,
            CanSkipIntegrityChecks = false,
            SupportsCustomSources = false,
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.No,
        };

        Properties = new ManagerProperties
        {
            Id = "aur",
            Name = "AUR",
            SetupGuide = "PACKAGE_MANAGERS.md#aur",
            Description = CoreTools.Translate(
                "Community packages from the Arch User Repository, built through paru or yay. AUR packages are user-produced and not reviewed by Arch Linux: check who maintains a package and read its PKGBUILD before installing it.<br>Contains: <b>AUR packages</b>"
            ),
            IconId = IconType.Pacman,
            ColorIconId = "pacman",
            ExecutableFriendlyName = DefaultFriendlyName,
            InstallVerb = "-S",
            UpdateVerb = "-S",
            UninstallVerb = "-Rs",
            DefaultSource = new ManagerSource(this, "aur", new Uri("https://aur.archlinux.org/packages/")),
            KnownSources = [new ManagerSource(this, "aur", new Uri("https://aur.archlinux.org/packages/"))],
        };

        DetailsHelper = new AurPkgDetailsHelper(this);
        OperationHelper = new AurPkgOperationHelper(this);
    }

    internal AurHelperKind HelperKind => _helperKind;

    internal string PacmanPath => _pacmanPath;

    public override int? CompareVersions(string versionA, string versionB) =>
        AlpmVersion.Compare(versionA, versionB);

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        var candidates = new List<string>();
        foreach (var name in HelperNames)
        {
            foreach (var path in CoreTools.WhichMultiple(name))
            {
                if (!candidates.Contains(path))
                    candidates.Add(path);
            }

            foreach (var dir in new[] { "/usr/bin", "/usr/local/bin" })
            {
                var path = Path.Join(dir, name);
                if (File.Exists(path) && !candidates.Contains(path))
                    candidates.Add(path);
            }
        }
        return candidates;
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments)
    {
        (found, path) = GetExecutableFile();
        callArguments = "";

        bool runningAsRoot = CoreTools.IsAdministrator();
        if (found && runningAsRoot)
            Logger.Warn("AUR helpers refuse to build packages as root, so the AUR manager is unavailable while UniGetUI runs as root.");

        (found, string friendlyName) = ResolveExecutable(found, path, runningAsRoot);
        if (Properties.ExecutableFriendlyName != friendlyName)
        {
            var properties = Properties;
            properties.ExecutableFriendlyName = friendlyName;
            Properties = properties;
        }

        _pacmanPath = CoreTools.WhichMultiple("pacman").FirstOrDefault() ?? "/usr/bin/pacman";
    }

    protected override void _loadManagerVersion(out string version)
    {
        using var p = CreateListingProcess(["--version"]);
        p.Start();
        var stderr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        stderr.Wait();
        p.WaitForExit();

        _helperKind = AurOutputParser.DetectHelper(output, Status.ExecutablePath);
        if (_helperKind is AurHelperKind.Unknown)
            Logger.Warn($"Could not tell whether {Status.ExecutablePath} is paru or yay; AUR operations will only pass the options both understand.");
        version = AurOutputParser.ParseHelperVersion(output);
    }

    internal static (bool Found, string FriendlyName) ResolveExecutable(bool found, string path, bool runningAsRoot)
    {
        if (!found || runningAsRoot)
            return (false, DefaultFriendlyName);
        return (true, Path.GetFileName(path));
    }

    internal static int ListingExitCode(int exitCode, int resultCount, string stderr)
    {
        bool onlyWarnings = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(line => line.StartsWith("warning:", StringComparison.OrdinalIgnoreCase));
        return exitCode == 1 && resultCount == 0 && onlyWarnings ? 0 : exitCode;
    }

    internal Process CreateListingProcess(IEnumerable<string> arguments, string? executable = null)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable ?? Status.ExecutablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
            p.StartInfo.ArgumentList.Add(argument);
        p.StartInfo.Environment["LANG"] = "C";
        p.StartInfo.Environment["LC_ALL"] = "C";
        p.StartInfo.Environment.Remove("LANGUAGE");
        return p;
    }

    internal List<T> RunListing<T>(
        IEnumerable<string> arguments,
        LoggableTaskType taskType,
        Func<string, List<T>> parse,
        string? executable = null)
    {
        using var p = CreateListingProcess(arguments, executable);
        IProcessTaskLogger logger = TaskLogger.CreateNew(taskType, p);
        p.Start();
        RegisterListingProcess(p);
        var stderr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        logger.AddToStdOut(output);
        string errors = stderr.Result;
        logger.AddToStdErr(errors);
        p.WaitForExit();
        var results = parse(output);
        int exitCode = ListingExitCode(p.ExitCode, results.Count, errors);
        logger.Close(exitCode);
        if (exitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(p.StartInfo.FileName)} exited with code {exitCode}: {errors.Trim()}");
        return results;
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        if (query.TrimStart().StartsWith('-'))
            return [];

        return RunListing(
            ["-Ss", "--aur", "--color=never", query],
            LoggableTaskType.FindPackages,
            AurOutputParser.ParseSearch)
            .Select(r => new Package(CoreTools.FormatAsName(r.Id), r.Id, r.Version, Properties.DefaultSource!, this))
            .ToList();
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        return RunListing(
            ["-Qm", "--color=never"],
            LoggableTaskType.ListInstalledPackages,
            AurOutputParser.ParseInstalled,
            PacmanPath)
            .Select(r => new Package(CoreTools.FormatAsName(r.Id), r.Id, r.Version, Properties.DefaultSource!, this))
            .ToList();
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        return RunListing(
            ["-Qua", "--color=never"],
            LoggableTaskType.ListUpdates,
            AurOutputParser.ParseUpdates)
            .Select(r => new Package(
                CoreTools.FormatAsName(r.Id), r.Id, r.InstalledVersion, r.NewVersion, Properties.DefaultSource!, this))
            .ToList();
    }
}
