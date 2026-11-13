using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Checks that a downloaded <c>.xip</c> carries Apple's own signature before it is cached or handed on. The signed
/// table of contents holds a checksum of every file, which <c>xip</c> enforces when it expands the archive.
/// </summary>
internal static partial class XcodeArchiveSignature
{
    internal const string PkgUtil = "/usr/sbin/pkgutil";
    internal const string AppleRootName = "Apple Root CA";

    /// <summary>The SHA-256 fingerprint of Apple Root CA, the anchor of every Xcode archive signature.</summary>
    internal const string AppleRootSha256 = "B0B1730ECBC7FF4505142C49F1295E6EDA6BCAED7E2C68C5BE91B5A11001F024";

    /// <summary>
    /// Apple Encrypted Archives can only be authenticated with Apple's signing key, so their Xcode is verified
    /// after expansion instead.
    /// </summary>
    public static bool CanCheck(string archive) => archive.EndsWith(".xip", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> IsAppleSignedAsync(XcodeToolRunner tools, string archive, CancellationToken cancellationToken)
    {
        XcodesResult result = await tools(PkgUtil, ["--check-signature", archive], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && IsAppleSignature(result.StandardOutput);
    }

    internal static bool IsAppleSignature(string output)
    {
        string[] lines = output.Split('\n', StringSplitOptions.TrimEntries);
        if (!lines.Contains("Status: signed Apple Software", StringComparer.Ordinal))
            return false;

        string? root = null;
        string? rootFingerprint = null;
        bool readingFingerprint = false;
        foreach (string line in lines)
        {
            Match certificate = CertificateLine().Match(line);
            if (certificate.Success)
            {
                root = certificate.Groups[1].Value;
                rootFingerprint = null;
                readingFingerprint = false;
            }
            else if (root is not null && line == "SHA256 Fingerprint:")
            {
                rootFingerprint = "";
                readingFingerprint = true;
            }
            else if (readingFingerprint && FingerprintLine().IsMatch(line))
                rootFingerprint += line.Replace(" ", "", StringComparison.Ordinal);
            else
                readingFingerprint = false;
        }
        return root == AppleRootName
               && string.Equals(rootFingerprint, AppleRootSha256, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\d+\.\s+(.+)$")]
    private static partial Regex CertificateLine();

    [GeneratedRegex(@"^[0-9A-Fa-f]{2}( [0-9A-Fa-f]{2})*$")]
    private static partial Regex FingerprintLine();
}
