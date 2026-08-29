using System.Security.AccessControl;
using System.Security.Principal;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>
/// The access-control rules an install location must satisfy before an elevated policy write is
/// allowed to originate from it.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately replaces the old lexical "is the path under Program Files" test. A string
/// prefix proves nothing: the path could contain a junction, the directory could be owned by a
/// standard user, or its DACL could grant a non-administrator the right to swap the binary between
/// the check and the launch. The rules here are evaluated against a security descriptor that was
/// read from a <em>held handle</em> to the object itself, so they describe the object the process
/// actually opened rather than a name that may since have been re-pointed.
/// </para>
/// <para>
/// The question asked of every object is narrow: could any principal outside
/// <see cref="IsTrustedPrincipal"/> write to, delete, replace, or take control of it? If yes, the
/// location is rejected. That is intentionally stricter than "the installer works": a machine-wide
/// policy write must never be launchable from a tree a standard user can modify.
/// </para>
/// </remarks>
public static class PolicyElevationAccessPolicy
{
    // ---- Access mask bits (winnt.h) ---------------------------------------------------------

    public const uint FileWriteData = 0x0002;         // also FILE_ADD_FILE on a directory
    public const uint FileAppendData = 0x0004;        // also FILE_ADD_SUBDIRECTORY on a directory
    public const uint FileWriteEa = 0x0010;
    public const uint FileDeleteChild = 0x0040;
    public const uint FileWriteAttributes = 0x0100;
    public const uint Delete = 0x00010000;
    public const uint WriteDac = 0x00040000;
    public const uint WriteOwner = 0x00080000;
    public const uint GenericAll = 0x10000000;
    public const uint GenericWrite = 0x40000000;

    /// <summary>
    /// Rights that let a principal alter the bytes of, unlink, or seize control of a packaged file.
    /// </summary>
    public const uint FileControlMask =
        FileWriteData | FileAppendData | FileWriteEa | FileWriteAttributes
        | Delete | WriteDac | WriteOwner | GenericWrite | GenericAll;

    /// <summary>
    /// Rights that let a principal add to, remove from, or seize control of a packaged directory.
    /// </summary>
    public const uint DirectoryControlMask =
        FileWriteData | FileAppendData | FileWriteEa | FileWriteAttributes | FileDeleteChild
        | Delete | WriteDac | WriteOwner | GenericWrite | GenericAll;

    /// <summary>
    /// Rights that let a principal replace or redirect a directory that merely <em>contains</em>
    /// the install tree.
    /// </summary>
    /// <remarks>
    /// Creating an unrelated sibling entry inside an ancestor is harmless and is granted to
    /// standard users on a stock volume root, so <see cref="FileWriteData"/> and
    /// <see cref="FileAppendData"/> are deliberately excluded here. What matters is whether the
    /// ancestor can be used to delete, rename or re-permission the directory on the way down.
    /// </remarks>
    public const uint AncestorControlMask =
        FileDeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    // ---- Trusted principals ------------------------------------------------------------------

    /// <summary>NT AUTHORITY\SYSTEM.</summary>
    public const string LocalSystemSid = "S-1-5-18";

    /// <summary>BUILTIN\Administrators.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>NT SERVICE\TrustedInstaller, the owner of servicing-managed program files.</summary>
    public const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>
    /// Whether a trustee may hold write, delete or security-control rights over a packaged object.
    /// </summary>
    public static bool IsTrustedPrincipal(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return false;
        }

        string value = sid.Value;
        return string.Equals(value, LocalSystemSid, StringComparison.Ordinal)
            || string.Equals(value, AdministratorsSid, StringComparison.Ordinal)
            || string.Equals(value, TrustedInstallerSid, StringComparison.Ordinal);
    }

    /// <summary>
    /// Evaluates a security descriptor read from a held handle.
    /// </summary>
    /// <param name="descriptor">The object's owner and DACL.</param>
    /// <param name="controlMask">Which rights count as control over this kind of object.</param>
    /// <param name="failure">
    /// A developer-log description of the first violation. Never shown to a user.
    /// </param>
    public static bool IsControlRestrictedToTrustedPrincipals(
        RawSecurityDescriptor? descriptor,
        uint controlMask,
        out string? failure)
    {
        failure = null;

        if (descriptor is null)
        {
            failure = "The object exposed no security descriptor.";
            return false;
        }

        if (!IsTrustedPrincipal(descriptor.Owner))
        {
            failure = $"The object is owned by '{descriptor.Owner?.Value ?? "<none>"}'.";
            return false;
        }

        RawAcl? dacl = descriptor.DiscretionaryAcl;
        if (dacl is null)
        {
            // A NULL DACL grants everyone everything.
            failure = "The object has a NULL DACL, which grants full control to everyone.";
            return false;
        }

        foreach (GenericAce ace in dacl)
        {
            if (ace is not CommonAce common)
            {
                // Object ACEs and conditional ACEs are not part of a stock file-system layout;
                // refuse to reason about anything this code was not written to evaluate.
                failure = $"The object carries an unsupported ACE type '{ace.AceType}'.";
                return false;
            }

            if (common.AceType is AceType.AccessDenied)
            {
                // A supported simple deny ACE only narrows access.
                continue;
            }

            if (common.AceType is not AceType.AccessAllowed)
            {
                // Callback/conditional grants cannot be evaluated without an authorization
                // context. Treat every unsupported allow shape as controlling access.
                failure = $"The object carries an unsupported ACE type '{common.AceType}'.";
                return false;
            }

            if ((common.AceFlags & AceFlags.InheritOnly) is not 0)
            {
                // An inherit-only ACE does not apply to this object.
                continue;
            }

            if (((uint)common.AccessMask & controlMask) is 0)
            {
                continue;
            }

            if (!IsTrustedPrincipal(common.SecurityIdentifier))
            {
                failure =
                    $"'{common.SecurityIdentifier.Value}' is granted 0x{(uint)common.AccessMask:X8} "
                    + "over the object.";
                return false;
            }
        }

        return true;
    }
}
