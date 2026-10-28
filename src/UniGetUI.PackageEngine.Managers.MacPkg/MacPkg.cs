using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// macOS .pkg and .dmg files published in the feeds that the software catalog declares (<c>pkgFeed</c>).
/// Detection relies on receipts and app bundles, so IT publishes the vendor's file unchanged.
/// </summary>
public sealed class MacPkg : PackageManager
{
    public const string ManagerId = "pkg";

    public MacPkg()
    {
        Dependencies = [];

        Capabilities = new ManagerCapabilities
        {
            CanRunAsAdmin = false,
            CanUninstall = false,
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
            Name = "Pkg",
            DisplayName = "Installer packages",
            Description = CoreTools.Translate(
                "Apps (.pkg and .dmg) that your organization publishes in its software catalog."
            ),
            IconId = IconType.Package,
            ColorIconId = "pkg_color",
            ExecutableFriendlyName = "installer",
            InstallVerb = "-pkg",
            UpdateVerb = "-pkg",
            UninstallVerb = "",
            KnownSources = [],
            DefaultSource = new ManagerSource(this, "Installer packages", new Uri("file:///")),
        };

        SourcesHelper = new InstallerFeedSourceHelper(this);
        DetailsHelper = new MacPkgDetailsHelper(this);
        OperationHelper = new MacPkgOperationHelper(this);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles() =>
        OperatingSystem.IsMacOS() && File.Exists(PkgInstaller.InstallerPath) ? [PkgInstaller.InstallerPath] : [];

    protected override void _loadManagerExecutableFile(out bool found, out string path, out string callArguments)
    {
        var candidates = FindCandidateExecutableFiles();
        found = candidates.Count > 0;
        path = found ? candidates[0] : "";
        callArguments = "";
    }

    protected override void _loadManagerVersion(out string version) =>
        version = $"macOS {Environment.OSVersion.Version}";

    public override void RefreshPackageIndexes()
    {
        PkgFeed.ForgetCachedIndexes();
        // A catalog edited since the last listing may name other feeds.
        ((InstallerFeedSourceHelper)SourcesHelper).ForgetSources();
    }

    // ── Listing ────────────────────────────────────────────────────────────

    /// <summary>The newest package of each identifier in every feed. An unreachable feed is skipped.</summary>
    internal IReadOnlyList<(IManagerSource Source, PkgFeedItem Item)> ListLatest()
    {
        var result = new List<(IManagerSource, PkgFeedItem)>();
        foreach (var source in SourcesHelper.GetSources())
        {
            try
            {
                foreach (var item in PkgFeed.Latest(PkgFeed.List(source.Url), CompareVersions, RuntimeInformation.OSArchitecture))
                    result.Add((source, item));
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not list the package feed {source.Name} at {source.Url}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>The feed package behind a package, at the requested version or else the newest one.</summary>
    internal InstallerFeedSource? FindFeedSource(IPackage package) =>
        SourcesHelper.GetSources().OfType<InstallerFeedSource>().FirstOrDefault(s => s.Name == package.Source.Name);

    internal PkgFeedItem? FindFeedItem(IPackage package, string? version = null, bool currentOnly = false)
    {
        var source = FindFeedSource(package);
        if (source is null)
            return null;
        var items = PkgFeed.List(source.Url, currentOnly: currentOnly).Where(i => i.Id == package.Id).ToArray();
        if (!string.IsNullOrEmpty(version))
            return items
                .Where(i => (i.Version == version || i.App?.Version == version)
                    && PkgMetadata.ArchitectureFit(i.Architectures, RuntimeInformation.OSArchitecture) > 0)
                // An update found by the app in place names the app's version; the package's own wins a tie,
                // then the newest app among releases of one package version.
                .OrderByDescending(i => i.Version == version)
                .ThenByDescending(i => i, ReleaseOrder)
                .ThenByDescending(i => PkgMetadata.ArchitectureFit(i.Architectures, RuntimeInformation.OSArchitecture))
                .FirstOrDefault();
        return PkgFeed.Latest(items, CompareVersions, RuntimeInformation.OSArchitecture).FirstOrDefault();
    }

    /// <summary>Orders versions as this manager compares them, falling back to their text.</summary>
    internal IComparer<string> VersionOrder =>
        Comparer<string>.Create((a, b) => CompareVersions(a, b) ?? string.CompareOrdinal(a, b));

    /// <summary>Orders releases of one package, newest last, as <see cref="PkgFeed.CompareReleases"/> does.</summary>
    internal IComparer<PkgFeedItem> ReleaseOrder =>
        Comparer<PkgFeedItem>.Create((a, b) => PkgFeed.CompareReleases(a, b, CompareVersions));

    internal IReadOnlyList<PkgFeedItem> ListFeedItems(IPackage package)
    {
        var source = SourcesHelper.GetSources().FirstOrDefault(s => s.Name == package.Source.Name);
        return source is null ? [] : PkgFeed.List(source.Url).Where(i => i.Id == package.Id).ToArray();
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

    // Only packages that a feed publishes are listed: other receipts (Apple's, or components of
    // software installed by other means) are not this manager's to show.
    // Installed and Offered are in one version domain (the receipt's, or the app's) so they compare; the
    // feed's own version stays on Item, for finding the package to install.
    internal IReadOnlyList<(IManagerSource Source, PkgFeedItem Item, string Installed, string Offered, bool HasUpdate)> ListInstalled()
    {
        var latest = ListLatest();
        IReadOnlyDictionary<string, string> receipts;
        try
        {
            receipts = PkgReceipts.GetInstalledVersions(
                latest.Where(p => !p.Item.CopiesAnApp).Select(p => p.Item.Id), TaskLogger);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // Without receipts, packages are still recognized by their apps, as disk images always are.
            Logger.Warn($"Could not read the installer package receipts ({ex.Message}); recognizing installed apps by their bundles only.");
            receipts = new Dictionary<string, string>();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var installed = new List<(IManagerSource, PkgFeedItem, string, string, bool)>();
        // Feeds that share a package are read newest first, so an update one of them offers is never hidden.
        foreach (var (source, item) in latest.OrderBy(p => p.Item.Id, StringComparer.Ordinal)
            .ThenByDescending(p => p.Item, ReleaseOrder))
        {
            if (seen.Contains(item.Id))
                continue;
            // The receipt decides first; without one, the app in place under its bundle id does, which
            // is how a .dmg, or an app that a management tool or the user installed another way, is recognized.
            if (!item.CopiesAnApp && receipts.TryGetValue(item.Id, out string? receipt))
                installed.Add((source, item, receipt, item.Version, CompareVersions(item.Version, receipt) > 0));
            else if (item.App is { } app && AppBundle.FindInstalled(app) is { } bundle)
                installed.Add((source, item, bundle.Version ?? "0", app.Version ?? item.Version,
                    app.Version is { } offered && bundle.Version is { } present && CompareVersions(offered, present) > 0));
            else
                continue;
            seen.Add(item.Id);
        }
        return installed;
    }

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe() =>
        ListInstalled()
            .Select(p => new Package(p.Item.Name, p.Item.Id, p.Installed, p.Source, this))
            .ToArray();

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe() =>
        ListInstalled()
            .Where(p => p.HasUpdate)
            .Select(p => new Package(p.Item.Name, p.Item.Id, p.Installed, p.Offered, p.Source, this))
            .ToArray();
}
