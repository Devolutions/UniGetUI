namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeCatalogSessionTests
{
    internal sealed class FakeCatalog(params XcodeCatalogRelease[] releases) : IXcodeReleasesCatalog, IXcodeReleasesCatalogCache
    {
        public int Downloads { get; private set; }

        public XcodeSavedReleaseCatalog? Saved { get; set; }

        public Task<IReadOnlyList<XcodeCatalogRelease>> GetAsync(CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult<IReadOnlyList<XcodeCatalogRelease>>(releases);
        }

        public Task<XcodeSavedReleaseCatalog?> GetSavedAsync(CancellationToken cancellationToken) => Task.FromResult(Saved);
    }

    internal static XcodeCatalogRelease Release(string version, string build,
        XcodeArchitecture architecture = XcodeArchitecture.Universal, bool downloadable = true)
        => new(version, build, architecture, null, null, null, [], [], null)
        {
            Download = downloadable
                ? new Uri($"https://download.developer.apple.com/Developer_Tools/Xcode_{version.Replace(' ', '_')}/Xcode_{version.Replace(' ', '_')}.xip")
                : null,
        };

    [Fact]
    public async Task ReusesARecentDownloadAndDownloadsAgainOnceItIsOld()
    {
        var inner = new FakeCatalog(Release("26.4", "17E192"));
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new XcodeCatalogSession(inner, TimeSpan.FromMinutes(1), () => now);

        await session.GetAsync(CancellationToken.None);
        now = now.AddSeconds(30);
        await session.GetAsync(CancellationToken.None);
        Assert.Equal(1, inner.Downloads);

        now = now.AddMinutes(1);
        await session.GetAsync(CancellationToken.None);
        Assert.Equal(2, inner.Downloads);
    }

    [Fact]
    public async Task SavedListIsTheSessionsDownloadElseTheOneOnDiskAndNeverDownloads()
    {
        var inner = new FakeCatalog(Release("26.4", "17E192"));
        var session = new XcodeCatalogSession(inner);

        Assert.Null(await session.GetSavedAsync(CancellationToken.None));
        inner.Saved = new XcodeSavedReleaseCatalog([Release("26.3", "17C52")], DateTime.UtcNow);
        Assert.Equal("26.3", Assert.Single((await session.GetSavedAsync(CancellationToken.None))!.Releases).Version);
        Assert.Equal(0, inner.Downloads);

        await session.GetAsync(CancellationToken.None);
        Assert.Equal("26.4", Assert.Single((await session.GetSavedAsync(CancellationToken.None))!.Releases).Version);
    }
}
