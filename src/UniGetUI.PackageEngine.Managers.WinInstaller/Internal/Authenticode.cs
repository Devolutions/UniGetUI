using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal readonly record struct SignatureCheck(int Status, string? Signer)
{
    public bool IsValid => Status == 0;

    public string Describe() => Status switch
    {
        0 => $"signed by {Signer ?? "an unknown publisher"}",
        unchecked((int)0x800B0100) => "not signed",
        unchecked((int)0x80096010) => "its signature does not match its contents",
        unchecked((int)0x800B0109) => "signed with a certificate this computer does not trust",
        unchecked((int)0x800B0101) => "signed with an expired certificate",
        unchecked((int)0x800B010C) => "signed with a revoked certificate",
        unchecked((int)0x800B0111) => "signed by a publisher this computer distrusts",
        _ => $"its signature could not be verified (0x{Status:X8})",
    };
}

internal static partial class Authenticode
{
    internal static Func<string, SignatureCheck> Verify { get; set; } = VerifyFile;

    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckChain = 0x40;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static SignatureCheck VerifyFile(string path)
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
                fdwRevocationChecks = RevokeWholeChain,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = StateActionVerify,
                dwProvFlags = RevocationCheckChain,
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
