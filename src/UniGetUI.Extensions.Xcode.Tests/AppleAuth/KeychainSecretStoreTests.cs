using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Tests.AppleAuth;

public sealed class KeychainSecretStoreTests
{
    /// <summary>Touches the login Keychain, so it runs only when UNIGETUI_KEYCHAIN_TESTS=1.</summary>
    [Fact]
    public void RoundTripsUnderItsOwnService()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("UNIGETUI_KEYCHAIN_TESTS") != "1")
            return;
        var store = new KeychainSecretStore($"com.devolutions.unigetui.tests.{Guid.NewGuid():N}");
        var other = new KeychainSecretStore($"com.devolutions.unigetui.tests.{Guid.NewGuid():N}");
        try
        {
            Assert.Null(store.Read("item"));
            Assert.False(store.ReadMetadata("item").Exists);
            store.Write("item", "first"u8, "meta-1"u8);
            SecretMetadata firstMeta = store.ReadMetadata("item");
            Assert.True(firstMeta.Exists);
            Assert.Equal("meta-1"u8.ToArray(), firstMeta.Metadata);
            Assert.NotNull(firstMeta.ModificationStamp);

            store.Write("item", "second"u8, "meta-2"u8);
            Assert.Equal("second"u8.ToArray(), store.Read("item"));
            Assert.Equal("meta-2"u8.ToArray(), store.ReadMetadata("item").Metadata);
            Assert.Null(other.Read("item"));
            store.Delete("item");
            store.Delete("item");
            Assert.Null(store.Read("item"));
            Assert.False(store.ReadMetadata("item").Exists);
        }
        finally
        {
            store.Delete("item");
        }
    }
}
