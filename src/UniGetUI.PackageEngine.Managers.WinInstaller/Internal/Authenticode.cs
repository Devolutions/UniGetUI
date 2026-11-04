using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal static class Authenticode
{
    internal static Func<string, SignatureCheck> Verify { get; set; } = VerifyFile;

    internal static SignatureCheck VerifyFile(string path) => AuthenticodeSignature.VerifyFile(path);
}
