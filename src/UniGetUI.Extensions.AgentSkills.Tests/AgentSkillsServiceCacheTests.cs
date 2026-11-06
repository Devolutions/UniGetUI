using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Devolutions.AgentSkills;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Extensions.AgentSkills.Tests.Infrastructure.Fakes;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed partial class AgentSkillsServiceTests
{
    private string ListingCache => Path.Combine(_testRoot, "listing-cache");
    private static SkillSourceLocator CacheSource(string address = "contoso/skills") =>
        SkillSourceLocator.Parse(address)!;
    private string CacheFile(SkillSourceLocator source) => Path.Combine(ListingCache,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.InstallSource))) + ".json");

    [Fact]
    public void PersistentListingsAvoidFetchAcrossLifetimesAndRefreshExactlyOnce()
    {
        AddSourceSetting("contoso/skills");
        string source = CacheSource().InstallSource;
        _backend.SourceSkills[source] = [new("review", "Cached review description", "review-plugin")];
        var first = new AgentSkillsService(_backend, cacheDirectory: ListingCache);
        Assert.Equal("review", Assert.Single(first.Search("", scope: SkillDiscoveryScope.Sources)).Id);
        Assert.Single(_backend.ListedSources);
        _backend.SourceSkills[source] = [new("deploy", "Fresh deploy description", null)];
        Assert.Equal("review", Assert.Single(first.Search("", scope: SkillDiscoveryScope.Sources)).Id);
        var restarted = new AgentSkillsService(_backend, cacheDirectory: ListingCache);
        var cached = Assert.Single(restarted.Search("review-plugin", scope: SkillDiscoveryScope.Sources));
        Assert.Equal(("review", "Cached review description", "contoso/skills"), (cached.Id, cached.Description, cached.SourceName));
        Assert.Single(_backend.ListedSources); // Cold=1; two warm browses=0 additional fetches.

        var fresh = Assert.Single(restarted.Search("", refreshSources: true, scope: SkillDiscoveryScope.Sources));
        Assert.Equal(("deploy", "Fresh deploy description"), (fresh.Id, fresh.Description));
        Assert.Equal(2, _backend.ListedSources.Count); // Explicit refresh=exactly one additional fetch.
        Assert.Equal("deploy", Assert.Single(new AgentSkillsService(_backend, cacheDirectory: ListingCache)
            .Search("", scope: SkillDiscoveryScope.Sources)).Id);
        Assert.Equal(2, _backend.ListedSources.Count);
        Assert.Single(Directory.GetFiles(ListingCache));
        Assert.Empty(Directory.GetFiles(ListingCache, "*.tmp"));
    }

    [Theory]
    [InlineData(0)] // malformed
    [InlineData(1)] // wrong version
    [InlineData(2)] // missing version
    [InlineData(3)] // wrong exact source
    [InlineData(4)] // null skills
    [InlineData(5)] // null entry
    [InlineData(6)] // missing name
    [InlineData(7)] // null description
    [InlineData(8)] // over size limit
    public void ListingCacheRejectsInvalidSnapshotsAndRefetches(int kind)
    {
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [new("review", "Original", null)];
        new SkillSourceListings(_backend, ListingCache).Get(source, null, default);
        string path = CacheFile(source);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (kind)
        {
            case 0: File.WriteAllText(path, "{"); break;
            case 1: json["Version"] = 2; break;
            case 2: json.Remove("Version"); break;
            case 3: json["Source"] = source.InstallSource + "/other"; break;
            case 4: json["Skills"] = null; break;
            case 5: json["Skills"] = new JsonArray((JsonNode?)null); break;
            case 6: json["Skills"]![0]!["Name"] = " "; break;
            case 7: json["Skills"]![0]!["Description"] = null; break;
            case 8:
                using (var stream = File.OpenWrite(path)) stream.SetLength(8 * 1024 * 1024 + 1);
                break;
        }
        if (kind is > 0 and < 8) File.WriteAllText(path, json.ToJsonString());
        _backend.SourceSkills[source.InstallSource] = [new("deploy", "Refetched", null)];

        var recovered = Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default));

        Assert.Equal(("deploy", "Refetched"), (recovered.Name, recovered.Description));
        Assert.Equal(2, _backend.ListedSources.Count);
        Assert.Equal("deploy", Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default)).Name);
        Assert.Equal(2, _backend.ListedSources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOrCancelledRefreshPreservesLastGoodListing(bool cancel)
    {
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [new("review", "Good", null)];
        var listings = new SkillSourceListings(_backend, ListingCache);
        listings.Get(source, null, default);
        byte[] before = File.ReadAllBytes(CacheFile(source));
        using var cancellation = new CancellationTokenSource();
        _backend.BeforeList = _ =>
        {
            if (cancel) cancellation.Cancel();
            else throw new SkillsException("fixture refresh failure");
        };
        _backend.SourceSkills[source.InstallSource] = [new("bad-refresh", "Must not persist", null)];
        if (cancel)
            Assert.ThrowsAny<OperationCanceledException>(() => listings.Get(source, null, cancellation.Token, refresh: true));
        else
            Assert.Throws<SkillsException>(() => listings.Get(source, null, default, refresh: true));

        Assert.Equal(before, File.ReadAllBytes(CacheFile(source)));
        Assert.Equal("review", Assert.Single(listings.Get(source, null, default)).Name);
        Assert.Equal("review", Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default)).Name);
        Assert.Equal(2, _backend.ListedSources.Count);
    }

    [Fact]
    public void CancelledInitialListingIsNotSavedAndPrecancelledReadNeverFetches()
    {
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [new("review", "Good", null)];
        using var cancellation = new CancellationTokenSource();
        _backend.BeforeList = _ => cancellation.Cancel();
        var listings = new SkillSourceListings(_backend, ListingCache);
        Assert.ThrowsAny<OperationCanceledException>(() => listings.Get(source, null, cancellation.Token));
        Assert.False(File.Exists(CacheFile(source)));
        Assert.ThrowsAny<OperationCanceledException>(() => listings.Get(source, null, cancellation.Token));
        Assert.Single(_backend.ListedSources);
        _backend.BeforeList = null;
        Assert.Equal("review", Assert.Single(listings.Get(source, null, default)).Name);
        Assert.Equal(2, _backend.ListedSources.Count);
    }

    [Fact]
    public void EmptyListingsPersistAndDefaultCacheIsMemoryOnly()
    {
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [];
        Assert.Empty(new SkillSourceListings(_backend, ListingCache).Get(source, null, default));
        Assert.Empty(new SkillSourceListings(_backend, ListingCache).Get(source, null, default));
        Assert.Single(_backend.ListedSources);
        var memoryOnly = new SkillSourceListings(_backend);
        Assert.Empty(memoryOnly.Get(source, null, default));
        Assert.Empty(memoryOnly.Get(source, null, default));
        Assert.Empty(new SkillSourceListings(_backend).Get(source, null, default));
        Assert.Equal(3, _backend.ListedSources.Count);
    }

    [Fact]
    public void OversizedListingRemainsUsableInMemoryButDoesNotOverwriteGoodDiskCache()
    {
        var source = CacheSource();
        var listings = new SkillSourceListings(_backend, ListingCache);
        listings.Store(source, [new("review", "Good", null)]);
        byte[] before = File.ReadAllBytes(CacheFile(source));
        listings.Store(source, [new("large", new string('x', 8 * 1024 * 1024), null)]);
        Assert.Equal("large", Assert.Single(listings.Get(source, null, default)).Name);
        Assert.Equal(before, File.ReadAllBytes(CacheFile(source)));
        Assert.Equal("review", Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default)).Name);
        Assert.Empty(_backend.ListedSources);
        Assert.Empty(Directory.GetFiles(ListingCache, "*.tmp"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void ListingAtOrJustBelowSizeLimitIsAcceptedWithoutFetching(int offset)
    {
        var source = CacheSource();
        new SkillSourceListings(_backend, ListingCache).Store(source, [new("review", "", null)]);
        string path = CacheFile(source);
        string json = File.ReadAllText(path);
        int descriptionLength = 8 * 1024 * 1024 + offset - Encoding.UTF8.GetByteCount(json);
        json = json.Replace("\"Description\":\"\"", "\"Description\":\"" + new string('x', descriptionLength) + "\"", StringComparison.Ordinal);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        Assert.Equal(8 * 1024 * 1024 + offset, new FileInfo(path).Length);

        var skill = Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default));
        Assert.Equal("review", skill.Name);
        Assert.Equal(descriptionLength, skill.Description.Length);
        Assert.Empty(_backend.ListedSources);
    }

    [Fact]
    public void UnwritableCachePathDoesNotPreventFetchOrMemoryReuse()
    {
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(ListingCache, "not a directory");
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [new("review", "Memory fallback", null)];
        var listings = new SkillSourceListings(_backend, ListingCache);
        Assert.Equal("Memory fallback", Assert.Single(listings.Get(source, null, default)).Description);
        Assert.Equal("review", Assert.Single(listings.Get(source, null, default)).Name);
        Assert.Single(_backend.ListedSources);
        Assert.Equal("not a directory", File.ReadAllText(ListingCache));
    }

    [Fact]
    public async Task CancelledAddSourceDoesNotPersistMetadataOrConfiguration()
    {
        var source = CacheSource();
        _backend.SourceSkills[source.InstallSource] = [new("review", "Cancelled", null)];
        using var cancel = new CancellationTokenSource();
        _backend.BeforeList = _ => cancel.Cancel();
        var service = new AgentSkillsService(_backend, cacheDirectory: ListingCache);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AddSourceAsync("contoso/skills", token: cancel.Token));
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        Assert.False(File.Exists(CacheFile(source)));
        _backend.BeforeList = null;
        await service.AddSourceAsync("contoso/skills");
        Assert.Equal(2, _backend.ListedSources.Count);
    }

    [Fact]
    public void NotionListingsAlwaysFetchAndNeverPersist()
    {
        var source = CacheSource("https://www.notion.so/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        Assert.Equal(SkillSourceKind.Notion, source.Kind);
        _backend.SourceSkills[source.InstallSource] = [new("notion", "First", null)];
        var listings = new SkillSourceListings(_backend, ListingCache);
        Assert.Equal("First", Assert.Single(listings.Get(source, null, default)).Description);
        _backend.SourceSkills[source.InstallSource] = [new("notion", "Second", null)];
        Assert.Equal("Second", Assert.Single(listings.Get(source, null, default)).Description);
        Assert.Equal("Second", Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(source, null, default)).Description);
        Assert.Equal(3, _backend.ListedSources.Count);
        Assert.False(Directory.Exists(ListingCache));
    }

    [Fact]
    public void ForgetRemovesOnlyExactSourceCacheAndForcesNextFetch()
    {
        var upper = CacheSource("https://skills.contoso.com/Team");
        var lower = CacheSource("https://skills.contoso.com/team");
        var listings = new SkillSourceListings(_backend, ListingCache);
        listings.Store(upper, [new("upper", "Upper", null)]);
        listings.Store(lower, [new("lower", "Lower", null)]);
        _backend.SourceSkills[upper.InstallSource] = [new("fresh", "Fresh", null)];
        listings.Forget(upper);
        Assert.False(File.Exists(CacheFile(upper)));
        Assert.True(File.Exists(CacheFile(lower)));
        Assert.Equal("lower", Assert.Single(listings.Get(lower, null, default)).Name);
        Assert.Equal("fresh", Assert.Single(listings.Get(upper, null, default)).Name);
        Assert.Equal([upper.InstallSource], _backend.ListedSources);
        Assert.Equal("lower", Assert.Single(new SkillSourceListings(_backend, ListingCache).Get(lower, null, default)).Name);
        Assert.Single(_backend.ListedSources);
    }

    [Fact]
    public void RemoveSourceDeletesPersistentListingWithoutTouchingOtherSource()
    {
        AddSourceSetting("contoso/skills");
        AddSourceSetting("fabrikam/skills");
        var source = CacheSource();
        var other = CacheSource("fabrikam/skills");
        _backend.SourceSkills[source.InstallSource] = [new("review", "", null)];
        _backend.SourceSkills[other.InstallSource] = [new("deploy", "", null)];
        var service = new AgentSkillsService(_backend, cacheDirectory: ListingCache);
        Assert.Equal(2, service.Search("").Count);
        service.RemoveSource(source);
        Assert.False(File.Exists(CacheFile(source)));
        Assert.True(File.Exists(CacheFile(other)));
        Assert.Equal("deploy", Assert.Single(new AgentSkillsService(_backend, cacheDirectory: ListingCache).Search("")).Id);
        Assert.Equal(2, _backend.ListedSources.Count);
        AddSourceSetting("contoso/skills");
        Assert.Equal(2, service.Search("").Count);
        Assert.Equal(3, _backend.ListedSources.Count);
    }

    [Fact]
    public async Task CachedUpdateChecksReuseTokensAndExplicitRefreshRechecks()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        var first = Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(first, Assert.Single(service.CheckUpdates(refresh: false)));
        Assert.Single(_backend.CheckedSkills);
        var next = _backend.UpdateCheck.Updates[0] with { LatestHash = "3333333cccc" };
        _backend.UpdateCheck = new([next], []);
        Assert.Equal("2222222", Assert.Single(service.CheckUpdates(refresh: false)).NewVersion);
        Assert.Equal("3333333", Assert.Single(service.CheckUpdates()).NewVersion);
        Assert.Equal(2, _backend.CheckedSkills.Count);
        await service.PerformAsync(first, SkillAction.Update);
        Assert.Same(next, Assert.Single(Assert.Single(_backend.AppliedUpdates)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void UpdateCacheInvalidatesWhenInstalledFingerprintChanges(int field)
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        service.CheckUpdates(refresh: false);
        _backend.Installed[0] = field switch
        {
            0 => _backend.Installed[0] with { Hash = "changed" },
            1 => _backend.Installed[0] with { SourceUrl = "https://github.com/contoso/skills/tree/main/sub" },
            2 => _backend.Installed[0] with { UpdatedAt = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero) },
            3 => _backend.Installed[0] with { Name = "renamed" },
            4 => _backend.Installed[0] with { Ref = "develop" },
            5 => _backend.Installed[0] with { SkillPath = "skills/new-path" },
            6 => _backend.Installed[0] with { PinnedRef = "v2" },
            7 => _backend.Installed[0] with { Path = "/changed/local/path" },
            8 => _backend.Installed[0] with { Agents = ["codex"] },
            9 => _backend.Installed[0] with { SourceBaseUrl = "https://github.com/contoso/skills" },
            _ => _backend.Installed[0] with { Source = "contoso/skills/tree/main" },
        };
        service.CheckUpdates(refresh: false);
        Assert.Equal(2, _backend.CheckedSkills.Count);
    }

    [Fact]
    public void UpdateCacheInvalidatesWhenConfiguredSourcesChange()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        Assert.Single(service.CheckUpdates(refresh: false));
        AddSourceSetting("fabrikam/skills");
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
        Settings.SetList(AgentSkillsService.SourcesListKey, new List<string> { "fabrikam/skills" });
        Assert.Empty(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
        AddSourceSetting("contoso/skills");
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(3, _backend.CheckedSkills.Count);
    }

    [Theory]
    [InlineData(SkillAction.Install)]
    [InlineData(SkillAction.Update)]
    [InlineData(SkillAction.Remove)]
    public async Task EveryMutationInvalidatesCachedUpdateChecks(SkillAction action)
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        var update = Assert.Single(service.CheckUpdates(refresh: false));
        await service.PerformAsync(update, action);
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
    }

    [Fact]
    public void CancelledUpdateCheckIsNotCached()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        using var cancel = new CancellationTokenSource();
        _backend.BeforeCheck = _ => cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => service.CheckUpdates(token: cancel.Token, refresh: false));
        _backend.BeforeCheck = null;
        Assert.Equal("2222222", Assert.Single(service.CheckUpdates(refresh: false)).NewVersion);
        Assert.Equal(2, _backend.CheckedSkills.Count);
    }

    [Fact]
    public void IncompleteUpdateCheckIsNotCachedAsSuccessfulEmptyResult()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        _backend.UpdateCheck = new([], [new("deploy", SkillScope.Global, "offline fixture", "contoso/skills")]);
        var service = CreateService();
        Assert.Empty(service.CheckUpdates(refresh: false));
        Assert.Empty(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
        _backend.UpdateCheck = new([], []);
        Assert.Empty(service.CheckUpdates(refresh: false));
        Assert.Empty(service.CheckUpdates(refresh: false));
        Assert.Equal(3, _backend.CheckedSkills.Count);
    }

    [Fact]
    public void UpdateCacheExpiresExactlyAtFifteenMinutesWithoutSlidingOnReads()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var clock = new CacheClock();
        var service = new AgentSkillsService(_backend, timeProvider: clock);
        Assert.Single(service.CheckUpdates(refresh: false));
        clock.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1);
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Single(_backend.CheckedSkills);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(2, _backend.CheckedSkills.Count);
        clock.Now += TimeSpan.FromMinutes(15) + TimeSpan.FromTicks(1);
        Assert.Single(service.CheckUpdates(refresh: false));
        Assert.Equal(3, _backend.CheckedSkills.Count);
    }

    private sealed class CacheClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
