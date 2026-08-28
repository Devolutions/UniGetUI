using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;
#if WINDOWS
using UniGetUI.PackageEngine.Managers.WingetManager;
#endif

// ReSharper disable once CheckNamespace
namespace UniGetUI.PackageEngine.PackageClasses;

/// <summary>
/// Avalonia-compatible package wrapper (replaces the WinUI PackageWrapper that uses Microsoft.UI.Xaml).
/// </summary>
public sealed class PackageWrapper : INotifyPropertyChanged, IDisposable, IPackageIconHost
{
    private static readonly HttpClient _iconHttpClient = new(CoreTools.GenericHttpClientParameters)
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly SemaphoreSlim _iconLoadSemaphore = new(8, 8);
    private static readonly object _inflightIconLoadsLock = new();
    private static readonly Dictionary<long, Task<Bitmap?>> _inflightIconLoads = new();

    // Cap decoded icon size; the list shows icons at ≤64px (128 covers 2x DPI).
    private const int MaxIconSide = 128;
    private const int MaxIconDownloadBytes = 2 * 1024 * 1024;

    // Bounded LRU by package hash. Evicted entries aren't disposed: a visible row may still
    // reference the bitmap, so dropping it here only makes it GC-eligible.
    private const int MaxIconCacheEntries = 512;
    private static readonly object _iconCacheLock = new();
    private static readonly Dictionary<long, LinkedListNode<(long Hash, Bitmap Bitmap)>> _iconCache = new();
    private static readonly LinkedList<(long Hash, Bitmap Bitmap)> _iconCacheOrder = new();
    private static readonly Dictionary<long, long> _failedIcons = new();
    private const int MaxFailedIconEntries = 512;
    private static readonly TimeSpan IconRetryInterval = TimeSpan.FromMinutes(5);

    private const string UnknownInstallerHost = "\u2014";
    private const int MaxInstallerHostCacheEntries = 1024;
    private const int MaxUnresolvedInstallerHostEntries = 512;
    private static readonly TimeSpan InstallerHostRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim _installerHostSemaphore = new(4, 4);
    private static readonly object _installerHostCacheLock = new();
    private static readonly Dictionary<long, (string Host, string Urls)> _installerHostCache = new();
    private static readonly Dictionary<long, long> _unresolvedInstallerHosts = new();

    private static Bitmap? GetCachedIcon(long hash)
    {
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(hash, out var node))
            {
                _iconCacheOrder.Remove(node);
                _iconCacheOrder.AddFirst(node);
                return node.Value.Bitmap;
            }
        }
        return null;
    }

    private static bool HasRecentIconFailure(long hash)
    {
        lock (_iconCacheLock)
        {
            if (!_failedIcons.TryGetValue(hash, out long failedAt))
                return false;

            if (Environment.TickCount64 - failedAt < (long)IconRetryInterval.TotalMilliseconds)
                return true;

            _failedIcons.Remove(hash);
            return false;
        }
    }

    private static void MarkIconFailed(long hash)
    {
        lock (_iconCacheLock)
        {
            _failedIcons[hash] = Environment.TickCount64;
            if (_failedIcons.Count > MaxFailedIconEntries)
                TrimFailedIcons();
        }
    }

    private static void TrimFailedIcons()
    {
        long now = Environment.TickCount64;
        long retryMs = (long)IconRetryInterval.TotalMilliseconds;
        foreach (var expired in _failedIcons.Where(e => now - e.Value >= retryMs).ToArray())
            _failedIcons.Remove(expired.Key);

        int excess = _failedIcons.Count - MaxFailedIconEntries;
        if (excess <= 0)
            return;

        foreach (var oldest in _failedIcons.OrderBy(e => e.Value).Take(excess).ToArray())
            _failedIcons.Remove(oldest.Key);
    }

    private static void CacheIcon(long hash, Bitmap bitmap)
    {
        lock (_iconCacheLock)
        {
            _failedIcons.Remove(hash);

            if (_iconCache.TryGetValue(hash, out var existing))
            {
                existing.Value = (hash, bitmap);
                _iconCacheOrder.Remove(existing);
                _iconCacheOrder.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<(long, Bitmap)>((hash, bitmap));
            _iconCache[hash] = node;
            _iconCacheOrder.AddFirst(node);

            while (_iconCache.Count > MaxIconCacheEntries && _iconCacheOrder.Last is { } last)
            {
                _iconCacheOrder.RemoveLast();
                _iconCache.Remove(last.Value.Hash);
            }
        }
    }

    public static void ClearIconCache()
    {
        lock (_iconCacheLock)
        {
            _iconCache.Clear();
            _iconCacheOrder.Clear();
            _failedIcons.Clear();
        }
    }

    public IPackage Package { get; }
    public PackageWrapper Self => this;
    public int Index { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly PackagesPageViewModel _page;

    private Bitmap? _iconBitmap;
    public Bitmap? IconBitmap
    {
        get => _iconBitmap;
        private set
        {
            _iconBitmap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBitmap)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCustomIcon)));
        }
    }
    public bool HasCustomIcon => _iconBitmap is not null;

    public bool IsChecked
    {
        get => Package.IsChecked;
        set
        {
            Package.IsChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            _page.UpdatePackageCount();
        }
    }

    public string VersionComboString { get; }
    public string ListedNameTooltip { get; private set; } = "";
    public float ListedOpacity { get; private set; } = 1.0f;
    public string TagBackdropIconPath { get; private set; } = "";
    public string TagSymbolIconPath { get; private set; } = "";
    public bool TagIconVisible { get; private set; }

    public bool InstallerHostChanged { get; private set; }
    public string InstallerHostChangeTooltip { get; private set; } = "";

    public string InstallerHostText { get; private set; } = "";
    public string? InstallerHostTooltip { get; private set; }

    private CancellationTokenSource? _installerHostCheckCts;
    // Cancels this row's queued/in-flight icon load on disposal so it stops rooting the wrapper.
    private readonly CancellationTokenSource _lifetimeCts = new();

    public string SourceIconPath => IconTypeToSvgPath(Package.Source.IconId);

    private static string IconTypeToSvgPath(IconType icon)
    {
        string name = icon switch
        {
            IconType.Chocolatey => "choco",
            IconType.MsStore => "ms_store",
            IconType.LocalPc => "local_pc",
            IconType.SaveAs => "save_as",
            IconType.SysTray => "sys_tray",
            IconType.ClipboardList => "clipboard_list",
            IconType.OpenFolder => "open_folder",
            IconType.AddTo => "add_to",
            _ => icon.ToString().ToLowerInvariant(),
        };
        return $"avares://UniGetUI/Assets/Symbols/{name}.svg";
    }

    public PackageWrapper(IPackage package, PackagesPageViewModel page)
    {
        Package = package;
        _page = page;
        VersionComboString = package.VersionString;

        Package.PropertyChanged += Package_PropertyChanged;
        UpdateDisplayState();

        // Icons are normally preloaded after the result set completes. The visible-row hook remains
        // as a fallback while results are still arriving.
        MaybeStartInstallerHostCheck();
    }

    private readonly object _iconLoadLock = new();
    private Task? _iconLoadTask;

    /// <summary>Loads this row's icon at most once; also called when the row becomes visible.</summary>
    public void EnsureIconLoaded()
    {
        _ = EnsureIconLoadedAsync();
    }

    public static Task<Bitmap?> LoadSharedIconAsync(IPackage package)
    {
        if (Settings.Get(Settings.K.DisableIconsOnPackageLists))
            return Task.FromResult<Bitmap?>(null);

        return GetSharedIconLoad(package.GetHash(), package);
    }

    /// <summary>Loads this row's icon at most once and returns the shared load operation.</summary>
    public Task EnsureIconLoadedAsync()
    {
        if (Settings.Get(Settings.K.DisableIconsOnPackageLists)) return Task.CompletedTask;
        lock (_iconLoadLock)
            return _iconLoadTask ??= LoadIconAsync();
    }

    private int _installerHostLoadStarted;

    public void EnsureInstallerHostLoaded()
    {
        if (!_page.InstallerHostColumnVisible) return;
        if (Interlocked.Exchange(ref _installerHostLoadStarted, 1) != 0) return;
        _ = LoadInstallerHostAsync();
    }

    private string InstallerHostVersion =>
        Package.IsUpgradable ? Package.NewVersionString : Package.VersionString;

    private async Task LoadInstallerHostAsync()
    {
        CancellationToken token = _lifetimeCts.Token;
        long hash = CoreTools.HashStringAsLong(
            $"{Package.GetVersionedHash()}|{InstallerHostVersion}"
        );
        try
        {
            if (TryGetCachedInstallerHost(hash, out var cached))
            {
                ApplyInstallerHost(cached.Host, cached.Urls);
                return;
            }

            if (HasRecentInstallerHostFailure(hash))
            {
                ApplyInstallerHost("", "");
                return;
            }

            await _installerHostSemaphore.WaitAsync(token).ConfigureAwait(false);
            (string Host, string Urls) resolved;
            try
            {
                if (!TryGetCachedInstallerHost(hash, out resolved))
                {
                    IReadOnlyList<string>? urls = await ResolveInstallerUrlsAsync(token)
                        .ConfigureAwait(false);
                    resolved = (
                        InstallerHostDisplay.FromUrls(urls),
                        InstallerHostDisplay.JoinUrls(urls)
                    );
                    if (resolved.Host.Length > 0)
                        CacheInstallerHost(hash, resolved.Host, resolved.Urls);
                    else
                        MarkInstallerHostUnresolved(hash);
                }
            }
            finally
            {
                _installerHostSemaphore.Release();
            }

            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                    ApplyInstallerHost(resolved.Host, resolved.Urls);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn($"Could not resolve the installer host for {Package.Id}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _installerHostLoadStarted, 0);
        }
    }

    private async Task<IReadOnlyList<string>?> ResolveInstallerUrlsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if WINDOWS
        if (Package.Manager is WinGet)
        {
            string version = InstallerHostVersion;
            return await Task.Run(() => WinGet.TryGetInstallerUrls(Package, version), token)
                .ConfigureAwait(false);
        }
#endif
        if (!Package.Details.IsPopulated)
            await Package.Details.Load().ConfigureAwait(false);

        return Package.Details.InstallerUrl is { } url ? [url.ToString()] : null;
    }

    private void ApplyInstallerHost(string host, string urls)
    {
        InstallerHostText = host.Length > 0 ? host : UnknownInstallerHost;
        InstallerHostTooltip = urls.Length > 0 ? urls : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHostTooltip)));
    }

    private static bool TryGetCachedInstallerHost(long hash, out (string Host, string Urls) entry)
    {
        lock (_installerHostCacheLock)
            return _installerHostCache.TryGetValue(hash, out entry);
    }

    private static bool HasRecentInstallerHostFailure(long hash)
    {
        lock (_installerHostCacheLock)
        {
            if (!_unresolvedInstallerHosts.TryGetValue(hash, out long failedAt))
                return false;

            if (Environment.TickCount64 - failedAt < (long)InstallerHostRetryInterval.TotalMilliseconds)
                return true;

            _unresolvedInstallerHosts.Remove(hash);
            return false;
        }
    }

    private static void MarkInstallerHostUnresolved(long hash)
    {
        lock (_installerHostCacheLock)
        {
            long now = Environment.TickCount64;
            _unresolvedInstallerHosts[hash] = now;
            if (_unresolvedInstallerHosts.Count <= MaxUnresolvedInstallerHostEntries)
                return;

            long retryMs = (long)InstallerHostRetryInterval.TotalMilliseconds;
            foreach (var expired in _unresolvedInstallerHosts.Where(e => now - e.Value >= retryMs).ToArray())
                _unresolvedInstallerHosts.Remove(expired.Key);

            int excess = _unresolvedInstallerHosts.Count - MaxUnresolvedInstallerHostEntries;
            if (excess <= 0)
                return;

            foreach (var oldest in _unresolvedInstallerHosts.OrderBy(e => e.Value).Take(excess).ToArray())
                _unresolvedInstallerHosts.Remove(oldest.Key);
        }
    }

    private static void CacheInstallerHost(long hash, string host, string urls)
    {
        lock (_installerHostCacheLock)
        {
            if (_installerHostCache.Count >= MaxInstallerHostCacheEntries
                && !_installerHostCache.ContainsKey(hash))
            {
                _installerHostCache.Clear();
            }

            _installerHostCache[hash] = (host, urls);
        }
    }

    /// <summary>
    /// For upgradable WinGet packages, asynchronously fetches the installer URL host for
    /// both the installed and the new version, and flags the row when the hosts differ.
    /// See issue #4617 — defense-in-depth signal that an upgrade may be redirecting the
    /// download to a different domain than the user originally trusted.
    /// </summary>
    private void MaybeStartInstallerHostCheck()
    {
#if WINDOWS
        if (!Package.IsUpgradable) return;
        if (Package.Manager is not WinGet) return;
        if (Settings.Get(Settings.K.DisableInstallerHostChangeWarning)) return;

        string installedVersion = Package.VersionString;
        string newVersion = Package.NewVersionString;
        if (string.IsNullOrWhiteSpace(installedVersion) || string.IsNullOrWhiteSpace(newVersion))
            return;
        if (installedVersion == newVersion) return;

        _installerHostCheckCts?.Cancel();
        _installerHostCheckCts = new CancellationTokenSource();
        CancellationToken token = _installerHostCheckCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                var oldHosts = WinGet.TryGetInstallerHostsForVersion(Package, installedVersion);
                if (token.IsCancellationRequested) return;
                var newHosts = WinGet.TryGetInstallerHostsForVersion(Package, newVersion);
                if (token.IsCancellationRequested) return;

                if (oldHosts is null || newHosts is null) return;
                // Only flag when the two host sets are fully disjoint. If they share even
                // one host, the publisher hasn't moved hosting — adding/removing CDN mirrors
                // or architectures shouldn't trigger the warning.
                if (oldHosts.Overlaps(newHosts)) return;

                string tooltip = CoreTools.Translate(
                    "Installer host changed since the installed version.\n"
                    + "Old: {0}\n"
                    + "New: {1}\n\n"
                    + "This is usually harmless (the publisher moved hosting), "
                    + "but can also indicate a hijacked package manifest. "
                    + "Verify the new source before upgrading.",
                    string.Join(", ", oldHosts),
                    string.Join(", ", newHosts)
                );

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    InstallerHostChanged = true;
                    InstallerHostChangeTooltip = tooltip;
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(InstallerHostChanged))
                    );
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(InstallerHostChangeTooltip))
                    );
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"Installer-host check failed for {Package.Id}: {ex.Message}");
            }
        }, token);
#endif
    }

    private async Task LoadIconAsync()
    {
        CancellationToken token = _lifetimeCts.Token;
        long hash = Package.GetHash();
        if (GetCachedIcon(hash) is { } cached)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) IconBitmap = cached;
            });
            return;
        }

        if (HasRecentIconFailure(hash)) return;

        try
        {
            Bitmap? bitmap = await GetSharedIconLoad(hash, Package).WaitAsync(token).ConfigureAwait(false);
            if (bitmap is null) return;

            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) IconBitmap = bitmap;
            });
        }
        catch (OperationCanceledException) { /* row discarded before its icon finished loading */ }
        catch { MarkIconFailed(hash); }
    }

    private static Task<Bitmap?> GetSharedIconLoad(long hash, IPackage package)
    {
        lock (_inflightIconLoadsLock)
        {
            if (GetCachedIcon(hash) is { } cached)
                return Task.FromResult<Bitmap?>(cached);
            if (HasRecentIconFailure(hash))
                return Task.FromResult<Bitmap?>(null);
            if (_inflightIconLoads.TryGetValue(hash, out Task<Bitmap?>? existing))
                return existing;

            Task<Bitmap?> task = LoadAndCacheIconAsync(hash, package);
            _inflightIconLoads[hash] = task;
            _ = RemoveInflightIconLoadAsync(hash, task);
            return task;
        }
    }

    private static async Task RemoveInflightIconLoadAsync(long hash, Task<Bitmap?> task)
    {
        try { await task.ConfigureAwait(false); }
        finally
        {
            lock (_inflightIconLoadsLock)
            {
                if (_inflightIconLoads.TryGetValue(hash, out Task<Bitmap?>? current)
                    && ReferenceEquals(current, task))
                    _inflightIconLoads.Remove(hash);
            }
        }
    }

    private static async Task<Bitmap?> LoadAndCacheIconAsync(long hash, IPackage package)
    {
        await _iconLoadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var uri = await Task.Run(package.GetIconUrlIfAny).ConfigureAwait(false);
            if (uri is null) { MarkIconFailed(hash); return null; }

            Bitmap? decoded;
            if (uri.IsFile)
            {
                if (IsKnownUndecodableExtension(uri.LocalPath))
                {
                    MarkIconFailed(hash);
                    return null;
                }
                decoded = await Task.Run(() => TryDecodeIcon(uri.LocalPath)).ConfigureAwait(false);
            }
            else if (uri.Scheme is "http" or "https")
            {
                using var response = await _iconHttpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxIconDownloadBytes)
                {
                    MarkIconFailed(hash);
                    return null;
                }

                await response.Content.LoadIntoBufferAsync(MaxIconDownloadBytes).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                decoded = TryDecodeIcon(bytes, uri.Host);
            }
            else { MarkIconFailed(hash); return null; }

            if (decoded is null)
            {
                MarkIconFailed(hash);
                return null;
            }

            CacheIcon(hash, decoded);
            return decoded;
        }
        catch
        {
            MarkIconFailed(hash);
            return null;
        }
        finally
        {
            _iconLoadSemaphore.Release();
        }
    }

    // Icons come from a shared on-disk cache that can hold empty or partial entries after an
    // interrupted download; decoding those throws. Skip those entries instead of failing the row.
    private static Bitmap? TryDecodeIcon(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length == 0) return null;
        return TryDecodeIcon(() => { using var fs = File.OpenRead(filePath); return DecodeDownscaled(fs); }, filePath);
    }

    private static Bitmap? TryDecodeIcon(byte[] bytes, string source)
        => bytes.Length == 0 ? null : TryDecodeIcon(() => { using var ms = new MemoryStream(bytes); return DecodeDownscaled(ms); }, source);

    private static Bitmap? TryDecodeIcon(Func<Bitmap> decode, string source)
    {
        try { return decode(); }
        catch (Exception ex) { Logger.Warn($"Discarding undecodable icon '{source}': {ex.Message}"); return null; }
    }

    // Decode directly at the display-cache width. This avoids allocating a full-size bitmap first,
    // which is important for untrusted or unusually large package artwork.
    private static Bitmap DecodeDownscaled(Stream stream)
        => Bitmap.DecodeToWidth(stream, MaxIconSide, BitmapInterpolationMode.HighQuality);

    private static bool IsKnownUndecodableExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Package.Tag))
        {
            UpdateDisplayState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListedOpacity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListedNameTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagBackdropIconPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagSymbolIconPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagIconVisible)));
        }
        else if (e.PropertyName == nameof(Package.IsChecked))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
        else
        {
            PropertyChanged?.Invoke(this, e);
        }
    }

    private void UpdateDisplayState()
    {
        ListedOpacity = Package.Tag switch
        {
            PackageTag.OnQueue or PackageTag.BeingProcessed or PackageTag.Unavailable => 0.5f,
            _ => 1.0f,
        };
        ListedNameTooltip = Package.Name;

        // Match WinUI: an accent-coloured filled disc with the symbol punched out, then the
        // symbol drawn on top in the default foreground. OnQueue/Unavailable show no badge.
        (string symbol, string backdrop) = Package.Tag switch
        {
            PackageTag.AlreadyInstalled => ("installed", "installed_filled"),
            PackageTag.IsUpgradable => ("upgradable", "upgradable_filled"),
            PackageTag.Pinned => ("pin", "pin_filled"),
            PackageTag.BeingProcessed => ("loading", "loading_filled"),
            PackageTag.Failed => ("warning", "warning_filled"),
            _ => ("", ""),
        };
        TagIconVisible = symbol.Length > 0;
        TagSymbolIconPath = TagIconVisible ? $"avares://UniGetUI/Assets/Symbols/{symbol}.svg" : "";
        TagBackdropIconPath = TagIconVisible ? $"avares://UniGetUI/Assets/Symbols/{backdrop}.svg" : "";
    }

    public void Dispose()
    {
        Package.PropertyChanged -= Package_PropertyChanged;
        _installerHostCheckCts?.Cancel();
        _installerHostCheckCts?.Dispose();
        _installerHostCheckCts = null;
        _lifetimeCts.Cancel(); // not disposed: a background load may still hold the token
    }
}

/// <summary>
/// Avalonia-compatible observable collection of PackageWrapper with sorting support
/// (replaces WinUI's ObservablePackageCollection that used SortableObservableCollection).
/// </summary>
public sealed class ObservablePackageCollection : AvaloniaList<PackageWrapper>
{
    public enum Sorter
    {
        Checked,
        Name,
        Id,
        Version,
        NewVersion,
        Source,
    }

    public Sorter CurrentSorter { get; private set; } = Sorter.Name;
    private bool _ascending = true;

    /// <summary>Fires when any wrapper's IsChecked changes, or when items are added/removed.</summary>
    public event EventHandler? SelectionStateChanged;

    public ObservablePackageCollection()
    {
        CollectionChanged += OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (PackageWrapper w in e.OldItems) w.PropertyChanged -= OnWrapperPropertyChanged;
        if (e.NewItems is not null)
            foreach (PackageWrapper w in e.NewItems) w.PropertyChanged += OnWrapperPropertyChanged;
        SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWrapperPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageWrapper.IsChecked))
            SelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the tri-state value for a "select-all" checkbox: true=all, false=none, null=some.</summary>
    public bool? GetSelectionState()
    {
        if (Count == 0) return false;
        int checkedCount = 0;
        foreach (var w in this) if (w.IsChecked) checkedCount++;
        if (checkedCount == 0) return false;
        if (checkedCount == Count) return true;
        return null;
    }

    public List<IPackage> GetPackages() =>
        this.Select(w => w.Package).ToList();

    public List<IPackage> GetCheckedPackages() =>
        this.Where(w => w.IsChecked).Select(w => w.Package).ToList();

    public void SelectAll()
    {
        foreach (var w in this) w.IsChecked = true;
    }

    public void ClearSelection()
    {
        foreach (var w in this) w.IsChecked = false;
    }

    public void SortBy(Sorter sorter) => CurrentSorter = sorter;

    public void SetSortDirection(bool ascending) => _ascending = ascending;

    /// <summary>Returns <paramref name="items"/> in the current sort order.</summary>
    public IEnumerable<PackageWrapper> ApplyToList(IEnumerable<PackageWrapper> items)
    {
        var comparer = Comparer<PackageWrapper>.Create((a, b) => Compare(a, b, CurrentSorter));
        return _ascending ? items.OrderBy(w => w, comparer) : items.OrderByDescending(w => w, comparer);
    }

    // Shared by the "Order by" menu (ApplyToList) and the DataGrid's native column sorting
    // (GetColumnComparer) so both order identically. Versions compare semantically via
    // CoreTools.Version; a string key can't be used there because that struct has no ToString
    // override, so its key would be a constant type name that never sorts.
    private static int Compare(PackageWrapper a, PackageWrapper b, Sorter sorter) => sorter switch
    {
        Sorter.Version => a.Package.NormalizedVersion.CompareTo(b.Package.NormalizedVersion),
        Sorter.NewVersion => a.Package.NormalizedNewVersion.CompareTo(b.Package.NormalizedNewVersion),
        _ => string.Compare(GetSortKey(a, sorter), GetSortKey(b, sorter), StringComparison.OrdinalIgnoreCase),
    };

    private static string GetSortKey(PackageWrapper w, Sorter sorter) => sorter switch
    {
        Sorter.Checked => w.IsChecked ? "0" : "1",
        Sorter.Name => w.Package.Name,
        Sorter.Id => w.Package.Id,
        Sorter.Source => w.Package.Source.AsString_DisplayName,
        _ => w.Package.Name,
    };

    // Comparer for the DataGrid's native column sorting. Reflection-free (unlike SortMemberPath),
    // so it keeps working under full-trim NativeAOT release builds — see issue #5103.
    public static IComparer GetColumnComparer(Sorter sorter) =>
        Comparer<PackageWrapper>.Create((a, b) => Compare(a, b, sorter));
}
