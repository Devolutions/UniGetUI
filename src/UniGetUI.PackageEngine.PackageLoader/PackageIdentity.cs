using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    public static class PackageIdentity
    {
        public static bool IsSamePackageAs(this IPackage package, IPackage other) =>
            package.IsEquivalentTo(other) || IsSamePackageFromAnySource(package, other);

        internal static bool IsSamePackageFromAnySource(IPackage candidate, IPackage package) =>
            !package.Manager.Capabilities.InstalledPackagesKeepTheirSource
            && candidate.Manager.Name == package.Manager.Name
            && string.Equals(candidate.Id, package.Id, StringComparison.OrdinalIgnoreCase);
    }
}
