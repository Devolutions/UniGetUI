using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Interfaces;

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
    private static readonly ConcurrentDictionary<long, Bitmap?> _iconCache = new();

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
    public string TagIconPath { get; private set; } = "";
    public bool TagIconVisible { get; private set; }

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
        return $"avares://UniGetUI.Avalonia/Assets/Symbols/{name}.svg";
    }

    public PackageWrapper(IPackage package, PackagesPageViewModel page)
    {
        Package = package;
        _page = page;
        VersionComboString = package.VersionString;

        Package.PropertyChanged += Package_PropertyChanged;
        UpdateDisplayState();

        if (!Settings.Get(Settings.K.DisableIconsOnPackageLists))
            _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        long hash = Package.GetHash();
        if (_iconCache.TryGetValue(hash, out Bitmap? cached))
        {
            if (cached is not null)
                IconBitmap = cached;
            return;
        }

        try
        {
            var uri = Package.GetIconUrlIfAny();
            if (uri is null) { _iconCache[hash] = null; return; }

            Bitmap bitmap;
            if (uri.IsFile)
            {
                bitmap = new Bitmap(uri.LocalPath);
            }
            else if (uri.Scheme is "http" or "https")
            {
                var bytes = await _iconHttpClient.GetByteArrayAsync(uri);
                using var ms = new MemoryStream(bytes);
                bitmap = new Bitmap(ms);
            }
            else { _iconCache[hash] = null; return; }

            _iconCache[hash] = bitmap;
            await Dispatcher.UIThread.InvokeAsync(() => IconBitmap = bitmap);
        }
        catch { _iconCache[hash] = null; }
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Package.Tag))
        {
            UpdateDisplayState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListedOpacity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListedNameTooltip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagIconPath)));
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

        string tagName = Package.Tag switch
        {
            PackageTag.AlreadyInstalled => "installed_filled",
            PackageTag.IsUpgradable => "upgradable_filled",
            PackageTag.Pinned => "pin_filled",
            PackageTag.OnQueue => "sandclock",
            PackageTag.BeingProcessed => "loading_filled",
            PackageTag.Failed => "warning_filled",
            _ => "",
        };
        TagIconVisible = tagName.Length > 0;
        TagIconPath = TagIconVisible
            ? $"avares://UniGetUI.Avalonia/Assets/Symbols/{tagName}.svg"
            : "";
    }

    public void Dispose()
    {
        Package.PropertyChanged -= Package_PropertyChanged;
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
        Sorter.Version => w.Package.NormalizedVersion.ToString(),
        Sorter.NewVersion => w.Package.NormalizedNewVersion.ToString(),
        Sorter.Source => w.Package.Source.AsString_DisplayName,
        _ => w.Package.Name,
    };
}
