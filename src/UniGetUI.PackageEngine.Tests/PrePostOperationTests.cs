using System.Diagnostics;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Tests.Infrastructure;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class PrePostOperationTests
{
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
