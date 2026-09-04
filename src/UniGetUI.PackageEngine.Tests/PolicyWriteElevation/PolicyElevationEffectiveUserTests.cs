#if WINDOWS
using System.Diagnostics;
using System.Security.Principal;
using UniGetUI.AgentPolicy.ElevatedHelper;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyElevationEffectiveUserTests
{
    [Fact]
    public void AuthenticatedHostTokenSuppliesInitiatingUser()
    {
        using Process current = Process.GetCurrentProcess();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        int result = PolicyElevationInitiatingUserResolver.Resolve(
            current.Handle,
            out string? effectiveUser);

        Assert.Equal(PolicyElevationProtocol.ExitSuccess, result);
        Assert.Equal(identity.Name, effectiveUser);
        Assert.InRange(
            effectiveUser!.Length,
            1,
            WindowsProcessInspector.MaxEffectiveUserCharacters);
    }

    [Fact]
    public void OverTheShoulderAdministratorDoesNotReplaceInitiatingUser()
    {
        const string initiatingUser = @"CONTOSO\initiating-user";
        const string elevatedAdministrator = @"ADMIN\approval-user";

        Devolutions.Now.Policy.Client.BrokerClientOptions options =
            PolicyReplacementExecutor.CreateClientOptions(initiatingUser);

        Assert.Equal(initiatingUser, options.EffectiveUser);
        Assert.NotEqual(elevatedAdministrator, options.EffectiveUser);
    }

    [Theory]
    [InlineData("")]
    [InlineData("user-only")]
    [InlineData(@"DOMAIN\")]
    [InlineData(@"\user")]
    [InlineData("DOMAIN\\user\n")]
    [InlineData(@"TOO\MANY\SEPARATORS")]
    public void NonCanonicalEffectiveUserIsRejected(string effectiveUser)
    {
        Assert.Throws<ArgumentException>(
            () => PolicyReplacementExecutor.CreateClientOptions(effectiveUser));
    }

    [Fact]
    public void OverlongEffectiveUserIsRejected()
    {
        string effectiveUser =
            "DOMAIN\\" + new string('a', WindowsProcessInspector.MaxEffectiveUserCharacters);

        Assert.Throws<ArgumentException>(
            () => PolicyReplacementExecutor.CreateClientOptions(effectiveUser));
    }

    [Fact]
    public void HostTokenLookupFailureFailsClosed()
    {
        int result = PolicyElevationInitiatingUserResolver.Resolve(
            nint.Zero,
            out string? effectiveUser);

        Assert.Equal(PolicyElevationProtocol.ExitPeerAuthenticationFailed, result);
        Assert.Null(effectiveUser);
    }
}
#endif
