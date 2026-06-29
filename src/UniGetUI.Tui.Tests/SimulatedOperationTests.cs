using UniGetUI.PackageEngine.Enums;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

// These tests mutate process-wide environment variables, so they all live in one class
// (xUnit runs tests within a class sequentially) and restore the originals on Dispose.
public class SimulatedOperationTests : IDisposable
{
    private const string SimVar = "UNIGETUI_TUI_SIMULATE";
    private const string DebugVar = "UNIGETUI_TUI_DEBUG_OP";

    private readonly string? _oldSim = Environment.GetEnvironmentVariable(SimVar);
    private readonly string? _oldDebug = Environment.GetEnvironmentVariable(DebugVar);

    public SimulatedOperationTests()
    {
        Environment.SetEnvironmentVariable(SimVar, null);
        Environment.SetEnvironmentVariable(DebugVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SimVar, _oldSim);
        Environment.SetEnvironmentVariable(DebugVar, _oldDebug);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)] // only the exact literal "1" enables simulation
    [InlineData("1", true)]
    public void IsEnabled_RequiresExactLiteralOne(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable(SimVar, value);
        Assert.Equal(expected, SimulatedOperation.IsEnabled);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("sim", false)]
    [InlineData("fail", true)]
    public void ShouldFail_RequiresExactLiteralFail(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable(DebugVar, value);
        Assert.Equal(expected, SimulatedOperation.ShouldFail);
    }

    [Fact]
    public void Metadata_IsFullyPopulated_WithSimulationPrefix()
    {
        var op = new SimulatedOperation("Install", "FakePkg");

        Assert.StartsWith("[SIMULATION]", op.Metadata.Title);
        Assert.Contains("Install", op.Metadata.Title);
        Assert.Contains("FakePkg", op.Metadata.Title);
        // MainThread() throws InvalidDataException on any empty metadata field; assert none are.
        Assert.NotEqual("", op.Metadata.Status);
        Assert.NotEqual("", op.Metadata.OperationInformation);
        Assert.NotEqual("", op.Metadata.SuccessTitle);
        Assert.NotEqual("", op.Metadata.SuccessMessage);
        Assert.NotEqual("", op.Metadata.FailureTitle);
        Assert.NotEqual("", op.Metadata.FailureMessage);
    }

    [Fact]
    public void Metadata_BlankInputs_FallBackToPlaceholders()
    {
        var op = new SimulatedOperation("", "  ");
        Assert.Contains("Process", op.Metadata.Title);
        Assert.Contains("package", op.Metadata.Title);
    }

    [Fact]
    public async Task MainThread_Succeeds_AndStreamsSimulationLines()
    {
        var op = new SimulatedOperation("Update", "FakePkg");
        bool finished = false;
        op.OperationFinished += (_, _) => finished = true;

        await op.MainThread();

        Assert.Equal(OperationStatus.Succeeded, op.Status);
        Assert.True(finished);
        var lines = op.GetOutput().Select(l => l.Item1).ToList();
        Assert.Contains(lines, l => l.Contains("[SIMULATION]") && l.Contains("succeeded"));
        // Every streamed step is clearly marked as fake.
        Assert.True(lines.Count(l => l.StartsWith("[SIMULATION]")) >= 10);
    }

    [Fact]
    public async Task MainThread_FailsWhenForced()
    {
        Environment.SetEnvironmentVariable(DebugVar, "fail");
        var op = new SimulatedOperation("Install", "FakePkg");
        bool failed = false;
        op.OperationFailed += (_, _) => failed = true;

        await op.MainThread();

        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.True(failed);
    }

    [Fact]
    public async Task MainThread_HonorsCancellation()
    {
        var op = new SimulatedOperation("Uninstall", "FakePkg");
        Task run = op.MainThread();

        await Task.Delay(300); // let a couple of fake steps stream
        op.Cancel();
        await run;

        Assert.Equal(OperationStatus.Canceled, op.Status);
    }
}
