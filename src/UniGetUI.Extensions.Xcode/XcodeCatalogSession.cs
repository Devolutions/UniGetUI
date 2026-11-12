namespace UniGetUI.Extensions.Xcode;

/// <summary>
/// One Xcode Releases list shared by the page and the installer, so a check for updates followed by the
/// release details downloads it once.
/// </summary>
public sealed class XcodeCatalogSession : IXcodeReleasesCatalog, IXcodeReleasesCatalogCache
{
    private readonly IXcodeReleasesCatalog _inner;
    private readonly TimeSpan _reuseFor;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<XcodeCatalogRelease>? _latest;
    private DateTime _fetchedAtUtc;

    public XcodeCatalogSession(IXcodeReleasesCatalog inner, TimeSpan? reuseFor = null, Func<DateTime>? utcNow = null)
    {
        _inner = inner;
        _reuseFor = reuseFor ?? TimeSpan.FromMinutes(1);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<XcodeCatalogRelease>> GetAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latest is not null && _utcNow() - _fetchedAtUtc < _reuseFor)
                return _latest;
            IReadOnlyList<XcodeCatalogRelease> releases = await _inner.GetAsync(cancellationToken).ConfigureAwait(false);
            _latest = releases;
            _fetchedAtUtc = _utcNow();
            return releases;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The list downloaded in this session, else the one saved on disk, else null. Never downloads.</summary>
    public async Task<XcodeSavedReleaseCatalog?> GetSavedAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_latest is not null)
                return new XcodeSavedReleaseCatalog(_latest, _fetchedAtUtc);
        }
        finally
        {
            _lock.Release();
        }
        return _inner is IXcodeReleasesCatalogCache cache
            ? await cache.GetSavedAsync(cancellationToken).ConfigureAwait(false)
            : null;
    }
}
