#if WINDOWS
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// Creates the single-use named pipe the elevated helper connects back to.
/// </summary>
/// <remarks>
/// The pipe gets an explicit DACL — no inherited ACEs, no "everyone" — allowing only:
/// the initiating user's SID (so the non-elevated host owns it), BUILTIN\Administrators (so an
/// over-the-shoulder consent, where the elevated token belongs to a different admin account in
/// the same session, can still connect), and LOCAL SYSTEM. The name carries 128 bits of
/// cryptographic entropy and the server is created with <c>FirstPipeInstance</c>, so a squatter
/// cannot pre-create the name; only one instance and one connection are ever allowed.
/// </remarks>
public static class PolicyElevationPipeServer
{
    public const int PipeBufferBytes = 64 * 1024;

    public static string CreatePipeName()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(PolicyElevationProtocol.PipeNameEntropyCharacters / 2);
        return PolicyElevationProtocol.PipeNamePrefix + Convert.ToHexStringLower(entropy);
    }

    public static NamedPipeServerStream Create(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        if (!PolicyElevationLaunchArguments.IsValidPipeName(pipeName))
        {
            throw new ArgumentException("The pipe name is not a valid policy elevation pipe name.", nameof(pipeName));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
            inBufferSize: PipeBufferBytes,
            outBufferSize: PipeBufferBytes,
            CreateSecurity());
    }

    public static PipeSecurity CreateSecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier initiator = identity.User
            ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new PipeSecurity();
        security.SetOwner(initiator);

        security.AddAccessRule(new PipeAccessRule(
            initiator,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }
}
#endif
