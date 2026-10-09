using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Models;

/// <summary>
/// A per-refresh snapshot of package identities, using the same aliases and case rules as CatalogEntry.
/// Construction takes expected O(packages) time and space; each tile membership check takes expected O(1).
/// Rebuild on each refresh so inventory removals and completed operations cannot leave stale identities.
/// </summary>
internal sealed class CatalogPackageIdentitySet
{
    private readonly HashSet<Identity> _identities = new(new IdentityComparer());

    public CatalogPackageIdentitySet(IEnumerable<IPackage> packages, bool installedInventory = false)
    {
        foreach (var package in packages)
        {
            var manager = package.Manager;
            // Only installed Chocolatey inventory loses feed provenance. Pending operations remain source-specific.
            string? source = installedInventory
                && string.Equals(manager.Id, "chocolatey", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : package.Source.Name;
            _identities.Add(new Identity(manager.Id, package.Id, source));
            _identities.Add(new Identity(manager.Name, package.Id, source));
            _identities.Add(new Identity(manager.DisplayName, package.Id, source));
        }
    }

    public bool Contains(CatalogEntry entry) =>
        _identities.Contains(new Identity(entry.ManagerName, entry.Id, entry.Source))
        || _identities.Contains(new Identity(entry.ManagerName, entry.Id, null));

    private readonly record struct Identity(string Manager, string Id, string? Source);

    private sealed class IdentityComparer : IEqualityComparer<Identity>
    {
        public bool Equals(Identity x, Identity y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Manager, y.Manager)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Source, y.Source);

        public int GetHashCode(Identity identity) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Manager),
            StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Id),
            identity.Source is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Source));
    }
}
