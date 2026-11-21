using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.Generic.PyPI;
using UniGetUI.PackageEngine.Managers.UvManager.Helpers;
using UniGetUI.PackageEngine.Managers.UvManager.Models;
using UniGetUI.PackageEngine.Managers.UvManager.Services;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.UvManager;

public sealed class Uv : PackageManager
{
    public Uv()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanUninstall = true,
            CanDownloadInstaller = true,
            SupportsCustomVersions = true,
            SupportsPreRelease = true,
            SerializesOperations = true,
            // Installed and update rows are all labelled machine; uv tool list does not say where a tool lives
            ReportsReliableInstalledScope = false,
            SupportsCustomPythonInterpreters = true,
            SupportsProxy = ProxySupport.Yes,
            SupportsProxyAuth = true,
        };

        Properties = new ManagerProperties
        {
            Id = "uv",
            Name = "Uv",
            SetupGuide = "PACKAGE_MANAGERS.md#uv",
            InstallCommands = new(
                Windows: "winget install --exact --id astral-sh.uv",
                MacOS: "brew install uv"
            ),
            Description = CoreTools.Translate(
                "Astral's uv tool runner. Installs Python command-line tools into isolated tool environments<br>Contains: <b>Python CLI tools</b>"
            ),
            IconId = IconType.Uv,
            ColorIconId = "uv_color",
            ExecutableFriendlyName = "uv tool",
            InstallVerb = "install",
            UpdateVerb = "upgrade",
            UninstallVerb = "uninstall",
            DefaultSource = new ManagerSource(this, "uv", new Uri("https://pypi.org/")),
            KnownSources = [new ManagerSource(this, "uv", new Uri("https://pypi.org/"))],
        };

        DetailsHelper = new UvPkgDetailsHelper(this);
        OperationHelper = new UvPkgOperationHelper(this);
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
        try
        {
            string[] matches = PyPIIndexClient.SelectSearchMatches(
                query,
                PyPIIndexClient.GetProjectNames(logger)
            );
            logger.Log($"Matched {matches.Length} packages for query '{query}'");

            Dictionary<string, string?> latestVersions = PyPIProjectClient.FetchLatestVersions(matches);
            return ParseSearchMatches(matches, latestVersions, DefaultSource, this);
        }
        catch (Exception e)
        {
            logger.Error(e);
            logger.Close(1);
            throw;
        }
    }

    internal static IReadOnlyList<Package> ParseSearchMatches(
        IReadOnlyList<string> matches,
        IReadOnlyDictionary<string, string?> latestVersions,
        IManagerSource source,
        PackageManager manager
    )
    {
        List<Package> packages = [];
        for (int i = 0; i < matches.Count; i++)
        {
            string version = latestVersions.GetValueOrDefault(matches[i]) ?? "latest";
            packages.Add(
                new Package(
                    CoreTools.FormatAsName(matches[i]),
                    matches[i],
                    version,
                    source,
                    manager,
                    new(PackageScope.Global)
                )
            );
        }
        return packages;
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);
        try
        {
            IReadOnlyList<UvTool> tools = ListTools(
                "--outdated --show-python --show-version-specifiers",
                LoggableTaskType.ListUpdates
            );

            foreach (UvTool tool in tools)
            {
                if (tool.LatestVersion is not null && tool.IsPinnedToExactVersion)
                    logger.Log(
                        $"{tool.PackageId} {tool.Version} is pinned to {tool.VersionSpecifier}, "
                            + $"reinstall it to move to {tool.LatestVersion}"
                    );
            }

            IReadOnlyList<Package> packages = ParseAvailableUpdates(tools, DefaultSource, this);

            logger.Log($"Found {packages.Count} available updates");
            logger.Close(0);
            return packages;
        }
        catch (Exception e)
        {
            logger.Error(e);
            logger.Close(1);
            throw;
        }
    }

    internal static IReadOnlyList<Package> ParseAvailableUpdates(
        IEnumerable<UvTool> tools,
        IManagerSource source,
        PackageManager manager
    )
    {
        List<Package> packages = [];
        foreach (UvTool tool in tools)
        {
            if (tool.LatestVersion is null)
                continue;

            if (tool.IsPinnedToExactVersion)
                continue;

            if (manager.CompareVersions(tool.Version, tool.LatestVersion) is null or >= 0)
                continue;

            packages.Add(
                new Package(
                    CoreTools.FormatAsName(tool.PackageId),
                    tool.PackageId,
                    tool.Version,
                    tool.LatestVersion,
                    source,
                    manager,
                    new(PackageScope.Global)
                )
            );
        }

        return packages;
    }

    internal static IReadOnlyList<Package> ParseInstalledTools(
        IEnumerable<UvTool> tools,
        IManagerSource source,
        PackageManager manager
    )
    {
        List<Package> packages = [];
        foreach (UvTool tool in tools)
        {
            packages.Add(
                new Package(
                    CoreTools.FormatAsName(tool.PackageId),
                    tool.PackageId,
                    tool.Version,
                    source,
                    manager,
                    new(PackageScope.Global)
                )
            );
        }
        return packages;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages);
        try
        {
            IReadOnlyList<UvTool> tools = ListTools(
                "--show-python --show-version-specifiers",
                LoggableTaskType.ListInstalledPackages
            );

            IReadOnlyList<Package> packages = ParseInstalledTools(tools, DefaultSource, this);

            logger.Log($"Found {packages.Count} installed tools");
            logger.Close(0);
            return packages;
        }
        catch (Exception e)
        {
            logger.Error(e);
            logger.Close(1);
            throw;
        }
    }

    internal IReadOnlyList<UvTool> ListTools(string listArguments, LoggableTaskType taskType)
        => UvToolListParser.Parse(RunUvCli($"list {listArguments}", taskType));

    internal IReadOnlyList<UvPythonInstall> ListPythonInterpreters(
        string listArguments,
        LoggableTaskType taskType
    ) => UvPythonService.ParseJson(
        string.Join(
            '\n',
            RunUvCli($"python list {listArguments} --output-format json", taskType, toolScoped: false)
        )
    );

    /// <summary>
    /// Runs the uv executable. Tool subcommands run under <c>uv tool</c>, which the manager's
    /// call arguments already name; <c>uv python</c> is top level, so it must not carry them.
    /// </summary>
    internal IReadOnlyList<string> RunUvCli(
        string arguments,
        LoggableTaskType taskType,
        bool toolScoped = true
    )
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = (toolScoped ? Status.ExecutableCallArgs : "") + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };

        IProcessTaskLogger processLogger = TaskLogger.CreateNew(taskType, process);

        process.Start();
        RegisterListingProcess(process);

        string? line;
        List<string> outputLines = [];
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            processLogger.AddToStdOut(line);
            outputLines.Add(line);
        }

        processLogger.AddToStdErr(process.StandardError.ReadToEnd());
        process.WaitForExit();
        processLogger.Close(process.ExitCode);

        return outputLines;
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
        CoreTools.WhichMultiple("uv");

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments
    )
    {
        var (executableFound, executablePath) = GetExecutableFile();
        found = executableFound;
        path = executableFound ? executablePath : "";
        callArguments = "tool ";
    }

    protected override void _loadManagerVersion(out string version)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Status.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };
        process.Start();
        version = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
    }

    protected override void _performExtraLoadingSteps()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1", EnvironmentVariableTarget.Process);

        _ = PyPIIndexClient.PreWarmIndexAsync();
    }

    public override int? CompareVersions(string versionA, string versionB)
    {
        if (
            PythonVersion.TryParse(versionA, out PythonVersion parsedA)
            && PythonVersion.TryParse(versionB, out PythonVersion parsedB)
        )
            return parsedA.CompareTo(parsedB);

        return base.CompareVersions(versionA, versionB);
    }

    protected override IReadOnlyList<string> ListVersionsForUpdateSelection(IPackage update, bool includePreReleases) =>
        includePreReleases && DetailsHelper is UvPkgDetailsHelper uv
            ? uv.GetVersionsIncludingPreReleases(update)
            : base.ListVersionsForUpdateSelection(update, includePreReleases);
}
