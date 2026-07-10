using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
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
public sealed class PackageWrapper : INotifyPropertyChanged, IDisposable
{
    private static readonly HttpClient _iconHttpClient = new(CoreTools.GenericHttpClientParameters)
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly SemaphoreSlim _iconLoadSemaphore = new(8, 8);

    // Cap decoded icon size; the list shows icons at ≤64px (128 covers 2x DPI).
    private const int MaxIconSide = 128;

    // Bounded LRU by package hash. Evicted entries aren't disposed: a visible row may still
    // reference the bitmap, so dropping it here only makes it GC-eligible.
    private const int MaxIconCacheEntries = 512;
    private static readonly object _iconCacheLock = new();
    private static readonly Dictionary<long, LinkedListNode<(long Hash, Bitmap? Bitmap)>> _iconCache = new();
    private static readonly LinkedList<(long Hash, Bitmap? Bitmap)> _iconCacheOrder = new();

    private static bool TryGetCachedIcon(long hash, out Bitmap? bitmap)
    {
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(hash, out var node))
            {
                _iconCacheOrder.Remove(node);
                _iconCacheOrder.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }
        bitmap = null;
        return false;
    }

    private static void CacheIcon(long hash, Bitmap? bitmap)
    {
        lock (_iconCacheLock)
        {
            if (_iconCache.TryGetValue(hash, out var existing))
            {
                existing.Value = (hash, bitmap);
                _iconCacheOrder.Remove(existing);
                _iconCacheOrder.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<(long, Bitmap?)>((hash, bitmap));
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

        // Icons load lazily per visible row (see EnsureIconLoaded), not eagerly for every result.
        MaybeStartInstallerHostCheck();
    }

    private int _iconLoadStarted;

    /// <summary>Loads this row's icon at most once; called when the row becomes visible.</summary>
    public void EnsureIconLoaded()
    {
        if (Settings.Get(Settings.K.DisableIconsOnPackageLists)) return;
        if (Interlocked.Exchange(ref _iconLoadStarted, 1) != 0) return;
        _ = LoadIconAsync();
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
        if (TryGetCachedIcon(hash, out Bitmap? cached))
        {
            if (cached is not null)
                IconBitmap = cached;
            return;
        }

        try
        {
            await _iconLoadSemaphore.WaitAsync(token).ConfigureAwait(false);
            Bitmap bitmap;
            try
            {
                var uri = await Task.Run(Package.GetIconUrlIfAny, token).ConfigureAwait(false);
                if (uri is null) { CacheIcon(hash, null); return; }

                Bitmap? decoded;
                if (uri.IsFile)
                {
                    if (!IsSkiaDecodableExtension(uri.LocalPath))
                    {
                        // Avalonia's Bitmap (Skia) can't decode SVG/AVIF/ICO/TIFF — the
                        // icon cache may produce those. Reject upfront so we don't throw.
                        CacheIcon(hash, null);
                        return;
                    }
                    decoded = await Task.Run(() => TryDecodeIcon(uri.LocalPath), token).ConfigureAwait(false);
                }
                else if (uri.Scheme is "http" or "https")
                {
                    var bytes = await _iconHttpClient.GetByteArrayAsync(uri, token).ConfigureAwait(false);
                    decoded = TryDecodeIcon(bytes, uri.Host);
                }
                else { CacheIcon(hash, null); return; }

                if (decoded is null) { CacheIcon(hash, null); return; }
                bitmap = decoded;
                CacheIcon(hash, bitmap);
            }
            finally
            {
                _iconLoadSemaphore.Release();
            }

            if (token.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested) IconBitmap = bitmap;
            });
        }
        catch (OperationCanceledException) { /* row discarded before its icon finished loading */ }
        catch { CacheIcon(hash, null); }
    }

    // Icons come from a shared on-disk cache that can hold empty or partial entries after an
    // interrupted download; decoding those throws. Skip them quietly instead of surfacing an error.
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
        catch (Exception ex) { Logger.Debug($"Discarding undecodable icon '{source}': {ex.Message}"); return null; }
    }

    // Downscales oversized icons to MaxIconSide; small icons pass through (never upscaled).
    private static Bitmap DecodeDownscaled(Stream stream)
    {
        var bitmap = new Bitmap(stream);
        PixelSize size = bitmap.PixelSize;
        if (size.Width <= MaxIconSide && size.Height <= MaxIconSide)
            return bitmap;

        double scale = (double)MaxIconSide / Math.Max(size.Width, size.Height);
        var target = new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));

        try
        {
            return bitmap.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    private static bool IsSkiaDecodableExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
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
    public IEnumerable<PackageWrapper> ApplyToList(IEnumerable<PackageWrapper> items) =>
        _ascending
            ? items.OrderBy(GetSortKey, StringComparer.OrdinalIgnoreCase)
            : items.OrderByDescending(GetSortKey, StringComparer.OrdinalIgnoreCase);

    private string GetSortKey(PackageWrapper w) => CurrentSorter switch
    {
        Sorter.Checked => w.IsChecked ? "0" : "1",
        Sorter.Name => w.Package.Name,
        Sorter.Id => w.Package.Id,
        Sorter.Version => w.Package.NormalizedVersion.ToString() ?? string.Empty,
        Sorter.NewVersion => w.Package.NormalizedNewVersion.ToString() ?? string.Empty,
        Sorter.Source => w.Package.Source.AsString_DisplayName,
        _ => w.Package.Name,
    };
}
