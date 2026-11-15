using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations.Reboot;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface;

/// <summary>The outcome of resolving a remote request's canonical source name.</summary>
public enum RemoteSourceResolution
{
    Resolved,
    UnknownManager,
    MissingSourceName,
    NotFound,
    Ambiguous,
    /// <summary>The manager cannot report which feed an installed package came from.</summary>
    OriginUnknown,
}

/// <summary>
/// Draft projections for Agent inventory.
/// Package and source requests remain gated until isolation, source privacy and completeness
/// can be established.
/// </summary>
public static class IpcRemoteInventory
{
    /// <summary>
    /// The installed packages already loaded locally, or <c>false</c> while that list is not
    /// complete (never loaded, or being reloaded), which the caller reports as busy.
    /// </summary>
    public static bool TryListInstalledPackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        TryProject(InstalledPackagesLoader.Instance, installed: true, out packages);

    /// <summary>The available updates already loaded locally; see <see cref="TryListInstalledPackages"/>.</summary>
    public static bool TryListUpgradablePackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        TryProject(UpgradablePackagesLoader.Instance, installed: false, out packages);

    public static IReadOnlyList<IpcManagerInfo> ListManagers() =>
        PEInterface.Managers
            .OrderBy(manager => manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(manager =>
            {
                var info = IpcManagerSettingsApi.ToManagerInfo(manager, includeHealth: false);
                // Executable paths are local paths, and health reasons can quote captured output.
                info.ExecutablePath = "";
                info.ExecutableArguments = "";
                return info;
            })
            .ToArray();

    /// <summary>
    /// The manager's known sources plus the sources of its loaded packages. Configured sources
    /// are not queried, because that runs the manager's own command and logs its output.
    /// </summary>
    public static IReadOnlyList<IpcSourceInfo> ListSources()
    {
        var observed = SnapshotPackages(InstalledPackagesLoader.Instance)
            .Concat(SnapshotPackages(UpgradablePackagesLoader.Instance))
            .Select(package => package.Source);
        return BuildSources(PEInterface.Managers, observed);
    }

    /// <summary>
    /// Resolves a canonical source name by ordinal comparison against the manager's known and
    /// observed sources. The display label is never accepted in its place.
    /// </summary>
    public static RemoteSourceResolution ResolveSource(
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    )
    {
        var observed = SnapshotPackages(InstalledPackagesLoader.Instance)
            .Concat(SnapshotPackages(UpgradablePackagesLoader.Instance))
            .Select(package => package.Source);
        return ResolveSource(PEInterface.Managers, observed, manager, sourceName, requireInstalledOrigin);
    }

    /// <param name="requireInstalledOrigin">
    /// True for update and uninstall, which require package-specific origin evidence not present
    /// in this source-only lookup.
    /// </param>
    internal static RemoteSourceResolution ResolveSource(
        IEnumerable<IPackageManager> managers,
        IEnumerable<IManagerSource> observedSources,
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    )
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return RemoteSourceResolution.MissingSourceName;
        }

        var resolvedManager = managers.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(manager)
            && candidate.Id.Equals(manager, StringComparison.OrdinalIgnoreCase));
        if (resolvedManager is null)
        {
            return RemoteSourceResolution.UnknownManager;
        }

        if (requireInstalledOrigin)
        {
            return RemoteSourceResolution.OriginUnknown;
        }

        var matches = SourcesOf(resolvedManager, observedSources)
            .Where(source => string.Equals(source.Name, sourceName, StringComparison.Ordinal))
            .Select(source => source.Url.ToString())
            .Distinct(StringComparer.Ordinal)
            .Count();
        return matches switch
        {
            0 => RemoteSourceResolution.NotFound,
            1 => IsSafeSourceText(sourceName) ? RemoteSourceResolution.Resolved : RemoteSourceResolution.NotFound,
            _ => RemoteSourceResolution.Ambiguous,
        };
    }

    internal static IReadOnlyList<IpcSourceInfo> BuildSources(
        IEnumerable<IPackageManager> managers,
        IEnumerable<IManagerSource> observedSources
    )
    {
        var observed = observedSources.ToArray();
        List<IpcSourceInfo> result = [];
        foreach (var manager in managers)
        {
            var sources = SourcesOf(manager, observed).ToArray();
            var ambiguous = AmbiguousNames(sources);
            var known = manager.Properties.KnownSources.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
            foreach (var source in sources.DistinctBy(SourceKey, StringComparer.Ordinal))
            {
                var name = SafeSourceName(source, ambiguous);
                result.Add(new IpcSourceInfo
                {
                    Manager = IpcManagerSettingsApi.GetPublicManagerId(manager),
                    Name = name,
                    DisplayName = SafeText(source.AsString_DisplayName),
                    Url = SafeUrl(source.Url),
                    IsKnown = known.Contains(SourceKey(source)),
                    IsConfigured = !known.Contains(SourceKey(source)),
                });
            }
        }

        return result
            .OrderBy(source => source.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<IpcPackageInfo> ProjectPackages(
        IEnumerable<IPackage> packages,
        bool installed,
        IReadOnlySet<string>? pendingRestartKeys
    )
    {
        var list = packages.ToArray();
        IEnumerable<IPackage> distinct = installed
            ? list.DistinctBy(IpcPackageApi.GetInstalledPackageIdentity)
            : list.DistinctBy(IpcPackageApi.GetPackageIdentity);
        return distinct
            .Select(package =>
            {
                var info = IpcPackageApi.ToIpcInstalledPackageInfo(package, pendingRestartKeys);
                info.Source = SafeText(info.Source);
                return info;
            })
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// False for text that can carry credentials or local paths: URLs with user information or a
    /// query, file URLs, rooted or UNC paths, and control characters.
    /// </summary>
    internal static bool IsSafeSourceText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (text.Any(char.IsControl))
        {
            return false;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var afterScheme = text[(schemeEnd + 3)..];
            var authorityEnd = afterScheme.IndexOfAny(['/', '?', '#']);
            var authority = authorityEnd < 0 ? afterScheme : afterScheme[..authorityEnd];
            if (authority.Contains('@') || text.Contains('?') || text.Contains('#'))
            {
                return false;
            }

            if (text.Contains("file:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('\\')
            || text.StartsWith('/')
            || text.StartsWith('~')
            || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':'))
        {
            return false;
        }

        return true;
    }

    private static bool TryProject(
        AbstractPackageLoader? loader,
        bool installed,
        out IReadOnlyList<IpcPackageInfo> packages
    )
    {
        packages = [];
        if (loader is null || !loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        var snapshot = loader.Packages;
        // A reload that started while copying clears the list first, so the copy may be partial.
        if (!loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        packages = ProjectPackages(snapshot, installed, PendingRebootStore.GetPendingKeys());
        return true;
    }

    private static IReadOnlyList<IPackage> SnapshotPackages(AbstractPackageLoader? loader) =>
        loader is null || !loader.IsLoaded || loader.IsLoading ? [] : loader.Packages;

    private static IEnumerable<IManagerSource> SourcesOf(
        IPackageManager manager,
        IEnumerable<IManagerSource> observedSources
    ) => manager.Properties.KnownSources.Concat(observedSources.Where(source => source.Manager == manager));

    private static HashSet<string> AmbiguousNames(IReadOnlyList<IManagerSource> sources) =>
        sources
            .GroupBy(source => source.Name, StringComparer.Ordinal)
            .Where(group => group.Select(source => source.Url.ToString()).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static string SafeSourceName(IManagerSource source, IReadOnlySet<string> ambiguous) =>
        ambiguous.Contains(source.Name) || !IsSafeSourceText(source.Name) ? "" : source.Name;

    private static string SafeText(string text) => IsSafeSourceText(text) ? text : "";

    private static string SafeUrl(Uri url)
    {
        var text = url.ToString();
        return url.IsAbsoluteUri
            && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            && IsSafeSourceText(text)
            ? text
            : "";
    }

    private static string SourceKey(IManagerSource source) => $"{source.Name}\n{source.Url}";
}
