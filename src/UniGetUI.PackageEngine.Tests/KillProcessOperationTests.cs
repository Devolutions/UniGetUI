using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class KillProcessOperationTests
{
    [Fact]
    public async Task SucceedsWhenNoProcessHasTheName()
    {
        using var operation = new KillProcessOperation($"unigetui-absent-{Guid.NewGuid():N}");

        await operation.MainThread().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
    }

}
