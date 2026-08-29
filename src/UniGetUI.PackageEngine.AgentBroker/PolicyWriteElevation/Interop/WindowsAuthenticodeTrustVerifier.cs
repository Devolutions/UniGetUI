#if WINDOWS
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

/// <summary>
/// Production trust verifier: the file must carry a valid Authenticode signature chain
/// (<c>WinVerifyTrust</c>), and the signer identity reported back is the one that chain
/// verification actually accepted.
/// </summary>
/// <remarks>
/// <para>
/// This type answers "is this file validly signed, and by whom"; it deliberately does not decide
/// whether that signer is acceptable. That decision belongs to
/// <see cref="PolicyElevationSignerBinding"/>, which requires both peers to carry the same signer.
/// Keeping the two apart is what makes the scheme rotation safe and keeps any notion of a pinned
/// constant out of the codebase.
/// </para>
/// <para>
/// <b>Dual signatures.</b> The signer public key is read out of the WinTrust provider state while
/// it is still open, so it is bound to the exact signature <c>WinVerifyTrust</c> validated under
/// <c>WINTRUST_ACTION_GENERIC_VERIFY_V2</c>. The file is never re-opened and no embedded
/// certificate is selected independently. If a binary carries several signatures, the digest
/// returned here is the one belonging to the verified signature and not, for instance, an appended
/// secondary signature that the verification never considered. A dual-signed release is therefore
/// safe: both peers report the signer of their own verified signature, and the binding compares
/// those.
/// </para>
/// <para>
/// <b>Redaction.</b> Rejection reasons are written for a user interface and never contain a path,
/// certificate subject or thumbprint. The path is attached to <c>Detail</c>, which is only ever
/// written to the developer log.
/// </para>
/// </remarks>
public sealed class WindowsAuthenticodeTrustVerifier : IPolicyElevationTrustVerifier
{
    public PolicyElevationTrustResult VerifyExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return PolicyElevationTrustResult.Rejected(
                "A policy elevation binary could not be identified.",
                "No executable path was supplied to the trust verifier.");
        }

        if (!File.Exists(executablePath))
        {
            return PolicyElevationTrustResult.Rejected(
                "A policy elevation binary is missing.",
                $"'{executablePath}' does not exist.");
        }

        AuthenticodeVerification verification = VerifyAuthenticodeSignature(executablePath);
        if (verification.Status is not 0)
        {
            return PolicyElevationTrustResult.Rejected(
                "A policy elevation binary is not validly signed.",
                $"Authenticode verification of '{executablePath}' failed with status 0x{verification.Status:X8}.",
                verification.Status);
        }

        if (verification.SignerPublicKeySha256 is null)
        {
            return PolicyElevationTrustResult.Rejected(
                "The publisher of a policy elevation binary could not be determined.",
                $"The verified signer certificate of '{executablePath}' could not be read.");
        }

        return PolicyElevationTrustResult.Signed(verification.SignerPublicKeySha256);
    }

    private readonly record struct AuthenticodeVerification(int Status, string? SignerPublicKeySha256);

    private static AuthenticodeVerification VerifyAuthenticodeSignature(string executablePath)
    {
        nint filePathPointer = Marshal.StringToHGlobalUni(executablePath);
        nint fileInfoPointer = nint.Zero;

        try
        {
            var fileInfo = new PolicyElevationNative.WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<PolicyElevationNative.WinTrustFileInfo>(),
                pcwszFilePath = filePathPointer,
                hFile = nint.Zero,
                pgKnownSubject = nint.Zero,
            };

            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PolicyElevationNative.WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = new PolicyElevationNative.WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<PolicyElevationNative.WinTrustData>(),
                dwUIChoice = PolicyElevationNative.WtdUiNone,
                fdwRevocationChecks = PolicyElevationNative.WtdRevokeNone,
                dwUnionChoice = PolicyElevationNative.WtdChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = PolicyElevationNative.WtdStateActionVerify,
                dwProvFlags = PolicyElevationNative.WtdSaferFlag
                    | PolicyElevationNative.WtdCacheOnlyUrlRetrieval,
            };

            Guid action = PolicyElevationNative.WinTrustActionGenericVerifyV2;
            int status = PolicyElevationNative.WinVerifyTrust(nint.Zero, ref action, ref trustData);

            // Read the signer while the state data is still open, so the identity is the one this
            // verification accepted rather than a certificate chosen by re-parsing the file.
            string? signerPublicKey = status is 0
                ? TryReadSignerPublicKeyDigest(trustData.hWVTStateData)
                : null;

            trustData.dwStateAction = PolicyElevationNative.WtdStateActionClose;
            PolicyElevationNative.WinVerifyTrust(nint.Zero, ref action, ref trustData);

            return new AuthenticodeVerification(status, signerPublicKey);
        }
        finally
        {
            if (fileInfoPointer != nint.Zero)
            {
                Marshal.DestroyStructure<PolicyElevationNative.WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeHGlobal(filePathPointer);
        }
    }

    private static string? TryReadSignerPublicKeyDigest(nint stateData)
    {
        if (stateData == nint.Zero)
        {
            return null;
        }

        nint providerData = PolicyElevationNative.WTHelperProvDataFromStateData(stateData);
        if (providerData == nint.Zero)
        {
            return null;
        }

        // Signer 0 of the provider state is the signature WinVerifyTrust validated; counter
        // signatures and any additional embedded signatures are intentionally not consulted.
        nint signer = PolicyElevationNative.WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signer == nint.Zero)
        {
            return null;
        }

        // Certificate 0 of that signer's chain is the leaf that made the signature.
        nint providerCertificate = PolicyElevationNative.WTHelperGetProvCertFromChain(signer, 0);
        if (providerCertificate == nint.Zero)
        {
            return null;
        }

        var certificateEntry = Marshal.PtrToStructure<PolicyElevationNative.CryptProviderCert>(providerCertificate);
        if (certificateEntry.pCert == nint.Zero)
        {
            return null;
        }

        var context = Marshal.PtrToStructure<PolicyElevationNative.CertContext>(certificateEntry.pCert);
        if (context.pbCertEncoded == nint.Zero || context.cbCertEncoded is 0)
        {
            return null;
        }

        byte[] encoded = new byte[context.cbCertEncoded];
        Marshal.Copy(context.pbCertEncoded, encoded, 0, encoded.Length);

        try
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(encoded);
            byte[] subjectPublicKeyInfo = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            return Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
#endif
