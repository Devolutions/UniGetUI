using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations.Reboot;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PendingRebootStoreTests : IDisposable
{
    private static readonly long TwoHours = TimeSpan.FromHours(2).Ticks;
    private const string BootA = "11111111111111111111111111111111";
    private const string BootB = "22222222222222222222222222222222";

    private readonly string _tempFile;

    public PendingRebootStoreTests()
    {
        _tempFile = Path.Combine(
            Path.GetTempPath(),
            $"unigetui-reboottest-{Guid.NewGuid():N}.json");
        PendingRebootStore.TestFilePathOverride = _tempFile;
        PendingRebootStore.InvalidateCache();
        PendingRebootStore.ClearAll();
        BootSession.TestUptimeOverride = TwoHours;
        BootSession.TestIdOverride = BootA;
    }

    public void Dispose()
    {
        BootSession.TestUptimeOverride = null;
        BootSession.TestIdOverride = null;
        PendingRebootStore.TestFilePathOverride = null;
        PendingRebootStore.InvalidateCache();
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); }
        catch { /* best-effort cleanup */ }
    }

    private static IPackage Package(string id = "Contoso.Test")
        => new PackageBuilder().WithId(id).WithName(id).Build();

    [Fact]
    public void RecordedPackageIsReportedAsPending()
    {
        var package = Package();

        PendingRebootStore.Record(package, OperationType.Install);

        Assert.Equal(1, PendingRebootStore.PendingCount);
        Assert.True(PendingRebootStore.IsPending(package.Manager.Id, package.Id));
        Assert.Equal("install-package", PendingRebootStore.GetPending()[0].Kind);
    }

    [Fact]
    public void RecordingTheSamePackageTwiceKeepsASingleEntry()
    {
        var package = Package();

        PendingRebootStore.Record(package, OperationType.Install);
        PendingRebootStore.Record(package, OperationType.Update);

        Assert.Equal(1, PendingRebootStore.PendingCount);
        Assert.Equal("update-package", PendingRebootStore.GetPending()[0].Kind);
    }

    [Fact]
    public void ClearRemovesOnlyTheGivenPackage()
    {
        var kept = Package("Contoso.Kept");
        var dropped = Package("Contoso.Dropped");

        PendingRebootStore.Record(kept, OperationType.Install);
        PendingRebootStore.Record(dropped, OperationType.Install);
        PendingRebootStore.Clear(dropped.Manager.Id, dropped.Id);

        Assert.Equal(1, PendingRebootStore.PendingCount);
        Assert.True(PendingRebootStore.IsPending(kept.Manager.Id, kept.Id));
    }

    [Fact]
    public void ReopeningTheAppWithinTheSameBootKeepsPendingPackages()
    {
        PendingRebootStore.Record(Package(), OperationType.Install);

        PendingRebootStore.InvalidateCache();

        Assert.Equal(1, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void UptimeAdvancingWithinTheSameBootKeepsPendingPackages()
    {
        PendingRebootStore.Record(Package(), OperationType.Install);

        BootSession.TestUptimeOverride = TwoHours + TimeSpan.FromHours(30).Ticks;
        PendingRebootStore.InvalidateCache();

        Assert.Equal(1, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void RebootingTheMachineClearsPendingPackages()
    {
        PendingRebootStore.Record(Package(), OperationType.Install);

        BootSession.TestIdOverride = BootB;
        BootSession.TestUptimeOverride = TimeSpan.FromSeconds(45).Ticks;
        PendingRebootStore.InvalidateCache();

        Assert.Equal(0, PendingRebootStore.PendingCount);
        Assert.False(PendingRebootStore.HasPending);
    }

    [Fact]
    public void RebootIsDetectedEvenWhenTheEntryWasRecordedEarlyInThePreviousBoot()
    {
        BootSession.TestUptimeOverride = TimeSpan.FromMinutes(2).Ticks;
        PendingRebootStore.Record(Package(), OperationType.Install);

        BootSession.TestIdOverride = BootB;
        BootSession.TestUptimeOverride = TimeSpan.FromHours(1).Ticks;
        PendingRebootStore.InvalidateCache();

        Assert.Equal(0, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void WithoutABootIdTheUptimeCounterStillDetectsAReboot()
    {
        BootSession.TestIdOverride = "";
        PendingRebootStore.Record(Package(), OperationType.Install);

        BootSession.TestUptimeOverride = TimeSpan.FromSeconds(45).Ticks;
        PendingRebootStore.InvalidateCache();

        Assert.Equal(0, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void ClearAllRemovesEveryEntry()
    {
        PendingRebootStore.Record(Package("Contoso.One"), OperationType.Install);
        PendingRebootStore.Record(Package("Contoso.Two"), OperationType.Install);

        PendingRebootStore.ClearAll();

        Assert.Equal(0, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void MalformedEntriesAreDiscardedInsteadOfCrashingTheStore()
    {
        File.WriteAllText(_tempFile,
            "[null,{\"PackageId\":null,\"ManagerName\":\"choco\",\"BootId\":\"" + BootA + "\"},"
            + "{\"PackageId\":\"Contoso.Good\",\"ManagerName\":\"choco\",\"PackageName\":null,"
            + "\"BootId\":\"" + BootA + "\",\"UptimeTicks\":1}]");

        PendingRebootStore.InvalidateCache();

        Assert.Equal(1, PendingRebootStore.PendingCount);
        Assert.True(PendingRebootStore.IsPending("choco", "Contoso.Good"));
        Assert.Equal("", PendingRebootStore.GetPending()[0].PackageName);
    }

    [Fact]
    public void RecordsWrittenByAnotherSessionAreObserved()
    {
        PendingRebootStore.Record(Package("Contoso.Mine"), OperationType.Install);
        Assert.Equal(1, PendingRebootStore.PendingCount);

        File.WriteAllText(_tempFile,
            "[{\"PackageId\":\"Contoso.Mine\",\"ManagerName\":\"Test Manager\",\"BootId\":\"" + BootA + "\",\"UptimeTicks\":1},"
            + "{\"PackageId\":\"Contoso.Theirs\",\"ManagerName\":\"Test Manager\",\"BootId\":\"" + BootA + "\",\"UptimeTicks\":1}]");

        Assert.Equal(2, PendingRebootStore.PendingCount);
        Assert.True(PendingRebootStore.IsPending("Test Manager", "Contoso.Theirs"));
    }

    [Fact]
    public void RealUptimeSourceIsPositiveAndMonotonic()
    {
        BootSession.TestUptimeOverride = null;

        long first = BootSession.GetUptimeTicks();
        long second = BootSession.GetUptimeTicks();

        Assert.True(first > 0);
        Assert.True(second >= first);
    }

    [Fact]
    public void RealBootIdIsStableWithinTheSession()
    {
        BootSession.TestIdOverride = null;

        string first = BootSession.GetId();
        string second = BootSession.GetId();

        Assert.Equal(first, second);
        if (OperatingSystem.IsWindows()) Assert.NotEqual("", first);
    }

    [Fact]
    public void SurvivesAReloadUsingTheRealUptimeSource()
    {
        BootSession.TestUptimeOverride = null;
        BootSession.TestIdOverride = null;
        PendingRebootStore.Record(Package(), OperationType.Install);

        PendingRebootStore.InvalidateCache();

        Assert.Equal(1, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void EntriesWithNeitherBootIdNorUptimeStampAreDiscarded()
    {
        PendingRebootStore.Record(Package(), OperationType.Install);
        var entry = PendingRebootStore.GetPending()[0];
        entry.BootId = "";
        entry.UptimeTicks = 0;
        File.WriteAllText(
            _tempFile,
            System.Text.Json.JsonSerializer.Serialize(new[] { entry }));

        PendingRebootStore.InvalidateCache();

        Assert.Equal(0, PendingRebootStore.PendingCount);
    }

    [Fact]
    public void AMatchingBootIdOutweighsAMissingUptimeStamp()
    {
        PendingRebootStore.Record(Package(), OperationType.Install);
        var entry = PendingRebootStore.GetPending()[0];
        entry.UptimeTicks = 0;
        File.WriteAllText(
            _tempFile,
            System.Text.Json.JsonSerializer.Serialize(new[] { entry }));

        PendingRebootStore.InvalidateCache();

        Assert.Equal(1, PendingRebootStore.PendingCount);
    }
}
