using UniGetUI.Core.Data;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// PowerShell 7 is a manager on every platform UniGetUI supports, so the launcher it runs with
/// -File has to be deployed everywhere too. It used to be copied only by the Windows-conditioned
/// item group, which left every non-Windows operation on the concatenated -Command path.
/// </summary>
public sealed class OperationLauncherDeploymentTests
{
    [Fact]
    public void TheOperationLauncherIsDeployedOnThisPlatform()
    {
        Assert.True(
            File.Exists(CoreData.PowerShellOperationLauncher),
            $"The operation launcher was not found at {CoreData.PowerShellOperationLauncher}."
        );
    }
}
