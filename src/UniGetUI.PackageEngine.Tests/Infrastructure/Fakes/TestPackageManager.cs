using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

public sealed class TestPackageManager : PackageManager
{
    private Func<string, IReadOnlyList<Package>> _findPackages = _ => [];
    private Func<IReadOnlyList<Package>> _getAvailableUpdates = static () => [];
    private Func<IReadOnlyList<Package>> _getInstalledPackages = static () => [];
    private IReadOnlyList<string> _candidateExecutableFiles;

    public TestPackageManager(string name = "TestManager", string? displayName = null)
    {
        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = true,
            CanSkipIntegrityChecks = true,
            CanRunInteractively = true,
            CanRemoveDataOnUninstall = true,
            CanDownloadInstaller = true,
            CanUninstallPreviousVersionsAfterUpdate = true,
            CanListDependencies = true,
            SupportsCustomVersions = true,
            SupportsCustomArchitectures = true,
            SupportsCustomScopes = true,
            SupportsPreRelease = true,
            SupportsCustomLocations = true,
            SupportsCustomSources = true,
            SupportsCustomPackageIcons = true,
            SupportsCustomPackageScreenshots = true,
            SupportsProxy = ProxySupport.Yes,
            SupportsProxyAuth = true,
            Sources = new SourceCapabilities
            {
                KnowsPackageCount = true,
                KnowsUpdateDate = true,
            },
        };

        Properties = new ManagerProperties
        {
            Name = name,
            DisplayName = displayName ?? name,
            Description = "Package-engine test harness manager",
            ExecutableFriendlyName = $"{name}.exe",
            InstallVerb = "install",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            ColorIconId = "package_mask",
            IconId = (IconType)'\uE8FD',
        };

        var defaultSource = new ManagerSource(this, "default", new Uri("https://example.test/default"));
        var properties = Properties;
        properties.DefaultSource = defaultSource;
        properties.KnownSources = [defaultSource];
        Properties = properties;

        TestDetailsHelper = new TestPackageDetailsHelper(this);
        TestOperationHelper = new TestPackageOperationHelper(this);
        TestSourcesHelper = new TestSourceHelper(this);

        DetailsHelper = TestDetailsHelper;
        OperationHelper = TestOperationHelper;
        SourcesHelper = TestSourcesHelper;

        _candidateExecutableFiles = [$"C:\\test-tools\\{name}.exe"];
    }

    public TestPackageDetailsHelper TestDetailsHelper { get; }

    public TestPackageOperationHelper TestOperationHelper { get; }

    public TestSourceHelper TestSourcesHelper { get; }

    public bool ExecutableFound { get; set; } = true;

    public string ExecutablePath { get; set; } = "C:\\test-tools\\manager.exe";

    public string ExecutableArguments { get; set; } = "";

    public string LoadedVersion { get; set; } = "1.0.0-test";

    public int AttemptFastRepairCalls { get; private set; }

    public int RefreshPackageIndexesCalls { get; private set; }

    public string? LastQuery { get; private set; }

    public void SetFindPackages(Func<string, IReadOnlyList<Package>> findPackages)
    {
        _findPackages = findPackages;
    }

    public void SetAvailableUpdates(Func<IReadOnlyList<Package>> getAvailableUpdates)
    {
        _getAvailableUpdates = getAvailableUpdates;
    }

    public void SetInstalledPackages(Func<IReadOnlyList<Package>> getInstalledPackages)
    {
        _getInstalledPackages = getInstalledPackages;
    }

    public void SetCandidateExecutableFiles(params string[] candidateExecutableFiles)
    {
        _candidateExecutableFiles = candidateExecutableFiles;
    }

    public void SetKnownSources(IEnumerable<IManagerSource> sources)
    {
        var sourceArray = sources.ToArray();
        if (sourceArray.Length == 0)
        {
            sourceArray = [DefaultSource];
        }

        TestSourcesHelper.SetSources(sourceArray);

        var properties = Properties;
        properties.KnownSources = sourceArray;
        Properties = properties;
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments
    )
    {
        found = ExecutableFound;
        path = ExecutablePath;
        callArguments = ExecutableArguments;
    }

    protected override void _loadManagerVersion(out string version)
    {
        version = LoadedVersion;
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        LastQuery = query;
        return _findPackages(query);
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        return _getAvailableUpdates();
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        return _getInstalledPackages();
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles()
    {
        return _candidateExecutableFiles;
    }

    public override void AttemptFastRepair()
    {
        AttemptFastRepairCalls++;
    }

    public override void RefreshPackageIndexes()
    {
        RefreshPackageIndexesCalls++;
    }
}
