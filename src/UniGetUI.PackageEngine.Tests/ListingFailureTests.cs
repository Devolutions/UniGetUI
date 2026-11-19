using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Managers.AptManager;
using UniGetUI.PackageEngine.Managers.DnfManager;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// A listing command that fails must fail the listing, not report a successful empty list, for the
/// managers that have no test file of their own.
/// </summary>
[Collection(ListingFailureTestCollection.Name)]
public sealed class ListingFailureTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(ListingFailureTests),
        Guid.NewGuid().ToString("N")
    );

    public ListingFailureTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private ManagerStatus FailingStatus(int exitCode, string stderr) =>
        new()
        {
            Found = true,
            ExecutablePath = FailingCommandScript.Create(_testRoot, exitCode, stderr),
            ExecutableCallArgs = "",
        };

    private sealed class ListingApt : Apt
    {
        public IReadOnlyList<Package> ListInstalled() => GetInstalledPackages_UnSafe();

        public IReadOnlyList<Package> ListUpdates() => GetAvailableUpdates_UnSafe();
    }

    private sealed class ListingVcpkg : Vcpkg
    {
        public IReadOnlyList<Package> ListInstalled() => GetInstalledPackages_UnSafe();

        public IReadOnlyList<Package> ListUpdates() => GetAvailableUpdates_UnSafe();
    }

    [Fact]
    public void AFailingAptListingIsAFailedListingRatherThanAnEmptyOne()
    {
        var apt = new ListingApt { Status = FailingStatus(100, "Could not open lock file") };

        Assert.Throws<InvalidOperationException>(apt.ListInstalled);
        Assert.Throws<InvalidOperationException>(apt.ListUpdates);
    }

    [Fact]
    public void AFailingVcpkgListingIsAFailedListingRatherThanAnEmptyOne()
    {
        var vcpkg = new ListingVcpkg { Status = FailingStatus(1, "Could not locate a manifest") };

        Assert.Throws<InvalidOperationException>(vcpkg.ListInstalled);
        Assert.Throws<InvalidOperationException>(vcpkg.ListUpdates);
    }

    [Theory]
    [InlineData(0, "", 0, false)]
    [InlineData(100, "", 3, true)]
    [InlineData(1, "Error: No matching Packages to list\n", 0, false)]
    [InlineData(1, "No matching packages to list\n", 2, true)]
    [InlineData(1, "Error: Failed to download metadata for repo 'fedora'\n", 0, true)]
    public void DnfOnlyAcceptsNoMatchingPackagesAsAnEmptyListing(int exitCode, string stderr, int parsed, bool failed)
    {
        Assert.Equal(failed, Dnf.IsListingFailure(exitCode, stderr, parsed));
    }

    [Theory]
    [InlineData(1, 0, "", 0)]
    [InlineData(1, 0, "warning: database file for 'extra' does not exist\n", 0)]
    [InlineData(1, 0, "error: failed to init transaction (unable to lock database)\n", 1)]
    [InlineData(1, 2, "", 1)]
    [InlineData(0, 4, "", 0)]
    public void PacmanOnlyAcceptsAnExitCodeOfOneWithNothingFoundAndNoError(int exitCode, int count, string stderr, int expected)
    {
        Assert.Equal(expected, Pacman.ListingExitCode(exitCode, count, stderr));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ListingFailureTestCollection
{
    public const string Name = "Listing failure tests";
}
