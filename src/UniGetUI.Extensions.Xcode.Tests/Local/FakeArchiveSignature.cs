using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode.Tests.Local;

/// <summary>What <c>pkgutil --check-signature</c> prints for archives, recorded from real Xcode downloads.</summary>
internal static class FakeArchiveSignature
{
    public const string Apple = """
        Package "Xcode_27.3_beta.xip":
           Status: signed Apple Software
           Certificate Chain:
            1. Software Update
               Expires: 2029-04-14 21:28:23 +0000
               SHA256 Fingerprint:
                   E0 74 D2 04 AC 24 98 E9 DC 90 4A 7B C7 CE D8 46 41 19 B7 9D 05 66
                   80 28 92 05 83 B1 E8 96 EB B4
               ------------------------------------------------------------------------
            2. Apple Software Update Certification Authority
               Expires: 2031-10-15 00:00:00 +0000
               SHA256 Fingerprint:
                   12 99 E9 BF E7 76 A2 9F F4 52 F8 C4 F5 E5 5F 3B 4D FD 29 34 34 9D
                   D1 85 0B 82 74 F3 5C 71 74 5C
               ------------------------------------------------------------------------
            3. Apple Root CA
               Expires: 2035-02-09 21:40:36 +0000
               SHA256 Fingerprint:
                   B0 B1 73 0E CB C7 FF 45 05 14 2C 49 F1 29 5E 6E DA 6B CA ED 7E 2C
                   68 C5 BE 91 B5 A1 10 01 F0 24

        """;

    /// <summary>Answers every signature check as a genuine Apple archive.</summary>
    public static readonly XcodeToolRunner Signed = (_, _, _) => Task.FromResult(new XcodesResult(0, Apple, ""));
}
