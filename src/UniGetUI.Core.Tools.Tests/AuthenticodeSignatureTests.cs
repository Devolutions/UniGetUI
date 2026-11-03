namespace UniGetUI.Core.Tools.Tests;

public class AuthenticodeSignatureTests
{
    [WindowsFact]
    public void ASignedRuntimeAssemblyIsReportedAsValidWithoutOnlineRevocation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        SignatureCheck check = AuthenticodeSignature.VerifyFile(
            typeof(object).Assembly.Location,
            checkRevocationOnline: false
        );

        Assert.Equal(SignatureKind.Valid, check.Kind);
        Assert.False(string.IsNullOrWhiteSpace(check.Signer));
    }

    [WindowsFact]
    public void AFileWithoutASignatureIsNotReportedAsValid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string path = Path.Join(Path.GetTempPath(), $"unigetui-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, new byte[4096]);
        try
        {
            SignatureCheck check = AuthenticodeSignature.VerifyFile(path, checkRevocationOnline: false);

            Assert.NotEqual(SignatureKind.Valid, check.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, SignatureKind.Valid)]
    [InlineData(unchecked((int)0x800B0100), SignatureKind.NotSigned)]
    [InlineData(unchecked((int)0x800B0003), SignatureKind.UnsignedFileType)]
    [InlineData(unchecked((int)0x800B0101), SignatureKind.Expired)]
    [InlineData(unchecked((int)0x800B010A), SignatureKind.Unverifiable)]
    public void StatusCodesMapToOneKindUsedByBothDescriptions(int status, SignatureKind kind)
    {
        var check = new SignatureCheck(status, "Example Corp");

        Assert.Equal(kind, check.Kind);
        Assert.False(string.IsNullOrWhiteSpace(check.Describe()));
        Assert.Equal(kind is SignatureKind.UnsignedFileType, check.Summary() is null);
    }
}
