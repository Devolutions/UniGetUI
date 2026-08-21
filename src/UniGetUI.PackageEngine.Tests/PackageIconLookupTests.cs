using UniGetUI.Core.Data;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageIconLookupTests : IDisposable
{
    private readonly string _testRoot;

    public PackageIconLookupTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(PackageIconLookupTests),
            Guid.NewGuid().ToString("N")
        );
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        Package.ResetIconCache();
    }

    public void Dispose()
    {
        Package.TEST_IconLookupRetryIntervalOverride = null;
        Package.ResetIconCache();
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void GetIconUrlIfAny_DoesNotRetryFailedLookupWithinRetryInterval()
    {
        int lookups = 0;
        var package = BuildPackage(
            "Contoso.WithinInterval",
            _ =>
            {
                lookups++;
                return null;
            }
        );

        Assert.Null(package.GetIconUrlIfAny());
        Assert.Null(package.GetIconUrlIfAny());

        Assert.Equal(1, lookups);
    }

    [Fact]
    public void GetIconUrlIfAny_RetriesFailedLookupAfterRetryInterval()
    {
        Package.TEST_IconLookupRetryIntervalOverride = TimeSpan.Zero;
        string iconPath = CreateIconFile();

        int lookups = 0;
        var package = BuildPackage(
            "Contoso.AfterInterval",
            _ =>
            {
                lookups++;
                return lookups == 1 ? null : new CacheableIcon(iconPath);
            }
        );

        Assert.Null(package.GetIconUrlIfAny());
        Uri? retried = package.GetIconUrlIfAny();

        Assert.Equal(2, lookups);
        Assert.NotNull(retried);
        Assert.True(retried.IsFile);
        Assert.Equal(iconPath, retried.LocalPath);
    }

    [Fact]
    public void GetIconUrlIfAny_DoesNotResolveResolvedIconAgain()
    {
        Package.TEST_IconLookupRetryIntervalOverride = TimeSpan.Zero;
        string iconPath = CreateIconFile();

        int lookups = 0;
        var package = BuildPackage(
            "Contoso.AlreadyResolved",
            _ =>
            {
                lookups++;
                return new CacheableIcon(iconPath);
            }
        );

        Uri? first = package.GetIconUrlIfAny();
        Uri? second = package.GetIconUrlIfAny();

        Assert.Equal(1, lookups);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ResetIconCache_AllowsFailedLookupToBeRetriedImmediately()
    {
        int lookups = 0;
        var package = BuildPackage(
            "Contoso.AfterReset",
            _ =>
            {
                lookups++;
                return null;
            }
        );

        Assert.Null(package.GetIconUrlIfAny());
        Package.ResetIconCache();
        Assert.Null(package.GetIconUrlIfAny());

        Assert.Equal(2, lookups);
    }

    private string CreateIconFile()
    {
        Directory.CreateDirectory(_testRoot);
        string iconPath = Path.Combine(_testRoot, "icon.png");
        File.WriteAllBytes(iconPath, [0x89, 0x50, 0x4E, 0x47]);
        return iconPath;
    }

    private static Package BuildPackage(string id, Func<IPackage, CacheableIcon?> iconFactory)
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomPackageIcons = true;
                return capabilities;
            })
            .ConfigureDetails(details => details.IconFactory = iconFactory)
            .Build();

        return new PackageBuilder().WithId(id).WithManager(manager).Build();
    }
}
