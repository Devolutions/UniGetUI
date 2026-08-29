using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// The packaged layout is a security boundary: the helper is only ever accepted at one exact path
/// under an administrator-protected root.
/// </summary>
public class PolicyElevationPathsTests
{
    private const string Root = @"C:\Program Files\UniGetUI";

    [Fact]
    public void HelperPath_IsTheExactPackagedLayout()
        => Assert.Equal(
            Path.Combine(Root, "Assets", "Utilities", "UniGetUI.PolicyElevator.exe"),
            PolicyElevationPaths.GetHelperPath(Root));

    [Fact]
    public void HostPath_IsTheExactPackagedLayout()
        => Assert.Equal(Path.Combine(Root, "UniGetUI.exe"), PolicyElevationPaths.GetHostPath(Root));

    [Fact]
    public void InstallRoot_IsRecoveredFromTheHelperPath()
    {
        Assert.True(PolicyElevationPaths.TryGetInstallRootFromHelperPath(
            PolicyElevationPaths.GetHelperPath(Root),
            out string? recovered));

        Assert.Equal(Root, recovered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\UniGetUI\UniGetUI.PolicyElevator.exe")]
    [InlineData(@"C:\Program Files\UniGetUI\Assets\UniGetUI.PolicyElevator.exe")]
    [InlineData(@"C:\Program Files\UniGetUI\Assets\Utilities\UniGetUI.exe")]
    [InlineData(@"C:\Program Files\UniGetUI\Utilities\Assets\UniGetUI.PolicyElevator.exe")]
    [InlineData(@"Assets\Utilities\UniGetUI.PolicyElevator.exe")]
    public void AnyOtherLayout_IsRejected(string? helperPath)
    {
        Assert.False(PolicyElevationPaths.TryGetInstallRootFromHelperPath(helperPath, out string? recovered));
        Assert.Null(recovered);
    }
}
