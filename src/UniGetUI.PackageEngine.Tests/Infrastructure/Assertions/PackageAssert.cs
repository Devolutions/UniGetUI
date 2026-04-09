using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;

public static class PackageAssert
{
    public static void Matches(IPackage package, string name, string id, string version, string? newVersion = null)
    {
        Assert.Equal(name, package.Name);
        Assert.Equal(id, package.Id);
        Assert.Equal(version, package.VersionString);
        Assert.Equal(newVersion ?? version, package.NewVersionString);
    }

    public static void BelongsTo(IPackage package, IPackageManager manager, IManagerSource source)
    {
        Assert.Same(manager, package.Manager);
        Assert.Same(source, package.Source);
    }

    public static void ContainsIds(IEnumerable<IPackage> packages, params string[] expectedIds)
    {
        Assert.Equal(expectedIds.OrderBy(id => id), packages.Select(package => package.Id).OrderBy(id => id));
    }
}
