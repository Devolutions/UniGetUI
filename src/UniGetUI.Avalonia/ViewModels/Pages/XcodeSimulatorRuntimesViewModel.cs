using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Xml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>Simulator runtime snapshots from Apple and the selected developer tools.</summary>
public partial class XcodeSimulatorRuntimesViewModel(IXcodeSimulatorRuntimeService service) : ViewModelBase
{
    private IReadOnlyList<XcodeSimulatorRuntime> _catalog = [];
    private IReadOnlyList<XcodeSimulatorRuntime> _installed = [];
    private XcodeSimulatorRuntime? _operationRuntime;
    private IReadOnlyList<XcodeSimulatorSdkMapping>? _sdkMappings;
    private MappingContext? _mapping;
    private DateTime? _catalogChecked;
    private DateTime? _installedChecked;
    private bool _catalogCacheLoaded;
    private bool _catalogFromSavedFile;
    private bool _catalogFresh;
    private int _platformFilterIndex = XcodePreferences.RuntimePlatformFilter;
    private int _sourceFilterIndex = XcodePreferences.RuntimeSourceFilter;
    private int _releaseFilterIndex = XcodePreferences.RuntimeReleaseFilter;
    private int _architectureFilterIndex = XcodePreferences.RuntimeArchitectureFilter;

    [ObservableProperty]
    private string _catalogStatus = CoreTools.Translate("The Apple simulator catalog has not been read.");

    [ObservableProperty]
    private string _installedStatus = CoreTools.Translate("Installed simulator runtimes have not been read.");

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(MappingStatus))]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private IReadOnlyList<XcodeSimulatorRuntimeItemViewModel> _items = [];

    public bool IsEmpty => Items.Count == 0;
    public bool HasMappingContext => _mapping is not null;
    public string MappingCatalogStatus => _mapping?.CatalogStatus ?? "";
    public string MappingStatus => _mapping switch
    {
        null => "",
        { SDKs.Count: 0 } context => CoreTools.Translate(
            "SDK build information for {0} is unavailable. A missing runtime does not mean it is incompatible.", context.Title),
        { } when IsRefreshing && _sdkMappings is null => CoreTools.Translate("Reading simulator runtimes..."),
        { } when _sdkMappings is null => CoreTools.Translate(
            "Apple's SDK-to-simulator mappings are unavailable. A missing runtime does not mean it is incompatible."),
        { } context => CoreTools.Translate(
            "Showing runtimes Apple maps to SDK builds for {0}. Mappings are informational; absent runtimes are not necessarily incompatible.",
            context.Title),
    };
    public string EmptyText => IsRefreshing
        ? CoreTools.Translate("Reading simulator runtimes...")
        : _catalogChecked is null && _installedChecked is null
            ? CoreTools.Translate("Simulator runtimes are unavailable.")
            : CoreTools.Translate("No simulator runtimes match these filters.");

    public int PlatformFilterIndex
    {
        get => _platformFilterIndex;
        set
        {
            if (value is < 0 or > 4 || !SetProperty(ref _platformFilterIndex, value)) return;
            XcodePreferences.RuntimePlatformFilter = value;
            Rebuild();
        }
    }

    public int SourceFilterIndex
    {
        get => _sourceFilterIndex;
        set
        {
            if (value is < 0 or > 2 || !SetProperty(ref _sourceFilterIndex, value)) return;
            XcodePreferences.RuntimeSourceFilter = value;
            Rebuild();
        }
    }

    public int ReleaseFilterIndex
    {
        get => _releaseFilterIndex;
        set
        {
            if (value is < 0 or > 2 || !SetProperty(ref _releaseFilterIndex, value)) return;
            XcodePreferences.RuntimeReleaseFilter = value;
            Rebuild();
        }
    }

    public int ArchitectureFilterIndex
    {
        get => _architectureFilterIndex;
        set
        {
            if (value is < 0 or > 3 || !SetProperty(ref _architectureFilterIndex, value)) return;
            XcodePreferences.RuntimeArchitectureFilter = value;
            Rebuild();
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    internal bool CanInstall(XcodeSimulatorRuntime runtime)
        => _catalogFresh && _catalog.Contains(runtime) && XcodeSimulatorRuntimeInstaller.Supports(runtime);

    internal bool CanDownload(XcodeSimulatorRuntime runtime)
        => _catalogFresh && _catalog.Contains(runtime) && XcodePublicSimulatorRuntimeDownloader.Supports(runtime);

    internal bool HasOperationTarget => _operationRuntime is not null;

    internal void SetOperationTarget(XcodeSimulatorRuntime? runtime)
    {
        _operationRuntime = runtime;
        foreach (XcodeSimulatorRuntimeItemViewModel item in Items)
            item.SetOperationTarget(runtime is not null && ReferenceEquals(item.Runtime, runtime));
    }

    internal void ShowMappedFor(string title, IReadOnlyList<XcodeSdkIdentity> sdks, string catalogStatus)
    {
        _mapping = new MappingContext(title, [.. sdks], catalogStatus);
        MappingChanged();
    }

    [RelayCommand]
    private void ShowAllRuntimes()
    {
        _mapping = null;
        MappingChanged();
    }

    private void MappingChanged()
    {
        OnPropertyChanged(nameof(HasMappingContext));
        OnPropertyChanged(nameof(MappingStatus));
        OnPropertyChanged(nameof(MappingCatalogStatus));
        Rebuild();
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        try
        {
            await LoadSavedCatalogAsync(cancellationToken);
            await Task.WhenAll(RefreshCatalogAsync(cancellationToken), RefreshInstalledAsync(cancellationToken));
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task LoadSavedCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalogCacheLoaded || service is not IXcodeSimulatorRuntimeCatalogCache cache) return;

        try
        {
            XcodeSavedSimulatorRuntimeCatalog? saved = await cache.GetSavedCatalogAsync(cancellationToken);
            _catalogCacheLoaded = true;
            if (saved is null || _catalogChecked is not null) return;
            _catalog = saved.Catalog.Runtimes;
            _sdkMappings = saved.Catalog.SDKMappings;
            _catalogChecked = saved.SavedAtUtc.ToLocalTime();
            _catalogFromSavedFile = true;
            _catalogFresh = false;
            CatalogStatus = CoreTools.Translate("Saved Apple simulator catalog may be out of date; last saved {0}.",
                FormatTime(_catalogChecked.Value));
            OnPropertyChanged(nameof(MappingStatus));
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or XcodesException)
        {
            _catalogCacheLoaded = true;
            Logger.Warn($"Could not read the saved Apple simulator catalog: {ex.Message}");
        }
    }

    private async Task RefreshCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            XcodeSimulatorRuntimeCatalog snapshot = await service.GetCatalogAsync(cancellationToken);
            _catalog = snapshot.Runtimes;
            _sdkMappings = snapshot.SDKMappings;
            _catalogFresh = true;
            _catalogFromSavedFile = false;
            _catalogChecked = DateTime.Now;
            CatalogStatus = CoreTools.Translate("Apple simulator catalog refreshed at {0}.", FormatTime(_catalogChecked.Value));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CatalogFailed(CoreTools.Translate("The request timed out."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or XcodesException)
        {
            Logger.Warn($"Could not read the Apple simulator catalog: {ex.Message}");
            CatalogFailed(ex.Message);
        }
        OnPropertyChanged(nameof(MappingStatus));
        Rebuild();
    }

    private void CatalogFailed(string reason)
    {
        _catalogFresh = false;
        CatalogStatus = _catalogChecked is { } checkedAt
            ? _catalogFromSavedFile
                ? CoreTools.Translate("Saved Apple simulator catalog may be out of date; last saved {0}. {1}",
                    FormatTime(checkedAt), reason)
                : CoreTools.Translate("Apple simulator catalog may be out of date; last refreshed {0}. {1}",
                    FormatTime(checkedAt), reason)
            : CoreTools.Translate("Could not read the Apple simulator catalog: {0}", reason);
    }

    private async Task RefreshInstalledAsync(CancellationToken cancellationToken)
    {
        try
        {
            _installed = await service.GetInstalledAsync(cancellationToken);
            _installedChecked = DateTime.Now;
            InstalledStatus = CoreTools.Translate("Installed runtimes refreshed at {0}.", FormatTime(_installedChecked.Value));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            InstalledFailed(CoreTools.Translate("The request timed out."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XcodesException)
        {
            Logger.Warn($"Could not read installed simulator runtimes: {ex.Message}");
            InstalledFailed(ex.Message);
        }
        Rebuild();
    }

    private void InstalledFailed(string reason)
    {
        InstalledStatus = _installedChecked is { } checkedAt
            ? CoreTools.Translate("Installed runtimes may be out of date; last refreshed {0}. {1}",
                FormatTime(checkedAt), reason)
            : CoreTools.Translate("Could not read installed simulator runtimes: {0}", reason);
    }

    private static string FormatTime(DateTime time) => time.ToString("g", CultureInfo.CurrentCulture);

    private void Rebuild()
    {
        string search = SearchText?.Trim() ?? "";
        string? platform = PlatformFilterIndex switch
        {
            1 => "iOS",
            2 => "watchOS",
            3 => "tvOS",
            4 => "visionOS",
            _ => null,
        };
        XcodeArchitecture? architecture = ArchitectureFilterIndex switch
        {
            1 => XcodeArchitecture.AppleSilicon,
            2 => XcodeArchitecture.Universal,
            3 => XcodeArchitecture.Intel,
            _ => null,
        };

        IEnumerable<XcodeSimulatorRuntime> source = _mapping is { } mapping
            ? XcodeSimulatorRuntimeMatching.ForSDKs(mapping.SDKs, _sdkMappings ?? [], _catalog, _installed)
            : _installed.Concat(_catalog);
        XcodeSimulatorRuntimeItemViewModel[] items = [.. source
            .Select(runtime => new XcodeSimulatorRuntimeItemViewModel(
                runtime, ReleaseStage(runtime), CanInstall(runtime), CanDownload(runtime)))
            .Where(item => platform is null || item.Runtime.Platform == platform)
            .Where(item => architecture is null || item.Runtime.Architecture == architecture)
            .Where(item => SourceFilterIndex switch
            {
                1 => item.Runtime.Origin != XcodeSimulatorRuntimeOrigin.AppleCatalog,
                2 => item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog,
                _ => true,
            })
            .Where(item => ReleaseFilterIndex switch
            {
                1 => item.IsPrerelease == false,
                2 => item.IsPrerelease == true,
                _ => true,
            })
            .Where(item => search.Length == 0
                || item.Runtime.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Runtime.Platform.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Runtime.Version.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Runtime.Build?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                || item.Runtime.Path?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(item => item.Runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog)
            .ThenBy(item => item.Runtime.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Runtime.Version, Comparer<string>.Create(XcodeVersion.Compare))
            .ThenBy(item => item.Runtime.Build, StringComparer.Ordinal)];
        foreach (XcodeSimulatorRuntimeItemViewModel item in Items)
            item.SetOperationTarget(false);
        foreach (XcodeSimulatorRuntimeItemViewModel item in items)
            item.SetOperationTarget(_operationRuntime is not null && ReferenceEquals(item.Runtime, _operationRuntime));
        Items = items;
    }

    private bool? ReleaseStage(XcodeSimulatorRuntime runtime)
    {
        if (runtime.IsPrerelease is { } known)
            return known;
        if (runtime.Origin == XcodeSimulatorRuntimeOrigin.AppleCatalog || !_catalogFresh || runtime.Build is null
            || runtime.Architecture == XcodeArchitecture.Unspecified)
            return null;

        bool?[] candidates = [.. _catalog
            .Where(item => item.Platform == runtime.Platform && item.Version == runtime.Version
                && item.Build == runtime.Build && item.Architecture == runtime.Architecture)
            .Select(item => item.IsPrerelease)];
        return candidates.Length > 0 && candidates[0] is not null
            && candidates.All(stage => stage == candidates[0]) ? candidates[0] : null;
    }

    private sealed record MappingContext(string Title, IReadOnlyList<XcodeSdkIdentity> SDKs, string CatalogStatus);
}

public sealed class XcodeSimulatorRuntimeItemViewModel(
    XcodeSimulatorRuntime runtime, bool? isPrerelease, bool canInstall, bool canDownload) : ViewModelBase
{
    private bool _isOperationTarget;

    public XcodeSimulatorRuntime Runtime { get; } = runtime;
    public bool? IsPrerelease { get; } = isPrerelease;
    public bool CanInstall { get; } = canInstall;
    public bool CanDownload { get; } = canDownload;
    public bool IsOperationTarget => _isOperationTarget;
    public string Title => Runtime.Name;
    public string CancelOperationText => CoreTools.Translate("Cancel operation for {0} ({1})", Title, Description);

    public string Description
    {
        get
        {
            List<string> parts =
            [
                Runtime.Origin switch
                {
                    XcodeSimulatorRuntimeOrigin.Installed => CoreTools.Translate("Installed"),
                    XcodeSimulatorRuntimeOrigin.Bundled => CoreTools.Translate("Bundled with selected developer tools"),
                    _ => CoreTools.Translate("Apple catalog"),
                },
            ];
            if (Runtime.Build is { Length: > 0 } build)
                parts.Add(CoreTools.Translate("Build {0}", build));
            parts.Add(XcodeText.Architecture(Runtime.Architecture) is { Length: > 0 } architecture
                ? architecture : CoreTools.Translate("Architecture unknown"));
            parts.Add(IsPrerelease switch
            {
                true => CoreTools.Translate("Pre-release"),
                false => CoreTools.Translate("Release"),
                _ => CoreTools.Translate("Release status unknown"),
            });
            if (Runtime.SizeBytes is { } bytes)
                parts.Add(CoreTools.Translate("Size: {0} MB", (bytes / 1_000_000d).ToString("N1", CultureInfo.CurrentCulture)));
            if (Runtime.State is { Length: > 0 } state)
                parts.Add(CoreTools.Translate("State: {0}", state));
            return string.Join(" · ", parts);
        }
    }

    public string Path => Runtime.Path ?? "";
    public bool HasPath => Runtime.Path is { Length: > 0 };
    public bool HasLocalPath => Runtime.Origin is XcodeSimulatorRuntimeOrigin.Installed or XcodeSimulatorRuntimeOrigin.Bundled
        && Path.StartsWith('/') && !Path.Contains('\0');

    internal void SetOperationTarget(bool value) => SetProperty(ref _isOperationTarget, value, nameof(IsOperationTarget));
}
