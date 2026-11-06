using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class AgentPluginCatalogCacheTests : IDisposable
{
    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UniGetUI-AgentPluginCache-" + Guid.NewGuid().ToString("N"));
    private static readonly AgentPluginMarketplace _marketplace = new("team", "https://example.test/team");
    private static string CacheFile(string directory, string provider, AgentPluginMarketplace marketplace)
    {
        string identity = string.Join('\0', provider, marketplace.Name, marketplace.Source);
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    [Fact]
    public async Task StoreAndRead_PersistsOnlyUninstalledMarketplaceMetadata()
    {
        var cache = new AgentPluginCatalogCache(_directory);
        AgentPluginItem[] plugins =
        [
            new("review@team", "Review tools", "2.0", true, true)
            {
                CanInstall = false,
                CanManage = false,
                Notice = "Managed by policy.",
            },
            new("deploy@team", "Deployment tools", "1.1", false, false),
        ];

        await cache.StoreAsync("Copilot:local-profile", _marketplace, plugins);
        AgentPluginCatalogCacheSnapshot snapshot = Assert.IsType<AgentPluginCatalogCacheSnapshot>(
            await cache.ReadAsync("Copilot:local-profile", _marketplace));

        Assert.Equal(2, snapshot.Plugins.Count);
        Assert.Equal(new AgentPluginItem("review@team", "Review tools", "2.0", false, false)
        {
            CanInstall = false,
            CanManage = false,
            Notice = "Managed by policy.",
        }, snapshot.Plugins[0]);
        Assert.Equal(new AgentPluginItem("deploy@team", "Deployment tools", "1.1", false, false), snapshot.Plugins[1]);
        Assert.True(File.Exists(CacheFile(_directory, "Copilot:local-profile", _marketplace)));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CacheIdentity_IsolatedByProviderAndMarketplaceSource()
    {
        var cache = new AgentPluginCatalogCache(_directory);
        await cache.StoreAsync("Codex", _marketplace, [new("review@team", "", "1", false, false)]);

        Assert.NotNull(await cache.ReadAsync("Codex", _marketplace));
        Assert.Null(await cache.ReadAsync("Copilot", _marketplace));
        Assert.Null(await cache.ReadAsync("Codex", _marketplace with { Source = "owner/other" }));
        cache.Forget("Codex", _marketplace);
        Assert.Null(await cache.ReadAsync("Codex", _marketplace));
        Assert.True(File.Exists(CacheFile(_directory, "Codex", _marketplace with { Source = "owner/other" })) is false);
    }

    [Fact]
    public async Task ExpiredSnapshot_IsIgnoredAtExactSevenDayBoundary()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
        var cache = new AgentPluginCatalogCache(_directory, clock);
        await cache.StoreAsync("Codex", _marketplace, []);
        clock.Now = clock.Now.AddDays(7).AddTicks(-1);
        Assert.NotNull(await cache.ReadAsync("Codex", _marketplace));
        clock.Now = clock.Now.AddTicks(1);
        Assert.Null(await cache.ReadAsync("Codex", _marketplace));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InvalidSnapshot_IsIgnoredAndCanBeReplaced(int kind)
    {
        var cache = new AgentPluginCatalogCache(_directory);
        await cache.StoreAsync("Codex", _marketplace, [new("review@team", "Original", "1", false, false)]);
        string path = CacheFile(_directory, "Codex", _marketplace);
        switch (kind)
        {
            case 0:
                await File.WriteAllTextAsync(path, "{");
                break;
            case 1:
                var wrongVersion = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                wrongVersion["Version"] = 2;
                await File.WriteAllTextAsync(path, wrongVersion.ToJsonString());
                break;
            case 2:
                using (var stream = File.OpenWrite(path)) stream.SetLength(8 * 1024 * 1024 + 1);
                break;
            case 3:
                var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                json["SourceHash"] = "unexpected";
                await File.WriteAllTextAsync(path, json.ToJsonString());
                break;
        }

        Assert.Null(await cache.ReadAsync("Codex", _marketplace));
        await cache.StoreAsync("Codex", _marketplace, [new("fresh@team", "Fresh", "2", false, false)]);
        Assert.Equal("fresh@team", Assert.Single((await cache.ReadAsync("Codex", _marketplace))!.Plugins).Id);
    }

    [Fact]
    public async Task StoreRejectsPluginOutsideMarketplace()
    {
        var cache = new AgentPluginCatalogCache(_directory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.StoreAsync("Codex", _marketplace, [new("review@other", "", "1", false, false)]));

        Assert.False(Directory.Exists(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
