using System.Diagnostics;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class ShutdownEndToEndFactAttribute : FactAttribute
{
    public const string Variable = "UNIGETUI_SHUTDOWN_E2E";

    public ShutdownEndToEndFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "shutdown.exe is only available on Windows.";
        else if (Environment.GetEnvironmentVariable(Variable) is null or "0")
            Skip = $"Schedules a real shutdown of this computer a day out and aborts it; set {Variable}=1 to run.";
    }
}

public sealed class ShutdownCommandEndToEndTests
{
    private const string FarFutureShutdown = "/s /t 86400";

    private static int AbortPendingShutdown()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = "/a",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    [ShutdownEndToEndFact]
    public async Task TheRealShutdownCommand_SchedulesAShutdownThatCanBeAborted()
    {
        bool scheduled = false;
        try
        {
            scheduled = await SystemRestartService.RunShutdownExeAsync(FarFutureShutdown, "shutdown");
            Assert.True(scheduled);
        }
        finally
        {
            if (scheduled)
                Assert.Equal(0, AbortPendingShutdown());
        }
    }

    [Fact]
    public async Task ARejectedShutdownCommand_IsReportedAsFailed()
    {
        Assert.False(await SystemRestartService.RunShutdownExeAsync("/not-a-real-switch", "shutdown"));
    }
}
