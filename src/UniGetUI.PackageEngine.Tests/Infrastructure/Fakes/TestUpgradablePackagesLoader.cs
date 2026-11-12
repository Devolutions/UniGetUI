using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

internal sealed class TestUpgradablePackagesLoader : UpgradablePackagesLoader
{
    public TestUpgradablePackagesLoader(IReadOnlyList<IPackageManager> managers)
        : base(managers) { }

    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(30);

    protected override TimeSpan InstalledListingWaitLimit => WaitLimit;

    public Task<bool> EvaluatePackageAsync(IPackage package) => IsPackageValid(package);

    public Task ApplyWhenAddingPackageAsync(IPackage package) => WhenAddingPackage(package);
}
