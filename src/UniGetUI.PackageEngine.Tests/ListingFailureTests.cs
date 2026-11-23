using UniGetUI.Core.Classes;
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.AptManager;
using UniGetUI.PackageEngine.Managers.DnfManager;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;
using UniGetUI.PackageEngine.Managers.PacmanManager;
using UniGetUI.PackageEngine.Managers.VcpkgManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
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

    private sealed class SharedNuGetListing : UniGetUI.PackageEngine.Managers.DotNetManager.DotNet
    {
        public Func<IReadOnlyList<Package>> Listing { get; set; } = () => [];
        protected override IReadOnlyList<Package> _getInstalledPackages_UnSafe() => Listing();
        public IReadOnlyList<Package> Read() => GetInstalledPackages_UnSafe();
    }

    [Fact]
    public void InstallerSourcesReplayCatalogDiscoveryFailuresAndStrictCallsReadAgain()
    {
        string? previous = CatalogFeeds.TestCatalogPathOverride;
        string path = Path.Combine(_testRoot, "SoftwareCatalog.json");
        CatalogFeeds.TestCatalogPathOverride = path;
        try
        {
            File.WriteAllText(path, "{broken");
            var helper = new InstallerFeedSourceHelper(new SharedNuGetListing());
            for (int i = 0; i < 2; i++)
            {
                Exception? failure = null;
                using var scope = PackageListingScope.TrackFailures(error => failure = error);
                Assert.Empty(helper.GetSources());
                Assert.IsAssignableFrom<System.Text.Json.JsonException>(failure);
            }
            using (PackageListingScope.EnterListingOnly())
                Assert.ThrowsAny<System.Text.Json.JsonException>(helper.GetSources);

            File.WriteAllText(path, "{\"catalogs\":[]}");
            using (PackageListingScope.EnterListingOnly())
                Assert.Empty(helper.GetSources());
            helper.ForgetSources();
            Exception? afterRepair = null;
            using (PackageListingScope.TrackFailures(error => afterRepair = error))
                Assert.Empty(helper.GetSources());
            Assert.Null(afterRepair);
        }
        finally
        {
            CatalogFeeds.TestCatalogPathOverride = previous;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{\"catalogs\":[]}")]
    public void MissingOrEmptyInstallerCatalogIsACompleteEmptySourceListing(string? json)
    {
        string? previous = CatalogFeeds.TestCatalogPathOverride;
        string path = Path.Combine(_testRoot, "SoftwareCatalog.json");
        CatalogFeeds.TestCatalogPathOverride = path;
        try
        {
            if (json is not null)
                File.WriteAllText(path, json);
            using var scope = PackageListingScope.EnterListingOnly();
            Assert.Empty(new InstallerFeedSourceHelper(new SharedNuGetListing()).GetSources());
        }
        finally
        {
            CatalogFeeds.TestCatalogPathOverride = previous;
        }
    }

    [Fact]
    public async Task SharedNuGetListingReportsIncompletenessToEveryCaller()
    {
        var manager = new SharedNuGetListing();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        manager.Listing = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            PackageListingScope.ReportIncomplete(new InvalidDataException("partial NuGet listing"));
            return [new Package("partial", "partial", "1.0", manager.DefaultSource, manager)];
        };
        (int Rows, int Failures) Read()
        {
            int failures = 0;
            using var tracking = PackageListingScope.TrackFailures(_ => Interlocked.Increment(ref failures));
            return (manager.Read().Count, failures);
        }

        int deduplicated = Volatile.Read(ref TaskRecyclerTelemetry.DeduplicatedCalls);
        Task<(int Rows, int Failures)> first = Task.Run(Read);
        Task<(int Rows, int Failures)>? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(Read);
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref TaskRecyclerTelemetry.DeduplicatedCalls) > deduplicated,
                TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
        }
        Assert.Equal((1, 1), await first);
        Assert.NotNull(second);
        Assert.Equal((1, 1), await second);
    }

    [Fact]
    public async Task StrictNuGetListingDoesNotAttachToANormalPartialListing()
    {
        var manager = new SharedNuGetListing();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        manager.Listing = () =>
        {
            if (!PackageListingScope.IsListingOnly)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            }
            PackageListingScope.ReportIncomplete(new InvalidDataException("partial NuGet listing"));
            return [];
        };
        Task normal = Task.Run(manager.Read);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() =>
            {
                using var scope = PackageListingScope.EnterListingOnly();
                Assert.Throws<InvalidDataException>(manager.Read);
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            await normal;
        }
    }

#if WINDOWS
    private sealed class ListingPowerShell7 : UniGetUI.PackageEngine.Managers.PowerShell7Manager.PowerShell7
    {
        public IReadOnlyList<Package> ListInstalled() => _getInstalledPackages_UnSafe();
    }

    [Fact]
    public void NormalPowerShell7ListingKeepsRowsFromAFailedCommandButListingOnlyFails()
    {
        var manager = new ListingPowerShell7
        {
            Status = new ManagerStatus
            {
                Found = true,
                ExecutablePath = FailingCommandScript.Create(
                    _testRoot, 1, "resource error",
                    "##SCOPE:AllUsers##", "Pester\t5.7.1\tPSGallery"),
                ExecutableCallArgs = "",
            },
        };

        Assert.Equal("Pester", Assert.Single(manager.ListInstalled()).Id);
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidOperationException>(manager.ListInstalled);
    }

    [Fact]
    public void MalformedNuGetUpdateEntriesKeepOtherRowsNormallyButFailListingOnly()
    {
        var manager = new ListingPowerShell7();
        const string output = """
            <feed>
              <entry><Id>first</Id><Version>2.0</Version></entry>
              <entry><Id>broken</Id></entry>
              <entry><Id>last</Id><Version>2.0</Version></entry>
            </feed>
            """;
        Dictionary<string, string> installed = new() { ["first"] = "1.0", ["last"] = "1.0" };
        List<Package> Parse() =>
            UniGetUI.PackageEngine.Managers.PowerShellManager.BaseNuGet.ParseUpdatesResponse(
                output, installed, new Dictionary<string, string?>(), manager.DefaultSource, manager);
        List<Exception> failures = [];

        using (PackageListingScope.TrackFailures(failures.Add))
            Assert.Equal(["first", "last"], Parse().Select(package => package.Id));
        Assert.IsType<InvalidDataException>(Assert.Single(failures));

        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidDataException>(Parse);
    }
#endif

    [Fact]
    public void AFailingAptListingIsAFailedListingRatherThanAnEmptyOne()
    {
        var apt = new ListingApt { Status = FailingStatus(100, "Could not open lock file") };

        Assert.Empty(apt.ListInstalled());
        Assert.Empty(apt.ListUpdates());
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidOperationException>(apt.ListInstalled);
        Assert.Throws<InvalidOperationException>(apt.ListUpdates);
    }

    [Fact]
    public void AFailingVcpkgListingIsAFailedListingRatherThanAnEmptyOne()
    {
        var vcpkg = new ListingVcpkg { Status = FailingStatus(1, "Could not locate a manifest") };

        Assert.Empty(vcpkg.ListInstalled());
        Assert.Empty(vcpkg.ListUpdates());
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidOperationException>(vcpkg.ListInstalled);
        Assert.Throws<InvalidOperationException>(vcpkg.ListUpdates);
    }

    [Fact]
    public void NormalAptListingKeepsRowsFromAFailedCommandButListingOnlyFails()
    {
        var apt = new ListingApt
        {
            Status = new ManagerStatus
            {
                Found = true,
                ExecutablePath = FailingCommandScript.Create(
                    _testRoot, 100, "repository failed", "git/stable 2.45.0 amd64 [installed]"),
                ExecutableCallArgs = "",
            },
        };

        Assert.Equal("git", Assert.Single(apt.ListInstalled()).Id);
        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Throws<InvalidOperationException>(apt.ListInstalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReportedParseFailureMakesThePublicCallIncompleteWithoutRepairOrRetry(bool listingOnly)
    {
        var manager = new TestPackageManager("MalformedNpmListing");
        int calls = 0;
        string output = """{"dependencies":{"first":{"version":"1.0"},"bad":17,"last":{"version":"1.0"}}}""";
        manager.SetInstalledPackages(() =>
        {
            calls++;
            return UniGetUI.PackageEngine.Managers.NpmManager.Npm.ParseInstalledPackagesOutput(
                output, manager.DefaultSource, manager, new(UniGetUI.PackageEngine.Enums.PackageScope.Global));
        });
        manager.Initialize();
        Assert.True(manager.IsReady());

        using (listingOnly ? PackageListingScope.EnterListingOnly() : null)
        {
            var packages = manager.GetInstalledPackages(out bool failed);

            Assert.True(failed);
            Assert.Equal(listingOnly ? [] : new[] { "first", "last" }, packages.Select(package => package.Id));
            Assert.Equal(1, calls);
            Assert.Equal(0, manager.AttemptFastRepairCalls);
        }

        output = """{"dependencies":{"good":{"version":"1.0"}}}""";
        Assert.Equal("good", Assert.Single(manager.GetInstalledPackages(out bool nextFailed)).Id);
        Assert.False(nextFailed);
        Assert.Equal(2, calls);
        Assert.Equal(0, manager.AttemptFastRepairCalls);
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
