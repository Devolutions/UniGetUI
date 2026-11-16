using System.Text;
using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoMinisignTests
{
    private const string BinstallPublicKey = "RWQVYzYOmS4CWLZCzyO32DYwvS5lkcOZfoWGvrk/82/3tGcjJTcJzZQK";
    private const string BinstallSignature = """
        untrusted comment: signature from rsign secret key
        RUQVYzYOmS4CWAtu4Qkk+pNL3xZe23uehyG/oHAA7XBXKQu5vjjcIB1vLw2WbmYpTKY8eHeqp+mWQhiM32k0s3JhuJqM30t1ZwY=
        trusted comment: gh=cargo-bins/cargo-binstall git=5885714d0a40a14b2535c6ffc051fd8e49aa9665 ts=2026-11-11T09:50:25.024Z run=42996742987
        qhBQgMhC2ShkVJ6N6bE2A0qt48aZY7HIl/MzkA/9i3uPqh6PL8MANRZTY8wsjZrb46XYv9eDBgV6FVPsZzNuDw==

        """;

    [Theory]
    [InlineData("", "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce")]
    [InlineData("abc", "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923")]
    public void Blake2b512MatchesReferenceVectors(string input, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(CargoMinisign.Blake2b512(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());

    [Theory]
    [InlineData(127, "b6292669ccd38d5f01caae96ba272c76a879a45743afa0725d83b9ebb26665b731f1848c52f11972b6644f554c064fa90780dbbbf3a89d4fc31f67df3e5857ef")]
    [InlineData(128, "2319e3789c47e2daa5fe807f61bec2a1a6537fa03f19ff32e87eecbfd64b7e0e8ccff439ac333b040f19b0c4ddd11a61e24ac1fe0f10a039806c5dcc0da3d115")]
    [InlineData(129, "f59711d44a031d5f97a9413c065d1e614c417ede998590325f49bad2fd444d3e4418be19aec4e11449ac1a57207898bc57d76a1bcf3566292c20c683a5c4648f")]
    [InlineData(256, "1ecc896f34d3f9cac484c73f75f6a5fb58ee6784be41b35f46067b9c65c63a6794d3d744112c653f73dd7deb6666204c5a9bfa5b46081fc10fdbe7884fa5cbf8")]
    [InlineData(300, "d9cf5983dc6b34c0fa1f0226926855ad3eccd2bcdcd8f8053b9a80664d33b5afcc32fd21c70ea14f4ef50ca97c3203c4d1803159f0e01bb6cb1d1c83db52b63c")]
    public void Blake2b512HandlesExactAndPartialBlockBoundaries(int length, string expected)
    {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        Assert.Equal(expected, Convert.ToHexString(CargoMinisign.Blake2b512(data)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    public void Ed25519MatchesRfc8032Vectors(string seed, string publicKey, string message, string signature)
    {
        byte[] key = Convert.FromHexString(publicKey);
        byte[] text = Convert.FromHexString(message);
        byte[] expected = Convert.FromHexString(signature);
        Assert.Equal(key, CargoTestData.Ed25519PublicKey(Convert.FromHexString(seed)));
        Assert.Equal(expected, CargoTestData.Ed25519Sign(Convert.FromHexString(seed), text));
        Assert.True(CargoMinisign.Ed25519.Verify(key, text, expected));
        Assert.False(CargoMinisign.Ed25519.Verify(key, [.. text, 0], expected));
        byte[] tampered = (byte[])expected.Clone();
        tampered[10] ^= 1;
        Assert.False(CargoMinisign.Ed25519.Verify(key, text, tampered));
    }

    [Fact]
    public void Ed25519RejectsNonCanonicalScalar()
    {
        byte[] key = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] signature = Convert.FromHexString(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var s = new System.Numerics.BigInteger(signature.AsSpan(32), isUnsigned: true) + CargoMinisign.Ed25519.L;
        s.TryWriteBytes(signature.AsSpan(32), out _, isUnsigned: true);
        Assert.False(CargoMinisign.Ed25519.Verify(key, [], signature));
    }

    [Theory]
    [InlineData("0100000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000080")]
    [InlineData("26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc05")]
    [InlineData("c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a")]
    public void Ed25519RejectsSmallOrderPublicKeysAndCommitments(string smallOrder)
    {
        byte[] point = Convert.FromHexString(smallOrder);
        byte[] forged = [.. point, .. new byte[32]];
        byte[] honest = CargoTestData.Ed25519Sign(CargoTestData.SigningSeed, [1, 2, 3]);
        Assert.False(CargoMinisign.Ed25519.Verify(point, [1, 2, 3], forged));
        Assert.False(CargoMinisign.Ed25519.Verify(point, "any message"u8, forged));
        Assert.False(CargoMinisign.Ed25519.Verify(CargoTestData.Ed25519PublicKey(CargoTestData.SigningSeed),
            [1, 2, 3], [.. point, .. honest.AsSpan(32)]));
    }

    [Fact]
    public void IdentityPublicKeyCannotAuthorizeAnyContent()
    {
        string identityKey = Convert.ToBase64String(
            [(byte)'E', (byte)'d', .. CargoTestData.SigningKeyId, 1, .. new byte[31]]);
        byte[] forgedSignature = [1, .. new byte[63]];
        string file = "untrusted comment: forged\n"
            + Convert.ToBase64String([(byte)'E', (byte)'D', .. CargoTestData.SigningKeyId, .. forgedSignature])
            + "\ntrusted comment: anything\n" + Convert.ToBase64String(forgedSignature) + "\n";
        var signature = CargoMinisign.Parse(identityKey, file);
        Assert.Contains("publisher signature", Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Verify(signature, Encoding.UTF8.GetBytes("replacement binary"))).Message);
    }

    [Theory]
    [InlineData("untrusted comment: minisign public key 0102030405060708\n{0}")]
    [InlineData("untrusted comment: minisign public key 0102030405060708\r\n{0}\r\n")]
    [InlineData("\n  {0}  \n")]
    public void PublicKeyMayIncludeTheMinisignCommentLine(string format)
    {
        byte[] content = [4, 5, 6];
        var signature = CargoMinisign.Parse(string.Format(format, CargoTestData.MinisignPublicKey()),
            CargoTestData.MinisignSignature(content));
        CargoMinisign.Verify(signature, content);
    }

    [Theory]
    [InlineData("untrusted comment: only a comment")]
    [InlineData("untrusted comment: x\n{0}\n{0}")]
    [InlineData("{0}\nuntrusted comment: x")]
    [InlineData("trusted comment: x\n{0}")]
    public void PublicKeyWithAnythingButOneOptionalCommentIsRejected(string format) =>
        Assert.Contains("public key", Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Parse(string.Format(format, CargoTestData.MinisignPublicKey()),
                CargoTestData.MinisignSignature([1]))).Message);

    [Fact]
    public void RealCargoBinstallSignatureParsesAndItsTrustedCommentVerifies()
    {
        var signature = CargoMinisign.Parse(BinstallPublicKey, BinstallSignature);
        Assert.True(signature.Prehashed);
        Assert.StartsWith("gh=cargo-bins/cargo-binstall ", signature.TrustedComment, StringComparison.Ordinal);
        Assert.True(CargoMinisign.Ed25519.Verify(signature.PublicKey,
            [.. signature.Signature, .. Encoding.UTF8.GetBytes(signature.TrustedComment)], signature.GlobalSignature));
        Assert.Throws<InvalidDataException>(() => CargoMinisign.Verify(signature, [1, 2, 3]));
        var forged = signature with { TrustedComment = signature.TrustedComment + " " };
        Assert.False(CargoMinisign.Ed25519.Verify(forged.PublicKey,
            [.. forged.Signature, .. Encoding.UTF8.GetBytes(forged.TrustedComment)], forged.GlobalSignature));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignedContentVerifiesAndAnyChangeIsRejected(bool prehashed)
    {
        byte[] content = Encoding.UTF8.GetBytes("publisher archive");
        var signature = CargoMinisign.Parse(CargoTestData.MinisignPublicKey(),
            CargoTestData.MinisignSignature(content, "timestamp:1", prehashed: prehashed));
        Assert.Equal(prehashed, signature.Prehashed);
        CargoMinisign.Verify(signature, content);
        Assert.Throws<InvalidDataException>(() => CargoMinisign.Verify(signature, [.. content, 0]));
        Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Verify(signature with { TrustedComment = "timestamp:2" }, content));
    }

    [Theory]
    [InlineData("untrusted comment: x\nnot base64\ntrusted comment: x\nAAAA\n", "malformed")]
    [InlineData("untrusted comment: x\n", "malformed")]
    [InlineData("comment: x\nAAAA\ntrusted comment: x\nAAAA\n", "malformed")]
    public void MalformedSignatureFilesAreRejected(string file, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Parse(CargoTestData.MinisignPublicKey(), file)).Message);

    [Fact]
    public void SignatureFromAnotherKeyIdIsRejected() =>
        Assert.Contains("different key", Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Parse(CargoTestData.MinisignPublicKey(),
                CargoTestData.MinisignSignature([1], keyId: [9, 9, 9, 9, 9, 9, 9, 9]))).Message);

    [Fact]
    public void MalformedPublicKeyIsRejected() =>
        Assert.Contains("public key", Assert.Throws<InvalidDataException>(() =>
            CargoMinisign.Parse("RWQ=", CargoTestData.MinisignSignature([1]))).Message);
}
