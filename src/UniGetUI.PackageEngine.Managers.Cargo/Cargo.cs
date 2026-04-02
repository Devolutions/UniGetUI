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

    [GeneratedRegex(@"(.+)v(\d+\.\d+\.\d+)\s*v(\d+\.\d+\.\d+)\s*(Yes|No)")]
    private static partial Regex UpdateLineRegex();

    // Matches "ripgrep v15.1.0:" lines from `cargo install --list`
    [GeneratedRegex(@"^([\w-]+)\s+v(\d+\.\d+\.\d+):")]
    private static partial Regex InstallListLineRegex();

    public Cargo()
    {
        string cargoCommand = OperatingSystem.IsWindows() ? "cargo.exe" : "cargo";
        string cargoUpdateBinary = OperatingSystem.IsWindows()
            ? "cargo-install-update.exe"
            : "cargo-install-update";
        string cargoBinstallBinary = OperatingSystem.IsWindows()
            ? "cargo-binstall.exe"
            : "cargo-binstall";

        Dependencies =
        [
            // cargo-update is required to check for installed and upgradable packages
            new ManagerDependency(
                "cargo-update",
                cargoCommand,
                "install cargo-update",
                "cargo install cargo-update",
                async () => (await CoreTools.WhichAsync(cargoUpdateBinary)).Item1
            ),
            // Cargo-binstall is required to install and update cargo binaries
            new ManagerDependency(
                "cargo-binstall",
                cargoCommand,
                "install cargo-binstall",
                "cargo install cargo-binstall",
                async () => (await CoreTools.WhichAsync(cargoBinstallBinary)).Item1
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
        };

        var cratesIo = new ManagerSource(this, "crates.io", new Uri("https://index.crates.io/"));

        Properties = new ManagerProperties
        {
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
        CoreTools.Which(OperatingSystem.IsWindows() ? "cargo-binstall.exe" : "cargo-binstall").Item1;

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
        TaskRecycler<List<Match>>.RemoveFromCache(GetInstalledCommandOutput);

    private IReadOnlyList<Package> GetPackages(LoggableTaskType taskType)
    {
        List<Package> Packages = [];
        foreach (var match in TaskRecycler<List<Match>>.RunOrAttach(GetInstalledCommandOutput, 15))
        {
            var id = match.Groups[1]?.Value?.Trim() ?? "";
            var name = CoreTools.FormatAsName(id);
            var oldVersion = match.Groups[2]?.Value?.Trim() ?? "";
            var newVersion = match.Groups[3]?.Value?.Trim() ?? "";
            if (taskType is LoggableTaskType.ListUpdates && oldVersion != newVersion)
                Packages.Add(new Package(name, id, oldVersion, newVersion, DefaultSource, this));
            else if (taskType is LoggableTaskType.ListInstalledPackages)
                Packages.Add(new Package(name, id, oldVersion, DefaultSource, this));
        }
        return Packages;
    }

    private List<Match> GetInstalledCommandOutput()
    {
        List<Match> output = [];
        using Process p = GetProcess(Status.ExecutablePath, "install-update --list");
        IProcessTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.OtherTask, p);
        logger.AddToStdOut("Other task: Call the install-update command");
        p.Start();

        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            var match = UpdateLineRegex().Match(line);
            if (match.Success)
                output.Add(match);
        }
        logger.AddToStdErr(p.StandardError.ReadToEnd());
        p.WaitForExit();
        logger.Close(p.ExitCode);

        if (output.Count > 0)
            return output;

        // Fallback: cargo-update is not installed, use the built-in `cargo install --list`.
        // No latest-version info is available, so updates won't be detected, but the installed
        // packages list will be populated correctly.
        using Process fallback = GetProcess(Status.ExecutablePath, "install --list");
        IProcessTaskLogger fallbackLogger = TaskLogger.CreateNew(LoggableTaskType.OtherTask, fallback);
        fallbackLogger.AddToStdOut("Falling back to `cargo install --list` (cargo-update not available)");
        fallback.Start();
        while ((line = fallback.StandardOutput.ReadLine()) is not null)
        {
            fallbackLogger.AddToStdOut(line);
            var m = InstallListLineRegex().Match(line);
            if (!m.Success) continue;
            // Synthesise a match compatible with UpdateLineRegex (same installed and latest version → no update)
            var fake = UpdateLineRegex().Match($"{m.Groups[1].Value} v{m.Groups[2].Value} v{m.Groups[2].Value} No");
            if (fake.Success)
                output.Add(fake);
        }
        fallbackLogger.AddToStdErr(fallback.StandardError.ReadToEnd());
        fallback.WaitForExit();
        fallbackLogger.Close(fallback.ExitCode);
        return output;
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
