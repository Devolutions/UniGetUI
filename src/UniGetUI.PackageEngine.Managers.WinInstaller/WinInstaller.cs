using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager;

public sealed class WinInstaller : PackageManager
{
    public const string ManagerId = "installer";

    public WinInstaller()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = false,
            CanUninstall = true,
            InstalledPackagesKeepTheirSource = false,
            CanDownloadInstaller = true,
            SupportsCustomSources = true,
            SupportsEmptySearchQuery = true,
            SupportsCustomPackageIcons = true,
            SupportsCustomVersions = true,
            SerializesOperations = true,
            Sources = new SourceCapabilities
            {
                KnowsPackageCount = false,
                KnowsUpdateDate = false,
                CanBeEdited = false,
            },
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
            KnowsPackageReleaseDate = PackageReleaseDateSupport.No,
        };

        Properties = new ManagerProperties
        {
            Id = ManagerId,
            Name = "Installer",
            DisplayName = "Installer packages",
            Description = CoreTools.Translate(
                "Apps (.exe and .msi) that your organization publishes in its software catalog."
            ),
            IconId = IconType.Package,
            ColorIconId = "pkg_color",
            ExecutableFriendlyName = "msiexec.exe",
            InstallVerb = "/i",
            UpdateVerb = "/i",
            UninstallVerb = "/x",
            KnownSources = [],
            DefaultSource = new ManagerSource(this, "Installer packages", new Uri("file:///")),
        };

        SourcesHelper = new InstallerFeedSourceHelper(this);
        DetailsHelper = new WinInstallerDetailsHelper(this);
        OperationHelper = new WinInstallerOperationHelper(this);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
        File.Exists(WinInstallerRunner.MsiExecPath) ? [WinInstallerRunner.MsiExecPath] : [];

    protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
    {
        var candidates = FindCandidateExecutableFiles();
        found = candidates.Count > 0;
        path = found ? candidates[0] : "";
        callArguments = "";
    }

    protected override void _loadManagerVersion(out string version) =>
        version = $"Windows {Environment.OSVersion.Version}";

    public override void RefreshPackageIndexes()
    {
        WinFeed.ForgetCachedIndexes();
        ((InstallerFeedSourceHelper)SourcesHelper).ForgetSources();
    }

    internal static Architecture Machine { get; set; } = RuntimeInformation.OSArchitecture;

    internal IReadOnlyList<(IManagerSource Source, WinFeedItem Item)> ListLatest()
    {
        var result = new List<(IManagerSource, WinFeedItem)>();
        foreach (var source in SourcesHelper.GetSources())
        {
            try
            {
                foreach (var item in WinFeed.Latest(WinFeed.List(source.Url), CompareVersions, Machine))
                    result.Add((source, item));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not list the package feed {source.Name} at {source.Url}: {ex.Message}");
            }
        }
        return result;
    }

    internal InstallerFeedSource? FindFeedSource(IPackage package) =>
        SourcesHelper.GetSources().OfType<InstallerFeedSource>().FirstOrDefault(s => s.Name == package.Source.Name);

    internal WinFeedItem? FindFeedItem(IPackage package, string? version = null, bool currentOnly = false)
    {
        var source = FindFeedSource(package);
        if (source is null)
            return null;
        var items = WinFeed.List(source.Url, currentOnly: currentOnly)
            .Where(i => string.Equals(i.Id, package.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!string.IsNullOrEmpty(version))
            items = items.Where(i => i.Version == version).ToArray();
        return WinFeed.Latest(items, CompareVersions, Machine).FirstOrDefault();
    }

    internal IComparer<string> VersionOrder =>
        Comparer<string>.Create((a, b) => CompareVersions(a, b) ?? string.CompareOrdinal(a, b));

    internal IReadOnlyList<WinFeedItem> ListFeedItems(IPackage package)
    {
        var source = SourcesHelper.GetSources().FirstOrDefault(s => s.Name == package.Source.Name);
        return source is null ? [] : WinFeed.List(source.Url)
            .Where(i => string.Equals(i.Id, package.Id, StringComparison.OrdinalIgnoreCase)
                && InstallerFile.ArchitectureFit(i.Architectures, Machine) > 0)
            .ToArray();
    }

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        query = query.Trim();
        return ListLatest()
            .Where(p => query.Length == 0
                || p.Item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p => new Package(p.Item.Name, p.Item.Id, p.Item.Version, p.Source, this))
            .ToArray();
    }

    internal sealed record InstalledItem(IManagerSource Source, WinFeedItem Item, ArpEntry Entry, string Installed, bool HasUpdate);

    internal IReadOnlyList<InstalledItem> ListInstalled()
    {
        var latest = ListLatest();
        var entries = ArpEntries.Read();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<InstalledItem>();
        foreach (var (source, item) in latest.OrderBy(p => p.Item.Id, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => p.Item.Version, VersionOrder))
        {
            if (seen.Contains(item.Id) || InstalledMatcher.Find(item, entries) is not { } entry)
                continue;
            string version = InstallReceipts.InstalledVersion(item.Id, entry);
            installed.Add(new InstalledItem(source, item, entry, version, CompareVersions(item.Version, version) > 0));
            seen.Add(item.Id);
        }
        return installed;
    }

    internal ArpEntry? FindInstalledEntry(IPackage package) =>
        ListInstalled().FirstOrDefault(p => string.Equals(p.Item.Id, package.Id, StringComparison.OrdinalIgnoreCase))?.Entry;

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
        ListInstalled()
            .Select(p => new Package(p.Item.Name, p.Item.Id, p.Installed, p.Source, this))
            .ToArray();

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe() =>
        ListInstalled()
            .Where(p => p.HasUpdate)
            .Select(p => new Package(p.Item.Name, p.Item.Id, p.Installed, p.Item.Version, p.Source, this))
            .ToArray();
}
