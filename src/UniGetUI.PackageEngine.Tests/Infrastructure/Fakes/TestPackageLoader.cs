using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

internal sealed class TestPackageLoader : AbstractPackageLoader
{
    private readonly Func<IPackageManager, IReadOnlyList<IPackage>> _loadPackages;
    private readonly Func<IPackage, Task<bool>> _isPackageValid;
    private readonly Func<IPackage, Task> _whenAddingPackage;

    public TestPackageLoader(
        IReadOnlyList<IPackageManager> managers,
        bool allowMultiplePackageVersions = false,
        bool disableReload = false,
        bool checkedByDefault = false,
        Func<IPackageManager, IReadOnlyList<IPackage>>? loadPackages = null,
        Func<IPackage, Task<bool>>? isPackageValid = null,
        Func<IPackage, Task>? whenAddingPackage = null
    )
        : base(
            managers,
            identifier: "TEST_LOADER",
            AllowMultiplePackageVersions: allowMultiplePackageVersions,
            DisableReload: disableReload,
            CheckedBydefault: checkedByDefault,
            RequiresInternet: false
        )
    {
        _loadPackages = loadPackages ?? (manager => manager.GetInstalledPackages());
        _isPackageValid = isPackageValid ?? (_ => Task.FromResult(true));
        _whenAddingPackage = whenAddingPackage ?? (_ => Task.CompletedTask);
    }

    protected override IReadOnlyList<IPackage> LoadPackagesFromManager(IPackageManager manager)
    {
        return _loadPackages(manager);
    }

    protected override Task<bool> IsPackageValid(IPackage package)
    {
        return _isPackageValid(package);
    }

    protected override Task WhenAddingPackage(IPackage package)
    {
        return _whenAddingPackage(package);
    }
}
