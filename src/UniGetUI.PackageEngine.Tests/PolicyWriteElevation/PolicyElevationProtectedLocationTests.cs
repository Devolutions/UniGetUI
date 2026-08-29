#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

/// <summary>
/// The access-control half of the handle-based protected-location check. These tests build real
/// security descriptors and ask the shipping rules about them, so no administrator token is needed
/// to prove that a weakly permissioned location is refused.
/// </summary>
public class PolicyElevationAccessPolicyTests
{
    private static readonly SecurityIdentifier System = new(PolicyElevationAccessPolicy.LocalSystemSid);
    private static readonly SecurityIdentifier Administrators = new(PolicyElevationAccessPolicy.AdministratorsSid);
    private static readonly SecurityIdentifier TrustedInstaller = new(PolicyElevationAccessPolicy.TrustedInstallerSid);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private static RawSecurityDescriptor Descriptor(
        SecurityIdentifier owner,
        params (AceQualifier Qualifier, AceFlags Flags, int Mask, SecurityIdentifier Sid)[] aces)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, aces.Length);
        int index = 0;
        foreach ((AceQualifier qualifier, AceFlags flags, int mask, SecurityIdentifier sid) in aces)
        {
            acl.InsertAce(index++, new CommonAce(flags, qualifier, mask, sid, false, null));
        }

        return new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, owner, owner, null, acl);
    }

    private static (AceQualifier, AceFlags, int, SecurityIdentifier) Allow(SecurityIdentifier sid, uint mask)
        => (AceQualifier.AccessAllowed, AceFlags.None, unchecked((int)mask), sid);

    [Fact]
    public void OnlySystemAdministratorsAndTrustedInstallerAreTrusted()
    {
        Assert.True(PolicyElevationAccessPolicy.IsTrustedPrincipal(System));
        Assert.True(PolicyElevationAccessPolicy.IsTrustedPrincipal(Administrators));
        Assert.True(PolicyElevationAccessPolicy.IsTrustedPrincipal(TrustedInstaller));

        Assert.False(PolicyElevationAccessPolicy.IsTrustedPrincipal(Users));
        Assert.False(PolicyElevationAccessPolicy.IsTrustedPrincipal(Everyone));
        Assert.False(PolicyElevationAccessPolicy.IsTrustedPrincipal(CreatorOwner));
        Assert.False(PolicyElevationAccessPolicy.IsTrustedPrincipal(WindowsIdentity.GetCurrent().User));
        Assert.False(PolicyElevationAccessPolicy.IsTrustedPrincipal(null));
    }

    [Fact]
    public void AStockProgramFilesShapedDescriptor_IsAccepted()
    {
        RawSecurityDescriptor descriptor = Descriptor(
            TrustedInstaller,
            Allow(TrustedInstaller, PolicyElevationAccessPolicy.GenericAll),
            Allow(System, PolicyElevationAccessPolicy.GenericAll),
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll),
            Allow(Users, 0x0020_00A9), // read, list, execute, synchronise
            Allow(Everyone, 0x0012_0089));

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.DirectoryControlMask,
                out string? failure),
            failure);
    }

    [Fact]
    public void AWeakAcl_ThatLetsAStandardUserWrite_IsRejected()
    {
        RawSecurityDescriptor descriptor = Descriptor(
            Administrators,
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll),
            Allow(Users, PolicyElevationAccessPolicy.FileWriteData));

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.FileControlMask,
                out string? failure));

        Assert.Contains(Users.Value, failure);
    }

    [Theory]
    [InlineData(PolicyElevationAccessPolicy.Delete)]
    [InlineData(PolicyElevationAccessPolicy.WriteDac)]
    [InlineData(PolicyElevationAccessPolicy.WriteOwner)]
    [InlineData(PolicyElevationAccessPolicy.FileDeleteChild)]
    [InlineData(PolicyElevationAccessPolicy.GenericAll)]
    public void EveryReplacementRight_GrantedToAnUntrustedPrincipal_IsRejected(uint right)
    {
        RawSecurityDescriptor descriptor = Descriptor(
            Administrators,
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll),
            Allow(Users, right));

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.AncestorControlMask,
                out _));
    }

    [Fact]
    public void AnUntrustedOwner_IsRejectedEvenWithAPerfectDacl()
    {
        // An owner always holds READ_CONTROL and WRITE_DAC implicitly, so ownership alone is enough
        // to re-permission the object.
        RawSecurityDescriptor descriptor = Descriptor(
            Users,
            Allow(System, PolicyElevationAccessPolicy.GenericAll),
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll));

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.DirectoryControlMask,
                out string? failure));

        Assert.Contains("owned by", failure);
    }

    [Fact]
    public void ANullDacl_IsRejected()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.None, System, System, null, null);

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.DirectoryControlMask,
                out string? failure));

        Assert.Contains("NULL DACL", failure);
    }

    [Fact]
    public void AMissingDescriptor_IsRejected()
        => Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                null,
                PolicyElevationAccessPolicy.FileControlMask,
                out _));

    [Fact]
    public void AnInheritOnlyGrant_DoesNotApplyToTheObjectItself()
    {
        // This is the shape of the stock "CREATOR OWNER: Full control, subfolders and files only"
        // entry on Program Files: it confers nothing on the directory that carries it.
        RawSecurityDescriptor descriptor = Descriptor(
            TrustedInstaller,
            Allow(TrustedInstaller, PolicyElevationAccessPolicy.GenericAll),
            (AceQualifier.AccessAllowed,
                AceFlags.InheritOnly | AceFlags.ContainerInherit | AceFlags.ObjectInherit,
                unchecked((int)PolicyElevationAccessPolicy.GenericAll),
                CreatorOwner));

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.DirectoryControlMask,
                out string? failure),
            failure);
    }

    [Fact]
    public void ADenyAce_NeverCausesARejection()
    {
        RawSecurityDescriptor descriptor = Descriptor(
            System,
            (AceQualifier.AccessDenied,
                AceFlags.None,
                unchecked((int)PolicyElevationAccessPolicy.GenericAll),
                Users),
            Allow(System, PolicyElevationAccessPolicy.GenericAll));

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.FileControlMask,
                out string? failure),
            failure);
    }

    [Fact]
    public void ACallbackAllowAce_IsRejectedWithoutEvaluatingItsCondition()
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 2);
        acl.InsertAce(
            0,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)(
                    PolicyElevationAccessPolicy.FileWriteData
                    | PolicyElevationAccessPolicy.Delete)),
                Users,
                true,
                [0, 0, 0, 0]));
        acl.InsertAce(
            1,
            new CommonAce(
                AceFlags.None,
                AceQualifier.AccessAllowed,
                unchecked((int)PolicyElevationAccessPolicy.GenericAll),
                Administrators,
                false,
                null));
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent,
            Administrators,
            Administrators,
            null,
            acl);

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.FileControlMask,
                out string? failure));
        Assert.Contains("unsupported ACE type", failure);
    }

    [Fact]
    public void SupportedSimpleTrustedAllowAce_RemainsAccepted()
    {
        RawSecurityDescriptor descriptor = Descriptor(
            Administrators,
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll));

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.FileControlMask,
                out string? failure),
            failure);
    }

    [Fact]
    public void AncestorRules_IgnoreHarmlessCreationRightsButNotDeletion()
    {
        // A stock volume root grants BUILTIN\Users "create folders / append data". Creating an
        // unrelated sibling cannot touch the install tree, so it must not fail the check.
        RawSecurityDescriptor volumeRoot = Descriptor(
            System,
            Allow(System, PolicyElevationAccessPolicy.GenericAll),
            Allow(Administrators, PolicyElevationAccessPolicy.GenericAll),
            Allow(Users, PolicyElevationAccessPolicy.FileAppendData));

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                volumeRoot,
                PolicyElevationAccessPolicy.AncestorControlMask,
                out string? failure),
            failure);

        // Being able to delete a child of that ancestor is a different matter entirely.
        RawSecurityDescriptor deletable = Descriptor(
            System,
            Allow(System, PolicyElevationAccessPolicy.GenericAll),
            Allow(Users, PolicyElevationAccessPolicy.FileDeleteChild));

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                deletable,
                PolicyElevationAccessPolicy.AncestorControlMask,
                out _));
    }
}

/// <summary>
/// The live handle-based verifier, exercised against directories this test can really create.
/// </summary>
public class WindowsProtectedLocationVerifierTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetDirectoryName(typeof(WindowsProtectedLocationVerifierTests).Assembly.Location)!,
        "policy-elevation-location-tests",
        Guid.NewGuid().ToString("n"));

    public WindowsProtectedLocationVerifierTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, true);
            }
        }
        catch (IOException)
        {
            // A leftover sandbox is not worth failing a test run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string CreateLayout(string rootName)
    {
        string root = Path.Combine(_sandbox, rootName);
        string utilities = Path.Combine(
            root,
            PolicyElevationProtocol.HelperRelativeDirectory,
            PolicyElevationProtocol.HelperRelativeSubDirectory);

        Directory.CreateDirectory(utilities);
        File.WriteAllText(PolicyElevationPaths.GetHostPath(root), "host");
        File.WriteAllText(PolicyElevationPaths.GetHelperPath(root), "helper");
        return root;
    }

    [Fact]
    public void AUserWritableLayout_IsRejected()
    {
        string root = CreateLayout("writable");

        using PolicyElevationLocationVerification verification = new WindowsProtectedLocationVerifier().Verify(
            root,
            PolicyElevationPaths.GetHelperPath(root),
            PolicyElevationPaths.GetHostPath(root));

        Assert.False(verification.IsProtected);
        Assert.Contains("administrator-protected", verification.FailureReason);

        // The user-facing reason names nothing sensitive; the specifics stay in the log detail.
        Assert.DoesNotContain(root, verification.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(verification.Detail);
    }

    [Fact]
    public void AMissingLayout_IsRejectedRatherThanThrowing()
    {
        string root = Path.Combine(_sandbox, "absent");

        using PolicyElevationLocationVerification verification = new WindowsProtectedLocationVerifier().Verify(
            root,
            PolicyElevationPaths.GetHelperPath(root),
            PolicyElevationPaths.GetHostPath(root));

        Assert.False(verification.IsProtected);
        Assert.NotNull(verification.Detail);
        Assert.Null(verification.CanonicalHelperPath);
    }

    [Fact]
    public void AJunctionInThePath_IsRejectedRatherThanFollowed()
    {
        string real = CreateLayout("real");
        string link = Path.Combine(_sandbox, "link");
        Directory.CreateSymbolicLink(link, real);

        using PolicyElevationLocationVerification verification = new WindowsProtectedLocationVerifier().Verify(
            link,
            PolicyElevationPaths.GetHelperPath(link),
            PolicyElevationPaths.GetHostPath(link));

        Assert.False(verification.IsProtected);
        Assert.Contains("administrator-protected", verification.FailureReason);

        // Either the reparse point itself or the security of the tree it lives in is refused; what
        // must never happen is the link being silently followed to a "protected" answer.
        Assert.NotNull(verification.Detail);
    }

    [Fact]
    public void AReparsePoint_IsRejectedByTheObjectRuleItself()
    {
        string real = CreateLayout("reparse-target");
        string link = Path.Combine(_sandbox, "reparse-link");
        Directory.CreateSymbolicLink(link, real);

        using PolicyElevationLocationVerification verification = WindowsProtectedLocationVerifier.InspectObject(
            link,
            isDirectory: true,
            PolicyElevationAccessPolicy.DirectoryControlMask);

        Assert.False(verification.IsProtected);
        Assert.Contains("reparse point", verification.Detail);
        Assert.DoesNotContain(link, verification.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AUserWritableDirectory_IsRejectedByTheAccessRule()
    {
        using PolicyElevationLocationVerification verification = WindowsProtectedLocationVerifier.InspectObject(
            _sandbox,
            isDirectory: true,
            PolicyElevationAccessPolicy.DirectoryControlMask);

        Assert.False(verification.IsProtected);
        Assert.Contains("not administrator-protected", verification.Detail);
    }

    [Fact]
    public void AUserWritableFile_IsRejectedByTheAccessRule()
    {
        string file = Path.Combine(_sandbox, "writable.exe");
        File.WriteAllText(file, "payload");

        using PolicyElevationLocationVerification verification = WindowsProtectedLocationVerifier.InspectObject(
            file,
            isDirectory: false,
            PolicyElevationAccessPolicy.FileControlMask);

        Assert.False(verification.IsProtected);
        Assert.Contains("not administrator-protected", verification.Detail);
    }

    [Fact]
    public void TheRealProgramFilesDirectory_PassesEveryObjectRule()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        using PolicyElevationLocationVerification verification = WindowsProtectedLocationVerifier.InspectObject(
            programFiles,
            isDirectory: true,
            PolicyElevationAccessPolicy.AncestorControlMask);

        Assert.True(verification.IsProtected, verification.Detail);
        Assert.Equal(programFiles, verification.CanonicalInstallRoot, ignoreCase: true);
    }

    [Fact]
    public void TheRealProgramFilesTree_SatisfiesTheAncestorRules()
    {
        // Sanity check against the actual machine: a stock Program Files must not be rejected by
        // the ancestor rules, or the shipping check would refuse every legitimate installation.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(Directory.Exists(programFiles));

        var security = new DirectoryInfo(programFiles).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);

        Assert.True(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.AncestorControlMask,
                out string? failure),
            failure);
    }

    [Fact]
    public void TheUsersProfileTree_DoesNotSatisfyTheDirectoryRules()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var security = new DirectoryInfo(profile).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);

        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);

        Assert.False(
            PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                PolicyElevationAccessPolicy.DirectoryControlMask,
                out _));
    }
}
#endif
