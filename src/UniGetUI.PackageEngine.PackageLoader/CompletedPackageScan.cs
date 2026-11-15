using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    /// <summary>
    /// The packages a full load held when it finished, the UTC time it finished, and the managers
    /// whose listing completed without a reported failure in that load.
    /// </summary>
    public sealed record CompletedPackageScan(
        IReadOnlyList<IPackage> Packages,
        DateTime CompletedUtc,
        IReadOnlySet<IPackageManager> SucceededManagers
    );
}
