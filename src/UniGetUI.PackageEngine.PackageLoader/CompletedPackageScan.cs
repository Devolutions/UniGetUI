using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.PackageLoader
{
    /// <summary>
    /// The packages a full load held when it finished, the UTC time it finished, and the managers
    /// whose listing completed without a reported failure in that load. <paramref name="StartGeneration"/>
    /// is the loader's <c>LoadGeneration</c> the load started with, so a caller that read a smaller
    /// generation knows this load started after it.
    /// </summary>
    public sealed record CompletedPackageScan(
        IReadOnlyList<IPackage> Packages,
        DateTime CompletedUtc,
        IReadOnlySet<IPackageManager> SucceededManagers,
        long StartGeneration = 0
    );
}
