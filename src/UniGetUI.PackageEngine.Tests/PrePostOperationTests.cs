using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Tests.Infrastructure;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PrePostOperationTests
{
    [WindowsFact]
    public async Task StandardErrorOfASucceedingCommandIsNotReportedAsAnError()
    {
        List<(string Text, AbstractOperation.LineType Type)> lines = [];
        using var operation = new PrePostOperation("echo progress-on-stderr 1>&2");
        operation.LogLineAdded += (_, line) => lines.Add(line);
        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        var stderrLine = Assert.Single(lines, l => l.Text.Trim() == "progress-on-stderr");
        Assert.Equal(AbstractOperation.LineType.StandardError, stderrLine.Type);
        Assert.DoesNotContain(lines, l => AbstractOperation.IsErrorLine(l.Type, operationFailed: false));
    }

    [WindowsFact]
    public async Task StandardErrorOfAFailingCommandIsReportedAsAnError()
    {
        List<(string Text, AbstractOperation.LineType Type)> lines = [];
        using var operation = new PrePostOperation("echo broken 1>&2 & exit /b 3");
        operation.LogLineAdded += (_, line) => lines.Add(line);
        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var stderrLine = Assert.Single(lines, l => l.Text.Trim() == "broken");
        Assert.Equal(AbstractOperation.LineType.StandardError, stderrLine.Type);
        Assert.True(AbstractOperation.IsErrorLine(stderrLine.Type, operationFailed: true));
        Assert.Contains(lines, l => l.Type == AbstractOperation.LineType.Error);
    }

    [Theory]
    [InlineData(AbstractOperation.LineType.Error, false, true)]
    [InlineData(AbstractOperation.LineType.Error, true, true)]
    [InlineData(AbstractOperation.LineType.StandardError, false, false)]
    [InlineData(AbstractOperation.LineType.StandardError, true, true)]
    [InlineData(AbstractOperation.LineType.Information, true, false)]
    [InlineData(AbstractOperation.LineType.VerboseDetails, true, false)]
    public void StandardErrorIsAnErrorOnlyWhenTheOperationFailed(
        AbstractOperation.LineType type,
        bool failed,
        bool expected
    )
    {
        Assert.Equal(expected, AbstractOperation.IsErrorLine(type, failed));
        Assert.Equal(expected, AbstractOperation.IsErrorLine(type.ToString(), failed));
    }

    [WindowsFact]
    public async Task AVariablePersistedByACommandReachesProcessesStartedAfterIt()
    {
        string name = "UNIGETUI_PREPOST_ENVIRONMENT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        try
        {
            PersistedEnvironment.TEST_ResetBaseline();
            using var setter = new PrePostOperation($"setx {name} fromhook");
            await setter.MainThread().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(OperationStatus.Succeeded, setter.Status);

            List<string> lines = [];
            using var reader = new PrePostOperation($"echo value=%{name}%");
            reader.LogLineAdded += (_, line) => lines.Add(line.Item1);
            await reader.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(OperationStatus.Succeeded, reader.Status);
            Assert.Contains("value=fromhook", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            PersistedEnvironment.TEST_ResetBaseline();
        }
    }

    [WindowsFact]
    public async Task AVariablePersistedOutsideUniGetUIReachesTheNextOperation()
    {
        string name = "UNIGETUI_EXTERNAL_ENVIRONMENT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        try
        {
            PersistedEnvironment.TEST_ResetBaseline();
            using (
                var setx = Process.Start(
                    new ProcessStartInfo("setx", $"{name} fromterminal")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                    }
                )!
            )
                await setx.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));

            List<string> lines = [];
            using var reader = new PrePostOperation($"echo value=%{name}%");
            reader.LogLineAdded += (_, line) => lines.Add(line.Item1);
            await reader.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(OperationStatus.Succeeded, reader.Status);
            Assert.Contains("value=fromterminal", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            PersistedEnvironment.TEST_ResetBaseline();
        }
    }
}
