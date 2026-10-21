using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

public partial class Cargo : PackageManager
{
    [GeneratedRegex(@"([\w-]+)\s=\s""(\d+\.\d+\.\d+)""\s*#\s(.*)")]
    private static partial Regex SearchLineRegex();

    public override bool InstallerUrlFollowsPackageVersion => true;

    internal static IReadOnlyList<string> GetCargoBinDirectories(
        Func<string, string?> readEnvironmentVariable,
        string userProfileDirectory
    )
    {
        List<string> directories = [];

        if (readEnvironmentVariable("CARGO_HOME")?.Trim() is { Length: > 0 } cargoHome)
            directories.Add(Path.Join(cargoHome, "bin"));
        else if (userProfileDirectory.Trim() is { Length: > 0 } userProfile)
            directories.Add(Path.Join(userProfile, ".cargo", "bin"));

        return directories;
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
            return false;

        if (OperatingSystem.IsWindows())
            return true;

        const UnixFileMode ExecutableBits =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        try
        {
            return (File.GetUnixFileMode(path) & ExecutableBits) is not 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public Cargo()
    {
        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = false,
            SupportsCustomVersions = true,
            SupportsCustomLocations = true,
            CanDownloadInstaller = true,
            SupportsProxy = ProxySupport.Partially,
            SupportsProxyAuth = true,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            SerializesOperations = true,
        };

        var cratesIo = new ManagerSource(this, "crates.io", new Uri("https://index.crates.io/"));

        Properties = new ManagerProperties
        {
            Id = "cargo",
            Name = "Cargo",
            Description = CoreTools.Translate(
                "The Rust package manager.<br>Contains: <b>Rust libraries and programs written in Rust</b>"
            ),
            IconId = IconType.Rust,
            ColorIconId = "cargo_color",
            ExecutableFriendlyName = "cargo.exe",
            InstallVerb = "install",
            UninstallVerb = "uninstall",
            UpdateVerb = "install",
            DefaultSource = cratesIo,
            KnownSources = [cratesIo],
        };

        DetailsHelper = new CargoPkgDetailsHelper(this);
        OperationHelper = new CargoPkgOperationHelper(this);
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        using Process p = GetProcess(Status.ExecutablePath, "search -q --color=never " + query);
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages, p);
        p.Start();

        string? line;
        List<Package> Packages = [];
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var match = SearchLineRegex().Match(line);
            if (match.Success)
            {
                var id = match.Groups[1].Value;
                var version = match.Groups[2].Value;
                Packages.Add(
                    new Package(CoreTools.FormatAsName(id), id, version, DefaultSource, this)
                );
            }
        }

        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();

        List<Package> BinPackages = [];

        for (int i = 0; i < Packages.Count; i++)
        {
            var package = Packages[i];
            try
            {
                var versionInfo = CratesIOClient.GetManifestVersion(
                    package.Id,
                    package.VersionString
                );
                if (versionInfo.bin_names?.Length > 0)
                    BinPackages.Add(package);
            }
            catch (Exception ex)
            {
                // On API failure, include the package rather than silently drop it
                logger.AddToStdErr($"bin_names check failed for {package.Id}: {ex.Message}");
                BinPackages.Add(package);
            }

        }

        logger.Close(p.ExitCode);

        return [.. BinPackages];
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        return GetPackages(LoggableTaskType.ListUpdates);
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        return GetPackages(LoggableTaskType.ListInstalledPackages);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        string name = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        return CoreTools.WhichMultiple(name)
            .Concat(GetCargoBinDirectories(Environment.GetEnvironmentVariable,
                CargoConfiguration.WorkingDirectory).Select(directory => Path.Join(directory, name))
                .Where(IsExecutableFile))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments
    )
    {
        var (_found, _executablePath) = GetExecutableFile();
        found = _found;
        path = _executablePath;
        callArguments = "";
    }

    public override int? CompareVersions(string versionA, string versionB)
    {
        if (
            SemanticVersion.TryParse(versionA, out SemanticVersion parsedA)
            && SemanticVersion.TryParse(versionB, out SemanticVersion parsedB)
        )
            return parsedA.CompareTo(parsedB);

        return base.CompareVersions(versionA, versionB);
    }

    protected override void _loadManagerVersion(out string version)
    {
        using Process p = GetProcess(Status.ExecutablePath, "--version");
        p.Start();
        version = p.StandardOutput.ReadToEnd().Trim();
        string error = p.StandardError.ReadToEnd();
        if (!string.IsNullOrEmpty(error))
            Logger.Error("cargo version error: " + error);
    }

    public void InvalidateInstalledCache()
    {
        TaskRecycler<List<CargoListEntry>>.RemoveFromCache(GetInstalledCommandOutput);
        CratesIOClient.InvalidateCache();
    }

    private IReadOnlyList<Package> GetPackages(LoggableTaskType taskType) =>
        GetPackages(TaskRecycler<List<CargoListEntry>>.RunOrAttach(GetInstalledCommandOutput, 15), taskType);

    internal IReadOnlyList<Package> GetPackages(
        IEnumerable<CargoListEntry> entries,
        LoggableTaskType taskType)
    {
        List<Package> Packages = [];
        foreach (var entry in entries)
        {
            var name = CoreTools.FormatAsName(entry.Id);
            var source = DefaultSource;
            if (!entry.IsCratesIo && entry.SourceDescription is { } description)
            {
                string location = description.StartsWith("registry+", StringComparison.Ordinal)
                    ? description["registry+".Length..]
                    : description.StartsWith("git+", StringComparison.Ordinal)
                        ? description["git+".Length..] : description;
                if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? sourceUrl))
                {
                    sourceUrl = new Uri("about:blank");
                    Logger.Debug(
                        $"Cargo source {description} is a display label without a direct URL; preserving its source identity.");
                }
                source = new ManagerSource(this, description, sourceUrl);
            }
            if (taskType is LoggableTaskType.ListUpdates)
            {
                if (!entry.IsCratesIo)
                {
                    Logger.Debug($"Skipping crates.io update detection for {entry.Id} from {entry.SourceDescription}.");
                    continue;
                }
                var (_, manifest) = CratesIOClient.GetManifest(entry.Id);
                if (CratesIOClient.SelectUpdate(manifest, entry.InstalledVersion) is { } update)
                    Packages.Add(
                        new Package(
                            name,
                            entry.Id,
                            entry.InstalledVersion,
                            update.num,
                            DefaultSource,
                            this
                        )
                    );
            }
            else if (taskType is LoggableTaskType.ListInstalledPackages)
                Packages.Add(
                    new Package(name, entry.Id, entry.InstalledVersion, source, this)
                );
        }
        return Packages;
    }

    private List<CargoListEntry> GetInstalledCommandOutput()
    {
        List<string> stdout = [];
        CargoInstallTracker.RecoverAsync(CargoConfiguration.GetRoot(), CancellationToken.None)
            .GetAwaiter().GetResult();
        using Process p = GetProcess(Status.ExecutablePath, "install --list");
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.OtherTask, p);
        p.Start();
        Task<string> stderr = p.StandardError.ReadToEndAsync();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            stdout.Add(line);
        }
        logger.AddToStdErr(stderr.GetAwaiter().GetResult());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Cargo installed-package listing failed with exit code {p.ExitCode}.");
        return ParseInstallList(stdout);
    }

    private string? _hostTarget;

    internal async Task<string?> GetHostTargetAsync(CancellationToken cancellationToken)
    {
        if (_hostTarget is not null)
            return _hostTarget;
        using Process process = GetProcess(Status.ExecutablePath, "--version --verbose");
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Debug($"Cargo host detection already exited during cancellation: {ex.Message}");
            }
        });
        await process.WaitForExitAsync(cancellationToken);
        string error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Cargo host detection failed: {error}");
        _hostTarget = (await stdout).Split('\n')
            .FirstOrDefault(line => line.StartsWith("host: ", StringComparison.Ordinal))?["host: ".Length..].Trim();
        return _hostTarget;
    }

    private Process GetProcess(string fileName, string extraArguments)
    {
        return new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = Status.ExecutableCallArgs + " " + extraArguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = CargoConfiguration.WorkingDirectory,
            },
        };
    }
}
