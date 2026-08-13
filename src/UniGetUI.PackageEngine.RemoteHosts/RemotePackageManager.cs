using System.Text;
using UniGetUI.Core.IconEngine;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class RemotePackageManager : IPackageManager
{
    private static readonly Dictionary<string, RemotePackageManager> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static RemotePackageManager For(string managerId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(managerId, out RemotePackageManager? existing))
                return existing;
            var created = new RemotePackageManager(managerId);
            Cache[managerId] = created;
            return created;
        }
    }

    public ManagerProperties Properties { get; }
    public ManagerCapabilities Capabilities { get; }
    public ManagerStatus Status { get; }
    public Encoding OutputEncoding => Encoding.UTF8;
    public string Id => Properties.Id;
    public string Name => Properties.Name;
    public string DisplayName => Properties.DisplayName ?? Properties.Name;
    public IManagerSource DefaultSource => Properties.DefaultSource;
    public IManagerLogger TaskLogger { get; }
    public IMultiSourceHelper SourcesHelper { get; }
    public IPackageDetailsHelper DetailsHelper { get; }
    public IPackageOperationHelper OperationHelper { get; }
    public IReadOnlyList<ManagerDependency> Dependencies { get; } = [];

    public bool IsSystemPackageManager => LinuxAgentless.SystemManagerIds.Contains(Id);

    private RemotePackageManager(string managerId)
    {
        string id = string.IsNullOrWhiteSpace(managerId) ? "unknown" : managerId;
        TaskLogger = new ManagerLogger(this);
        SourcesHelper = new RemoteNoopSourceHelper();
        DetailsHelper = new RemoteNoopDetailsHelper();
        OperationHelper = new RemoteNoopOperationHelper();
        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = LinuxAgentless.SystemManagerIds.Contains(id),
        };
        Status = new ManagerStatus
        {
            Found = true,
            Version = "remote",
            ExecutablePath = "",
            ExecutableCallArgs = "",
        };
        var source = new ManagerSource(this, id, new Uri("about:blank"), isVirtualManager: true);
        Properties = new ManagerProperties
        {
            Id = id,
            Name = DisplayNameFor(id),
            DisplayName = DisplayNameFor(id),
            Description = "Remote package manager",
            IconId = IconType.Package,
            ColorIconId = "unset",
            ExecutableFriendlyName = id,
            InstallVerb = "install",
            UpdateVerb = "update",
            UninstallVerb = "uninstall",
            KnownSources = [source],
            DefaultSource = source,
        };
    }

    private static string DisplayNameFor(string id) => id switch
    {
        "apt" => "APT",
        "dnf" => "DNF",
        "npm" => "Npm",
        "pip" => "Pip",
        "cargo" => "Cargo",
        "pacman" => "Pacman",
        "snap" => "Snap",
        "flatpak" => "Flatpak",
        "winget" => "WinGet",
        "scoop" => "Scoop",
        "chocolatey" => "Chocolatey",
        "homebrew" => "Homebrew",
        "dotnet-tool" => ".NET Tool",
        "pwsh" => "PowerShell 7",
        "winps" => "PowerShell",
        "vcpkg" => "vcpkg",
        "bun" => "Bun",
        _ => id,
    };

    public IReadOnlyList<IPackage> FindPackages(string query) => [];
    public IReadOnlyList<IPackage> GetAvailableUpdates() => [];
    public IReadOnlyList<IPackage> GetInstalledPackages() => [];
    public void Initialize() { }
    public bool IsEnabled() => true;
    public bool IsReady() => true;
    public void RefreshPackageIndexes() { }
    public void AttemptFastRepair() { }
    public IReadOnlyList<string> FindCandidateExecutableFiles() => [];
    public Tuple<bool, string> GetExecutableFile() => Tuple.Create(false, "");
}

internal sealed class RemoteNoopSourceHelper : IMultiSourceHelper
{
    public ISourceFactory Factory => throw new NotSupportedException();
    public string[] GetAddSourceParameters(IManagerSource source) => [];
    public string[] GetRemoveSourceParameters(IManagerSource source) => [];
    public OperationVeredict GetAddOperationVeredict(IManagerSource source, int ReturnCode, string[] Output)
        => OperationVeredict.Success;
    public OperationVeredict GetRemoveOperationVeredict(IManagerSource source, int ReturnCode, string[] Output)
        => OperationVeredict.Success;
    public IReadOnlyList<IManagerSource> GetSources() => [];
}

internal sealed class RemoteNoopDetailsHelper : IPackageDetailsHelper
{
    public void GetDetails(IPackageDetails details) { }
    public IReadOnlyList<string> GetVersions(IPackage package) => [];
    public CacheableIcon? GetIcon(IPackage package) => null;
    public IReadOnlyList<Uri> GetScreenshots(IPackage package) => [];
    public string? GetInstallLocation(IPackage package) => null;
}

internal sealed class RemoteNoopOperationHelper : IPackageOperationHelper
{
    public IReadOnlyList<string> GetParameters(IPackage package, InstallOptions options, OperationType operation)
        => [];
    public OperationVeredict GetResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode
    ) => returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    public void ApplyElevationRequirements(IPackage package, InstallOptions options, OperationType operation) { }
}
