using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class IpcBulkUpdateTests
{
    private static IpcPackageOperationResult Queued(string operationId) => new()
    {
        Command = "update-all",
        OperationId = operationId,
        OperationStatus = "inqueue",
        Completed = false,
        Message = "The operation was queued for background execution.",
        Package = new IpcPackageInfo { Id = "Git.Git", Manager = "winget" },
    };

    [Fact]
    public void ParseKeepsUpdateAllOptions()
    {
        IpcCliParseResult result = IpcCliSyntax.Parse(
            ["package", "update-all", "--manager", "winget", "--wait", "true", "--timeout", "60"]
        );

        Assert.Equal(IpcCliParseStatus.Success, result.Status);
        Assert.Equal("update-all", result.Command);
        Assert.Equal(
            ["--manager", "winget", "--wait", "true", "--timeout", "60"],
            Assert.IsType<string[]>(result.EffectiveArgs)
        );
    }

    [Fact]
    public void QueuedResultWithNoUpdatesIsComplete()
    {
        IpcPackageBulkOperationResult result = IpcPackageApi.CreateQueuedBulkResult("update-all", []);

        Assert.Equal("success", result.Status);
        Assert.True(result.Completed);
        Assert.Equal(0, result.QueuedCount);
        Assert.Empty(result.Results);
        Assert.Equal("No updates are available.", result.Message);
    }

    [Fact]
    public void BulkResultRoundTripsThroughTheIpcJsonContext()
    {
        var result = new IpcPackageBulkOperationResult
        {
            Command = "update-manager",
            QueuedCount = 1,
            Results = [Queued("op-1")],
        };

        IpcPackageBulkOperationResult? parsed = IpcJson.Deserialize<IpcPackageBulkOperationResult>(
            IpcJson.Serialize(result)
        );

        Assert.NotNull(parsed);
        Assert.Equal("update-manager", parsed.Command);
        Assert.Equal(1, parsed.QueuedCount);
        Assert.Equal("op-1", Assert.Single(parsed.Results).OperationId);
    }

    [Fact]
    public void WaitedResultStaysPendingWhileTheOperationRuns()
    {
        IpcPackageOperationResult result = IpcClient.ToWaitedResult(
            Queued("op-1"),
            new IpcOperationInfo { Id = "op-1", Status = "running" }
        );

        Assert.False(result.Completed);
        Assert.Equal("success", result.Status);
        Assert.Equal("running", result.OperationStatus);
    }

    [Fact]
    public void WaitedResultReportsSuccess()
    {
        IpcPackageOperationResult result = IpcClient.ToWaitedResult(
            Queued("op-1"),
            new IpcOperationInfo { Id = "op-1", Status = "succeeded" }
        );

        Assert.True(result.Completed);
        Assert.Equal("success", result.Status);
        Assert.Null(result.Message);
    }

    [Fact]
    public void WaitedResultReportsFailureWithTheLastLine()
    {
        IpcPackageOperationResult result = IpcClient.ToWaitedResult(
            Queued("op-1"),
            new IpcOperationInfo { Id = "op-1", Status = "failed", LiveLine = "Installer exited with code 1603" }
        );

        Assert.True(result.Completed);
        Assert.Equal("error", result.Status);
        Assert.Equal("Installer exited with code 1603", result.Message);
    }

    [Fact]
    public void WaitedResultTreatsAnUntrackedOperationAsFailed()
    {
        IpcPackageOperationResult result = IpcClient.ToWaitedResult(Queued("op-1"), null);

        Assert.True(result.Completed);
        Assert.Equal("error", result.Status);
        Assert.Equal("unknown", result.OperationStatus);
        Assert.Equal("Git.Git", result.Package?.Id);
    }
}
