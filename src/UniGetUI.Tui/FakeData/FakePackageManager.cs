using UniGetUI.Core.IconEngine;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Tui.FakeData;

/// <summary>
/// A package manager backed entirely by <see cref="FakeCatalog"/> and the sandboxed
/// <see cref="FakeStateStore"/>. Listing, searching, details and versions are answered in-process;
/// operations spawn <see cref="FakePackageManagerProcess"/> through the regular operation engine.
/// </summary>
internal sealed class FakePackageManager : PackageManager
{
    private readonly FakeManagerProfile _profile;
    private readonly FakeDataEnvironment _env;

    public FakePackageManager(FakeManagerProfile profile, FakeDataEnvironment env)
    {
        _profile = profile;
        _env = env;

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = profile.CanRunAsAdmin,
            CanSkipIntegrityChecks = profile.CanSkipIntegrityChecks,
            CanRunInteractively = profile.CanRunInteractively,
            CanRemoveDataOnUninstall = true,
            CanDownloadInstaller = profile.CanDownloadInstaller,
            CanUninstallPreviousVersionsAfterUpdate = true,
            CanListDependencies = true,
            SupportsCustomVersions = profile.SupportsVersions,
            SupportsCustomArchitectures = profile.SupportsArchitectures,
            SupportedCustomArchitectures = profile.SupportsArchitectures ? ["x86", "x64", "arm64"] : [],
            SupportsCustomScopes = profile.SupportsScopes,
            SupportsPreRelease = profile.SupportsVersions,
            SupportsCustomLocations = true,
            SupportsCustomSources = profile.SupportsSources,
            SupportsCustomPackageIcons = false,
            SupportsCustomPackageScreenshots = false,
            SupportsProxy = ProxySupport.No,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.Yes,
            Sources = new SourceCapabilities { KnowsPackageCount = true, KnowsUpdateDate = true },
        };

        var defaultSource = new ManagerSource(this, profile.DefaultSourceName, new Uri(profile.DefaultSourceUrl));
        Properties = new ManagerProperties
        {
            Id = profile.Name,
            Name = profile.Name,
            DisplayName = profile.DisplayName,
            Description = profile.Description,
            IconId = IconType.Package,
            ColorIconId = profile.Name.ToLowerInvariant() + "_color",
            ExecutableFriendlyName = profile.ExecutableName,
            InstallVerb = "install",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            KnownSources = FakeCatalog.DefaultSources
                .Where(s => s.Manager == profile.Name)
                .Select(s => (IManagerSource)new ManagerSource(this, s.Name, new Uri(s.Url)))
                .ToArray(),
            DefaultSource = defaultSource,
        };

        DetailsHelper = new FakeDetailsHelper(this);
        OperationHelper = new FakeOperationHelper(this);
        if (profile.SupportsSources)
            SourcesHelper = new FakeSourceHelper(this);
    }

    internal FakeDataEnvironment Environment => _env;
    internal FakeManagerProfile Profile => _profile;

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
        => _profile.Found ? [_env.FakeCliPath] : [];

    protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
    {
        found = _profile.Found;
        path = found ? _env.FakeCliPath : "";
        callArguments = string.Join(' ', CallVector());
    }

    protected override IReadOnlyList<string> _getOperationCallArgs(string executablePath, string callArguments)
        => CallVector();

    private string[] CallVector()
        => [.. _env.FakeCliPrefixArgs, FakePackageManagerProcess.PmFlag, "--state", _env.StatePath, _profile.Name];

    protected override void _loadManagerVersion(out string version)
        => version = $"{_profile.DisplayName} fake 1.0.0 (sandboxed, fake data)";

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        var logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
        logger.Log($"Fake search for \"{query}\" on {_profile.DisplayName}");
        string q = query.Trim();
        var results = FakeCatalog.Packages
            .Where(p => p.Manager == _profile.Name)
            .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || p.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new Package(p.Name, p.Id, p.LatestVersion, SourceFor(DefaultSourceName(p)), this))
            .ToList();
        logger.Log($"{results.Count} result(s)");
        logger.Close(0);
        return results;
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        var logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);
        var list = new List<Package>();
        foreach (FakeInstalledPackage installed in InstalledHere())
        {
            FakeCatalogPackage? catalog = FakeCatalog.Find(_profile.Name, installed.Id);
            if (catalog is null) continue;
            if (CompareVersions(installed.Version, catalog.LatestVersion) is < 0)
            {
                list.Add(new Package(catalog.Name, catalog.Id, installed.Version, catalog.LatestVersion,
                    SourceFor(installed.Source), this));
                logger.Log($"{catalog.Id} {installed.Version} -> {catalog.LatestVersion}");
            }
        }

        logger.Close(0);
        return list;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        var logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages);
        var list = new List<Package>();
        foreach (FakeInstalledPackage installed in InstalledHere())
        {
            FakeCatalogPackage? catalog = FakeCatalog.Find(_profile.Name, installed.Id);
            list.Add(new Package(catalog?.Name ?? installed.Id, installed.Id, installed.Version,
                SourceFor(installed.Source), this));
            logger.Log($"{installed.Id} {installed.Version}");
        }

        logger.Close(0);
        return list;
    }

    private IEnumerable<FakeInstalledPackage> InstalledHere()
        => FakeStateStore.Read(_env.StatePath).Installed.Where(p => p.Manager == _profile.Name);

    private string DefaultSourceName(FakeCatalogPackage package)
        => package.Id.StartsWith("Contoso.Notes", StringComparison.Ordinal) ? "msstore" : _profile.DefaultSourceName;

    internal IManagerSource SourceFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultSource;
        if (SourcesHelper is FakeSourceHelper) return SourcesHelper.Factory.GetSourceOrDefault(name);
        return DefaultSource;
    }

    public override void RefreshPackageIndexes()
    {
        var logger = TaskLogger.CreateNew(LoggableTaskType.RefreshIndexes);
        logger.Log($"Fake index refresh for {_profile.DisplayName} (nothing to download)");
        logger.Close(0);
    }

    /// <summary>The fake install directory for a package, created inside the sandbox on demand.</summary>
    internal string InstallDirectoryFor(IPackage package)
    {
        string dir = Path.Join(_env.SandboxDirectory, "FakeInstallRoot", _profile.Name, CoreSafe(package.Id));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CoreSafe(string id)
        => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

internal sealed class FakeDetailsHelper(FakePackageManager manager) : BasePkgDetailsHelper(manager)
{
    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        FakeCatalogPackage? p = FakeCatalog.Find(manager.Name, details.Package.Id);
        if (p is null) return;
        details.Description = p.Description;
        details.Publisher = p.Publisher;
        details.Author = p.Publisher;
        details.HomepageUrl = new Uri(p.Homepage);
        details.License = p.License;
        details.LicenseUrl = new Uri($"{p.Homepage}/license");
        details.InstallerUrl = new Uri($"{p.Homepage}/{p.LatestVersion}/installer.{p.InstallerType}");
        details.InstallerHash = FakeHash(p.Id + p.LatestVersion);
        details.InstallerType = p.InstallerType;
        details.InstallerSize = p.InstallerSize;
        details.ManifestUrl = new Uri($"{p.Homepage}/manifest.yaml");
        details.UpdateDate = "2026-09-01";
        details.ReleaseNotes = $"What's new in {p.LatestVersion}:\n- Fake improvement one\n- Fake bug fix two";
        details.ReleaseNotesUrl = new Uri($"{p.Homepage}/releases/{p.LatestVersion}");
        details.Tags = p.Tags;
        foreach (string dep in p.Dependencies)
            details.Dependencies.Add(new IPackageDetails.Dependency { Name = dep, Version = "*", Mandatory = true });
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => FakeCatalog.Find(manager.Name, package.Id)?.Versions.Reverse().ToArray() ?? [];

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package) => null;

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package) => [];

    protected override string? GetInstallLocation_UnSafe(IPackage package) => manager.InstallDirectoryFor(package);

    private static string FakeHash(string seed)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

internal sealed class FakeOperationHelper(FakePackageManager manager) : BasePkgOperationHelper(manager)
{
    protected override IReadOnlyList<string> _getOperationParameters(IPackage package, InstallOptions options,
        OperationType operation)
    {
        List<string> args =
        [
            operation switch
            {
                OperationType.Install => "install",
                OperationType.Update => "update",
                _ => "uninstall",
            },
            "--id", package.Id,
        ];

        if (operation is not OperationType.Uninstall)
        {
            if (options.Version.Length > 0) args.AddRange(["--version", options.Version]);
            if (!package.Source.IsVirtualManager && package.Source.Name.Length > 0)
                args.AddRange(["--source", package.Source.Name]);
            if (options.Architecture.Length > 0) args.AddRange(["--arch", options.Architecture]);
            if (options.CustomInstallLocation.Length > 0) args.AddRange(["--location", options.CustomInstallLocation]);
            if (options.SkipHashCheck) args.Add("--skip-hash");
            if (options.PreRelease) args.Add("--pre-release");
        }
        else if (options.RemoveDataOnUninstall)
        {
            args.Add("--remove-data");
        }

        if (options.InstallationScope.Length > 0) args.AddRange(["--scope", options.InstallationScope]);
        if (options.InteractiveInstallation) args.Add("--interactive");

        args.AddRange(operation switch
        {
            OperationType.Update => options.CustomParameters_Update,
            OperationType.Uninstall => options.CustomParameters_Uninstall,
            _ => options.CustomParameters_Install,
        });
        return args;
    }

    protected override OperationVeredict _getOperationResult(IPackage package, OperationType operation,
        IReadOnlyList<string> processOutput, int returnCode)
        => returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;
}

internal sealed class FakeSourceHelper(FakePackageManager manager) : BaseSourceHelper(manager)
{
    public override string[] GetAddSourceParameters(IManagerSource source)
        => ["source-add", "--name", source.Name, "--url", source.Url.ToString()];

    public override string[] GetRemoveSourceParameters(IManagerSource source)
        => ["source-remove", "--name", source.Name];

    protected override OperationVeredict _getAddSourceOperationVeredict(IManagerSource source, int ReturnCode,
        string[] Output) => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override OperationVeredict _getRemoveSourceOperationVeredict(IManagerSource source, int ReturnCode,
        string[] Output) => ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        FakeState state = FakeStateStore.Read(manager.Environment.StatePath);
        return state.Sources
            .Where(s => s.Manager == manager.Name)
            .Select(s => (IManagerSource)new ManagerSource(manager, s.Name, new Uri(s.Url),
                packageCount: FakeCatalog.Packages.Count(p => p.Manager == manager.Name),
                updateDate: "2026-09-01"))
            .ToList();
    }
}
