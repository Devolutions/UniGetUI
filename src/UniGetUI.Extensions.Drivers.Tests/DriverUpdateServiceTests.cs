using UniGetUI.Extensions.Drivers;

namespace UniGetUI.Extensions.Drivers.Tests;

public class DriverUpdateServiceTests
{
    private sealed class FakeRunner(params string[] outputs) : IDriverScriptRunner
    {
        private int _next;
        public List<(string Script, bool Elevated)> Calls { get; } = [];

        public Task<ScriptOutput> RunAsync(string script, bool elevated, CancellationToken cancellationToken)
        {
            Calls.Add((script, elevated));
            return Task.FromResult(new ScriptOutput(0, outputs[_next++], ""));
        }
    }

    private const string ScanOutput = """
        WARNING: something noisy
        UNIGETUI-DRIVERS:{"updates":[{"id":"8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b","revision":201,"title":"Intel - System - 10.1.1.44","description":"Intel\u00ae chipset","driverClass":"System","manufacturer":"Intel","model":"Intel(R) SMBus - 7A23","provider":"Intel","hardwareId":"PCI\\VEN_8086&DEV_7A23","driverDate":"2024-03-01","size":1048576,"rebootBehavior":2,"supportUrl":"https://support.microsoft.com","eulaAccepted":true,"deviceName":"Intel(R) SMBus","installedVersion":"10.1.1.38","installedDate":"2023-01-15"},{"id":"8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b","revision":201,"title":"duplicate","description":"","driverClass":"","manufacturer":"","model":"","provider":"","hardwareId":"","driverDate":null,"size":0,"rebootBehavior":0,"supportUrl":"","eulaAccepted":true,"deviceName":null,"installedVersion":null,"installedDate":null}]}
        """;

    [WindowsFact]
    public async Task ScanReadsTheMarkedLineAndDropsDuplicates()
    {
        var runner = new FakeRunner(ScanOutput);

        IReadOnlyList<DriverUpdate> updates = await new DriverUpdateService(runner).ScanAsync();

        DriverUpdate update = Assert.Single(updates);
        Assert.Equal("Intel - System - 10.1.1.44", update.Title);
        Assert.Equal("Intel\u00ae chipset", update.Description);
        Assert.Equal(@"PCI\VEN_8086&DEV_7A23", update.HardwareId);
        Assert.Equal(DriverRebootBehavior.CanRequestReboot, update.RebootBehavior);
        Assert.Equal(1048576, update.Size);
        Assert.Equal("10.1.1.38", update.InstalledVersion);
        Assert.Equal("2024-03-01", update.DriverDate);
        Assert.False(Assert.Single(runner.Calls).Elevated);
    }

    [WindowsFact]
    public async Task ScanReportsAnEmptyListWhenNothingIsPending()
    {
        var service = new DriverUpdateService(new FakeRunner("UNIGETUI-DRIVERS:{\"updates\":[]}"));

        Assert.Empty(await service.ScanAsync());
    }

    [WindowsTheory]
    [InlineData(unchecked((int)0x8024402C), DriversErrorKind.Offline)]
    [InlineData(unchecked((int)0x8024002E), DriversErrorKind.DisabledByPolicy)]
    [InlineData(unchecked((int)0x80070422), DriversErrorKind.ServiceUnavailable)]
    [InlineData(unchecked((int)0x80004005), DriversErrorKind.Unknown)]
    public async Task ScanClassifiesWindowsUpdateErrors(int hResult, DriversErrorKind kind)
    {
        var service = new DriverUpdateService(new FakeRunner($"UNIGETUI-DRIVERS:{{\"error\":\"failed\",\"hResult\":{hResult}}}"));

        var ex = await Assert.ThrowsAsync<DriversException>(() => service.ScanAsync());

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(hResult, ex.ErrorCode);
    }

    [WindowsFact]
    public async Task ScanFailsWhenTheScriptPrintsNoResult()
    {
        var service = new DriverUpdateService(new FakeRunner("powershell.exe : something went wrong"));

        var ex = await Assert.ThrowsAsync<DriversException>(() => service.ScanAsync());

        Assert.Equal(DriversErrorKind.UnreadableOutput, ex.Kind);
    }

    [WindowsFact]
    public async Task InstallRunsElevatedWithOnlyTheRequestedIds()
    {
        var runner = new FakeRunner("""
            UNIGETUI-DRIVERS:{"results":[{"id":"8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b","resultCode":2,"hResult":0,"rebootRequired":true},{"id":"00000000-0000-0000-0000-000000000001","resultCode":100,"hResult":0,"rebootRequired":false}],"rebootRequired":false}
            """);

        DriverInstallReport report = await new DriverUpdateService(runner)
            .InstallAsync(["8C3E5F0A-6B1D-4E2F-9A7B-1C2D3E4F5A6B", "00000000-0000-0000-0000-000000000001"]);

        (string script, bool elevated) = Assert.Single(runner.Calls);
        Assert.True(elevated);
        Assert.Contains("$Ids = @('8c3e5f0a-6b1d-4e2f-9a7b-1c2d3e4f5a6b', '00000000-0000-0000-0000-000000000001')", script);
        Assert.True(report.RebootRequired);
        Assert.True(report.Results[0].Succeeded);
        Assert.Equal(DriverInstallResultCode.NotFound, report.Results[1].ResultCode);
    }

    [WindowsFact]
    public async Task InstallOfNothingRunsNothing()
    {
        var runner = new FakeRunner();

        DriverInstallReport report = await new DriverUpdateService(runner).InstallAsync([]);

        Assert.Empty(report.Results);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData("'); Remove-Item C:\\ -Recurse; ('")]
    [InlineData("not-a-guid")]
    public void InstallScriptRefusesIdsThatAreNotGuids(string id)
        => Assert.Throws<FormatException>(() => DriverScripts.Install([id]));
}
