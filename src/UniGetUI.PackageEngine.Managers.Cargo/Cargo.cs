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

    internal static bool IsCargoBinaryPresent(string binaryName) =>
        CoreTools.Which(binaryName).Item1
        || GetCargoBinDirectories(
                Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            )
            .Any(directory => IsExecutableFile(Path.Join(directory, binaryName)));

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
        string cargoCommand = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        string cargoUpdateBinary = OperatingSystem.IsWindows()
            ? "cargo-install-update.exe"
            : "cargo-install-update";
        string cargoBinstallBinary = OperatingSystem.IsWindows()
            ? "cargo-binstall.exe"
            : "cargo-binstall";

        string CargoPath() =>
            Status.ExecutablePath is { Length: > 0 } path ? path : cargoCommand;

        Dependencies =
        [
            // Cargo-binstall is required to install and update cargo binaries
            new ManagerDependency(
                "cargo-binstall",
                cargoCommand,
                "install cargo-binstall --locked",
                "cargo install cargo-binstall --locked",
                async () => await Task.Run(() => IsCargoBinaryPresent(cargoBinstallBinary)),
                () => (CargoPath(), "install cargo-binstall --locked")
            ),
            // cargo-update is required to check for installed and upgradable packages
            new ManagerDependency(
                "cargo-update",
                cargoCommand,
                "install cargo-update --locked",
                "cargo install cargo-update --locked",
                async () => await Task.Run(() => IsCargoBinaryPresent(cargoUpdateBinary)),
                () => IsCargoBinaryPresent(cargoBinstallBinary)
                    ? (CargoPath(), "binstall --no-confirm cargo-update")
                    : (CargoPath(), "install cargo-update --locked")
            ),
        ];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = true,
            SupportsCustomVersions = true,
            SupportsCustomLocations = true,
            CanDownloadInstaller = true,
            SupportsProxy = ProxySupport.Partially,
            SupportsProxyAuth = true,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
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
            InstallVerb = "binstall",
            UninstallVerb = "uninstall",
            UpdateVerb = "binstall",
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
            DateTime startTime = DateTime.Now;

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

            if (i + 1 == Packages.Count)
                break;
            // Crates.io requires no more than one request per second
            Task.Delay(Math.Max(0, 1000 - (int)(DateTime.Now - startTime).TotalMilliseconds))
                .GetAwaiter()
                .GetResult();
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

    public readonly bool HasBinstall =
        IsCargoBinaryPresent(OperatingSystem.IsWindows() ? "cargo-binstall.exe" : "cargo-binstall");

    public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
        CoreTools.WhichMultiple(OperatingSystem.IsWindows() ? "cargo.exe" : "cargo");

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

    protected override void _loadManagerVersion(out string version)
    {
        using Process p = GetProcess(Status.ExecutablePath, "--version");
        p.Start();
        version = p.StandardOutput.ReadToEnd().Trim();
        string error = p.StandardError.ReadToEnd();
        if (!string.IsNullOrEmpty(error))
            Logger.Error("cargo version error: " + error);
    }

    public void InvalidateInstalledCache() =>
        TaskRecycler<List<CargoListEntry>>.RemoveFromCache(GetInstalledCommandOutput);

    private IReadOnlyList<Package> GetPackages(LoggableTaskType taskType)
    {
        List<Package> Packages = [];
        var entries = TaskRecycler<List<CargoListEntry>>.RunOrAttach(GetInstalledCommandOutput, 15);
        foreach (var entry in entries)
        {
            var name = CoreTools.FormatAsName(entry.Id);
            if (taskType is LoggableTaskType.ListUpdates)
            {
                if (
                    entry.NeedsUpdate
                    && entry.LatestVersion is { Length: > 0 } latestVersion
                    && latestVersion != entry.InstalledVersion
                )
                    Packages.Add(
                        new Package(
                            name,
                            entry.Id,
                            entry.InstalledVersion,
                            latestVersion,
                            DefaultSource,
                            this
                        )
                    );
            }
            else if (taskType is LoggableTaskType.ListInstalledPackages)
                Packages.Add(
                    new Package(name, entry.Id, entry.InstalledVersion, DefaultSource, this)
                );
        }
        return Packages;
    }

    private List<CargoListEntry> GetInstalledCommandOutput()
    {
        List<string> stdout = [];
        using Process p = GetProcess(Status.ExecutablePath, "install-update --list");
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.OtherTask, p);
        logger.AddToStdOut("Other task: Call the install-update command");
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            stdout.Add(line);
        }
        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();

        List<string> skippedRows = [];
        var output = ParseInstallUpdateList(stdout, skippedRows);
        foreach (var skippedRow in skippedRows)
            logger.AddToStdErr($"Ignored unrecognized `install-update --list` row: {skippedRow}");
        logger.Close(p.ExitCode);

        if (output.Count > 0)
            return output;

        List<string> fallbackStdout = [];
        using Process fallback = GetProcess(Status.ExecutablePath, "install --list");
        IProcessTaskLogger fallbackLogger = TaskLogger.CreateNew(
            LoggableTaskType.OtherTask,
            fallback
        );
        fallbackLogger.AddToStdOut(
            "Falling back to `cargo install --list` (cargo-update reported no packages)"
        );
        fallback.Start();
        while ((line = fallback.StandardOutput.ReadLine()) is not null)
        {
            fallbackLogger.AddToStdOut(line);
            fallbackStdout.Add(line);
        }
        fallbackLogger.AddToStdErr(fallback.StandardError.ReadToEnd());
        fallback.WaitForExit();
        fallbackLogger.Close(fallback.ExitCode);
        return ParseInstallList(fallbackStdout);
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
            },
        };
    }
}
