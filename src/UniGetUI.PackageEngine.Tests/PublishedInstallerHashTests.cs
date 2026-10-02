using System.Security.Cryptography;
using UniGetUI.PackageEngine.Operations;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PublishedInstallerHashTests : IDisposable
{
    private readonly string _filePath;
    private readonly byte[] _payload;

    public PublishedInstallerHashTests()
    {
        _payload = new byte[8192];
        new Random(97).NextBytes(_payload);
        _filePath = Path.Join(Path.GetTempPath(), $"unigetui-hash-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(_filePath, _payload);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    private async Task<bool> Matches(string publishedHash)
    {
        Assert.True(
            PublishedInstallerHash.TryParse(publishedHash, out PublishedInstallerHash parsed),
            $"could not parse {publishedHash}"
        );
        return await parsed.MatchesAsync(_filePath, CancellationToken.None);
    }

    [Fact]
    public async Task WinGetPublishesUppercaseHexSha256()
        => Assert.True(await Matches(Convert.ToHexString(SHA256.HashData(_payload))));

    [Fact]
    public async Task PipAndCargoPublishLowercaseHexSha256()
        => Assert.True(
            await Matches(Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant())
        );

    [Fact]
    public async Task NpmAndBunPublishSubresourceIntegrity()
        => Assert.True(
            await Matches("sha512-" + Convert.ToBase64String(SHA512.HashData(_payload)))
        );

    [Fact]
    public async Task TheNuGetFamilyPublishesBase64Sha512()
        => Assert.True(await Matches(Convert.ToBase64String(SHA512.HashData(_payload))));

    [Fact]
    public async Task ScoopPublishesPrefixedHashes()
    {
        Assert.True(
            await Matches("sha256:" + Convert.ToHexString(SHA256.HashData(_payload)))
        );
        Assert.True(await Matches("md5:" + Convert.ToHexString(MD5.HashData(_payload))));
        Assert.True(await Matches(Convert.ToHexString(SHA1.HashData(_payload))));
    }

    [Fact]
    public async Task IntegrityListsAreAcceptedThroughTheirFirstUsableEntry()
        => Assert.True(
            await Matches(
                "sha512-" + Convert.ToBase64String(SHA512.HashData(_payload))
                    + " sha256-" + Convert.ToBase64String(SHA256.HashData(_payload))
            )
        );

    [Fact]
    public async Task ADigestOfTheWrongContentDoesNotMatch()
        => Assert.False(await Matches(Convert.ToHexString(SHA256.HashData([1, 2, 3, 4]))));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("0f6ec2")]
    [InlineData("whirlpool:0f6ec2eda1f8c5dc4c267ee761c0dad8")]
    public void UnusableHashesAreRejected(string? publishedHash)
        => Assert.False(PublishedInstallerHash.TryParse(publishedHash, out _));

    [Fact]
    public void AnAlgorithmThatDisagreesWithItsDigestLengthIsRejected()
        => Assert.False(
            PublishedInstallerHash.TryParse(
                "sha256-" + Convert.ToBase64String(SHA512.HashData(_payload)),
                out _
            )
        );
}
