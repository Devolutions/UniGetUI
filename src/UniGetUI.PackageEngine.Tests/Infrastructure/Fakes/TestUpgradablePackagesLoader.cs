using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

internal sealed class TestUpgradablePackagesLoader : UpgradablePackagesLoader
{
    public TestUpgradablePackagesLoader(IReadOnlyList<IPackageManager> managers)
        : base(managers) { }

    public Task<bool> EvaluatePackageAsync(IPackage package) => IsPackageValid(package);

    public Task ApplyWhenAddingPackageAsync(IPackage package) => WhenAddingPackage(package);
}
