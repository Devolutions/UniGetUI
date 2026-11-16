using UniGetUI.Extensions.Drivers;

namespace UniGetUI.Extensions.Drivers.Tests;

public class DriverRunnerAndCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(DriverRunnerAndCacheTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void AnUnelevatedRunStartsTheSystemPowerShellDirectly()
    {
        var startInfo = PowerShellDriverScriptRunner.BuildStartInfo(elevator: null);

        Assert.Equal(PowerShellDriverScriptRunner.PowerShellPath, startInfo.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", DriverScripts.EncodedStandardInputLoader], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void AnElevatedRunGoesThroughTheElevatorWithItsArguments()
    {
        var elevation = new DriversElevation(false, false, @"C:\Tools\elevator.exe", ["-A", "--direct"]);

        var startInfo = PowerShellDriverScriptRunner.BuildStartInfo(elevation);

        Assert.Equal(@"C:\Tools\elevator.exe", startInfo.FileName);
        Assert.Equal(["-A", "--direct", PowerShellDriverScriptRunner.PowerShellPath], startInfo.ArgumentList.Take(3));
        Assert.Equal(DriverScripts.EncodedStandardInputLoader, startInfo.ArgumentList[^1]);
    }

    [Theory]
    [InlineData(true, "", DriversErrorKind.ElevationProhibited)]
    [InlineData(false, "", DriversErrorKind.NoElevator)]
    public void AnElevationThatCannotHappenIsRefusedBeforeStarting(bool prohibited, string elevatorPath, DriversErrorKind kind)
    {
        var elevation = new DriversElevation(false, prohibited, elevatorPath, []);

        var ex = Assert.Throws<DriversException>(() => PowerShellDriverScriptRunner.BuildStartInfo(elevation));

        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task ProhibitedElevationIsRefusedEvenWhenAlreadyAdministrator()
    {
        bool prompted = false;
        var runner = new PowerShellDriverScriptRunner(() => new DriversElevation(true, true, "", [], () => prompted = true));

        var ex = await Assert.ThrowsAsync<DriversException>(() => runner.RunAsync("exit", elevated: true, CancellationToken.None));

        Assert.Equal(DriversErrorKind.ElevationProhibited, ex.Kind);
        Assert.False(prompted);
    }

    [Theory]
    [InlineData(1223, "", "", true)]
    [InlineData(999, "", "gsudo: The operation was canceled by the user.", true)]
    [InlineData(999, "", "gsudo: something else failed", false)]
    [InlineData(1, "", "Program 'powershell.exe' failed to run: blocked by group policy", false)]
    [InlineData(0, "", "", false)]
    [InlineData(1223, "UNIGETUI-DRIVERS:{}", "", false)]
    public void OnlyARealCancellationIsReportedAsOne(int exitCode, string output, string error, bool cancelled)
        => Assert.Equal(cancelled, PowerShellDriverScriptRunner.IsElevationCancelled(exitCode, output, error));

    [Fact]
    public void TheScanSkipsTheDeviceLookupWhenNothingIsOffered()
        => Assert.Contains("if ($result.Updates.Count -gt 0) { try {", DriverScripts.Scan());

    [Fact]
    public void TheCacheRoundTripsAScan()
    {
        var cache = new DriverScanCache(Path.Combine(_root, "nested", "drivers.json"));
        var checkedAt = new DateTimeOffset(2026, 11, 13, 8, 30, 0, TimeSpan.FromHours(-5));
        DriverUpdate update = new() { Id = "8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b", Title = "Contoso \u00ae Display", RebootBehavior = DriverRebootBehavior.CanRequestReboot, Size = 42 };

        cache.Save(new DriverScanSnapshot([update], checkedAt));
        DriverScanSnapshot? loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Equal(checkedAt, loaded.CheckedAt);
        Assert.Equal(update, Assert.Single(loaded.Updates));
    }

    [Fact]
    public void AMissingOrCorruptCacheReadsAsNothing()
    {
        var cache = new DriverScanCache(Path.Combine(_root, "drivers.json"));
        Assert.Null(cache.Load());

        Directory.CreateDirectory(_root);
        File.WriteAllText(cache.Path, "{ not json");

        Assert.Null(cache.Load());
    }
}
