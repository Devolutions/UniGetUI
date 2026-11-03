using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UniGetUI.Core.Tools;

public enum SignatureKind
{
    Valid,
    NotSigned,
    UnsignedFileType,
    Tampered,
    UntrustedRoot,
    Expired,
    Revoked,
    DistrustedPublisher,
    Unverifiable,
}

public readonly record struct SignatureCheck(int Status, string? Signer)
{
    public bool IsValid => Status == 0;

    public SignatureKind Kind => Status switch
    {
        0 => SignatureKind.Valid,
        unchecked((int)0x800B0100) => SignatureKind.NotSigned,
        unchecked((int)0x800B0003) => SignatureKind.UnsignedFileType,
        unchecked((int)0x80096010) => SignatureKind.Tampered,
        unchecked((int)0x800B0109) => SignatureKind.UntrustedRoot,
        unchecked((int)0x800B0101) => SignatureKind.Expired,
        unchecked((int)0x800B010C) => SignatureKind.Revoked,
        unchecked((int)0x800B0111) => SignatureKind.DistrustedPublisher,
        _ => SignatureKind.Unverifiable,
    };

    public string Describe() => Kind switch
    {
        SignatureKind.Valid => $"signed by {Signer ?? "an unknown publisher"}",
        SignatureKind.NotSigned => "not signed",
        SignatureKind.UnsignedFileType => "of a file type that carries no Authenticode signature",
        SignatureKind.Tampered => "its signature does not match its contents",
        SignatureKind.UntrustedRoot => "signed with a certificate this computer does not trust",
        SignatureKind.Expired => "signed with an expired certificate",
        SignatureKind.Revoked => "signed with a revoked certificate",
        SignatureKind.DistrustedPublisher => "signed by a publisher this computer distrusts",
        _ => $"its signature could not be verified (0x{Status:X8})",
    };

    public string? Summary() => Kind switch
    {
        SignatureKind.Valid => CoreTools.Translate(
            "Signed by {0}",
            Signer ?? CoreTools.Translate("an unknown publisher")
        ),
        SignatureKind.NotSigned => CoreTools.Translate("Not signed"),
        SignatureKind.UnsignedFileType => null,
        SignatureKind.Tampered => CoreTools.Translate("Signature does not match the file"),
        SignatureKind.UntrustedRoot => CoreTools.Translate("Signed with an untrusted certificate"),
        SignatureKind.Expired => CoreTools.Translate("Signed with an expired certificate"),
        SignatureKind.Revoked => CoreTools.Translate("Signed with a revoked certificate"),
        SignatureKind.DistrustedPublisher => CoreTools.Translate("Signed by a distrusted publisher"),
        _ => CoreTools.Translate("Signature could not be verified ({0})", $"0x{Status:X8}"),
    };
}

[SupportedOSPlatform("windows")]
public static partial class AuthenticodeSignature
{
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevokeNone = 0;
    private const uint RevocationCheckChain = 0x40;
    private const uint RevocationCheckNone = 0x10;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureCheck VerifyFile(string path, bool checkRevocationOnline = true)
    {
        nint pathPointer = Marshal.StringToHGlobalUni(path);
        nint fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(new FileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<FileInfo>(),
                pcwszFilePath = pathPointer,
            }, fileInfoPointer, false);
            var data = new TrustData
            {
                cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = checkRevocationOnline ? RevokeWholeChain : RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = StateActionVerify,
                dwProvFlags = checkRevocationOnline ? RevocationCheckChain : RevocationCheckNone,
            };
            Guid action = GenericVerifyV2;
            int status = WinVerifyTrust(nint.Zero, ref action, ref data);
            string? signer = status == 0 ? ReadSigner(data.hWVTStateData) : null;
            data.dwStateAction = StateActionClose;
            WinVerifyTrust(nint.Zero, ref action, ref data);
            return new SignatureCheck(status, signer);
        }
        finally
        {
            Marshal.DestroyStructure<FileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    private static string? ReadSigner(nint state)
    {
        nint provider = state == nint.Zero ? nint.Zero : WTHelperProvDataFromStateData(state);
        if (provider == nint.Zero)
            return null;
        nint signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        nint certificate = signer == nint.Zero ? nint.Zero : WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == nint.Zero)
            return null;
        var entry = Marshal.PtrToStructure<ProviderCertificate>(certificate);
        if (entry.pCert == nint.Zero)
            return null;
        var context = Marshal.PtrToStructure<CertificateContext>(entry.pCert);
        if (context.pbCertEncoded == nint.Zero || context.cbCertEncoded == 0)
            return null;
        byte[] encoded = new byte[context.cbCertEncoded];
        Marshal.Copy(context.pbCertEncoded, encoded, 0, encoded.Length);
        try
        {
            using var parsed = X509CertificateLoader.LoadCertificate(encoded);
            return parsed.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSipClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszUrlReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

#pragma warning disable CS0649
    private struct ProviderCertificate
    {
        public uint cbStruct;
        public nint pCert;
    }

    private struct CertificateContext
    {
        public uint dwCertEncodingType;
        public nint pbCertEncoded;
        public uint cbCertEncoded;
        public nint pCertInfo;
        public nint hCertStore;
    }
#pragma warning restore CS0649

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    private static partial nint WTHelperProvDataFromStateData(nint state);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvCertFromChain")]
    private static partial nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
}
