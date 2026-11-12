// The synthetic SRP vectors come from blacktop/ipsw internal/srp/apple_proof_test.go (MIT License),
// pinned at a8067df2a52ae7c873ab9e934e1c7b7fc8434435. See THIRD_PARTY_NOTICES.md in UniGetUI.Extensions.Xcode.
using System.Text;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class AppleSrpTests
{
    private static readonly byte[] PrivateEphemeral = Convert.FromHexString(
        "ce1195df020de59e0d65a33a4279f1183e7ae4e5d980e309f8b55adff2e61c3ef55ff16f66f43360266b95db6f8fec01d76031054306ae4a4b380598f6cfd1142c3a4249d77070058649dbd822dcaf7957586fce428cfb2ca88b94741eda8b07f46dd28a5499d8efef0b8fb8ee1ec1c5a5e407c9381741d576ba8deb4f59ec3f4539e4b4889079c2a00afeae0bfc1439840ef2379a1fb81c8ba27361ad476d6b66220e71591b2d933c0e935c138ebfd60710b91fe2fb7599eced4430b3dbb3c9730bea4ff16f200fb931b06cae08a5da8e279813775d7ed81e680b4a77946fe120377cec9f51f6bf5ba1fa64649f3b1614e4eee833fd0fc5893f24f6e0accbaf");
    private static readonly byte[] ServerPublic = Convert.FromHexString(
        "7f816a1560db947d6ff798e30909816f400f14230e9a06afac8f8b213127aa215b950e77941d01cdf246d00b1ece546bc95234b77d98b44c9187e2733afa696aabdbc2b5cc2c7a519b72bf7a164c58ebf892ab0c2df6468213705cc2f0da85610cd20d37dbaa799d1d2f6f04adbab0b9e958b083f38e06512cdefadd20863f98239fd09dd1c48679b74cec2120cd5e448b002c728c05e9b10f2c19f298fbdd575ba2c833c5d65e649e4b4fa4d426223f3300650f874e32c4451d9346ce6469e29d574e1d3c5ed212edee33e2478e5a62cdecc5b5cb365479c4eb99e9d342aa38f86ae46d947e2215ce53b1ae840af949b5f686e69ea2f6b7eaf3725619d4303e");
    private static readonly byte[] Salt = Convert.FromHexString("2bc990059473448e6ebd442aa16bfaa0567ac03dd094876bdb1fd834abe42c1e");
    private const string Identity = "synthetic@example.com";
    private static readonly byte[] Password = Encoding.UTF8.GetBytes("synthetic-pass-Δ1");

    [Theory]
    [InlineData("s2k", "f8ffdfb9111079d5c130e22fb0d675f90e53182301d46fe116e29df35daa584f",
        "446969b3ce382010eaadbaada5caa7ee5f50c52443d4ff10d2c04841aa7af57a")]
    [InlineData("s2k_fo", "fe00a59cb9949584f74d2aecddfa679162bcfea056a3844def51d6076ee3dd0f",
        "84436cc428d5076cf048fec1ad162834a4e93b669a965bbf8f322df6deb5b959")]
    public void ComputeProofs_MatchesIndependentVectors(string protocol, string m1, string m2)
    {
        using var client = new AppleSrpClient(PrivateEphemeral);
        var proofs = client.ComputeProofs(protocol, Identity, Password, Salt, 20309, ServerPublic);
        Assert.Equal(m1, Convert.ToHexStringLower(proofs.M1));
        Assert.Equal(m2, Convert.ToHexStringLower(proofs.M2));
    }

    [Fact]
    public void ComputeProofs_RejectsInvalidChallenges()
    {
        byte[] n = Convert.FromHexString("AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73");
        Assert.Throws<ArgumentException>(() => new AppleSrpClient(PrivateEphemeral).ComputeProofs("s2k", Identity, Password, Salt, 20309, [0]));
        Assert.Throws<ArgumentException>(() => new AppleSrpClient(PrivateEphemeral).ComputeProofs("s2k", Identity, Password, Salt, 20309, n));
        Assert.Throws<ArgumentException>(() => new AppleSrpClient(PrivateEphemeral).ComputeProofs("unknown", Identity, Password, Salt, 20309, ServerPublic));
        Assert.Throws<ArgumentException>(() => new AppleSrpClient(PrivateEphemeral).ComputeProofs("s2k", Identity, Password, Salt, 0, ServerPublic));
        Assert.Throws<ArgumentException>(() => new AppleSrpClient(PrivateEphemeral).ComputeProofs("s2k", Identity, Password, [], 20309, ServerPublic));
    }

    [Fact]
    public void Client_IsSingleUseAndRandom()
    {
        using var client = new AppleSrpClient(PrivateEphemeral);
        client.ComputeProofs("s2k", Identity, Password, Salt, 1, ServerPublic);
        Assert.Throws<InvalidOperationException>(() => client.ComputeProofs("s2k", Identity, Password, Salt, 1, ServerPublic));
        using var first = new AppleSrpClient();
        using var second = new AppleSrpClient();
        Assert.NotEqual(first.PublicEphemeral, second.PublicEphemeral);
    }
}

public sealed class AppleHashcashTests
{
    // Vectors from xcodes Tests/AppleAPITests/AppleAPITests.swift (MIT License).
    [Theory]
    [InlineData(11, "4d74fb15eb23f465f1f6fcbf534e5877", "20230223170600", "1:11:20230223170600:4d74fb15eb23f465f1f6fcbf534e5877::6373")]
    [InlineData(10, "bb63edf88d2f9c39f23eb4d6f0281158", "20230224001754", "1:10:20230224001754:bb63edf88d2f9c39f23eb4d6f0281158::866")]
    public void Make_MatchesReference(int bits, string challenge, string date, string expected)
        => Assert.Equal(expected, AppleHashcash.Make(bits, challenge, date));

    [Fact]
    public void Make_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AppleHashcash.Make(33, "abc", "1"));
        Assert.Throws<ArgumentException>(() => AppleHashcash.Make(1, "a\nb", "1"));
    }
}
