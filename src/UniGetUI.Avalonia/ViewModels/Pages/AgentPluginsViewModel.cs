using System.Collections.ObjectModel;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public enum PluginLibraryView { Browse, Installed }
public enum PluginAddMode { None, Marketplace, Identifier }

public partial class AgentPluginsViewModel : ViewModelBase
{
    private const int PageSize = 100;
    private sealed class ProviderState
    {
        public bool IsLoaded { get; set; }
        public IReadOnlyList<AgentPluginItem> Installed { get; set; } = [];
        public IReadOnlyList<AgentPluginMarketplace> Marketplaces { get; set; } = [];
        public Dictionary<string, IReadOnlyList<AgentPluginItem>> BrowseCatalogs { get; } = new(StringComparer.Ordinal);
        public string Query { get; set; } = "";
        public string? SelectedMarketplace { get; set; }
        public string? SelectedPlugin { get; set; }
        public PluginLibraryView View { get; set; }
        public int VisibleCount { get; set; } = PageSize;
    }

    private IAgentPluginService _service;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly AgentPluginCatalogCache? _catalogCache;
    private readonly IAgentPluginService _copilotApp;
    private readonly Dictionary<IAgentPluginService, ProviderState> _providerStates = new(ReferenceEqualityComparer.Instance);
    private ProviderState _state;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _catalogRead;
    private int _catalogGeneration;
    private IReadOnlyList<AgentPluginItem> _filteredPlugins = [];
    private bool _stopped;
    private bool _readingCatalog;
    public IReadOnlyList<IAgentPluginService> Providers { get; }
    public IAgentPluginService SelectedProvider
    {
        get => _service;
        set
        {
            if (!CanSelectProvider || ReferenceEquals(value, _service) || !Providers.Contains(value)) return;
            CancelCatalogRead();
            SaveCurrentState();
            _service = value;
            _state = GetState(value);
            AddMode = PluginAddMode.None;
            PluginInput = "";
            MarketplaceInput = "";
            Status = "";
            HasError = false;
            NeedsRefresh = false;
            Activity = "";
            ShowRestartNotice = false;
            RestoreCurrentState();
            Filter();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Guidance));
            OnPropertyChanged(nameof(RestartNotice));
            OnPropertyChanged(nameof(SupportsToggle));
            OnPropertyChanged(nameof(SupportsUpdate));
            OnPropertyChanged(nameof(IsAppHandoff));
            OnPropertyChanged(nameof(IsCopilot));
            OnPropertyChanged(nameof(ShowCopilotAppFallback));
            OnPropertyChanged(nameof(MarketplaceActionLabel));
            OnPropertyChanged(nameof(InstallActionLabel));
            NotifyActions();
        }
    }
    public string Guidance => _service.Guidance;
    public string RestartNotice => _service.RestartNotice;
    public bool SupportsToggle => _service.SupportsToggle;
    public bool SupportsUpdate => _service.SupportsUpdate;
    public bool IsAppHandoff => _service is CopilotAppPluginService;
    public bool IsCopilot => _service is CopilotPluginService;
    public bool ShowCopilotAppFallback => IsCopilot;
    public string MarketplaceActionLabel => IsAppHandoff ? CoreTools.Translate("Open in Copilot app") : CoreTools.Translate("Add and browse");
    public string InstallActionLabel => IsAppHandoff ? CoreTools.Translate("Open in Copilot app") : CoreTools.Translate("Install plugin");
    [ObservableProperty] private IReadOnlyList<AgentPluginItem> _plugins = [];
    public ObservableCollection<AgentPluginMarketplace> Marketplaces { get; } = [];
    [ObservableProperty] private bool _isLoadingCatalog;
    [ObservableProperty] private PluginLibraryView _view;
    [ObservableProperty] private PluginAddMode _addMode;
    [ObservableProperty] private string _activity = "";
    [ObservableProperty] private bool _showRestartNotice;
    public bool InstalledOnly { get => View == PluginLibraryView.Installed; set => View = value ? PluginLibraryView.Installed : PluginLibraryView.Browse; }
    public bool AddingMarketplace => AddMode == PluginAddMode.Marketplace;
    public bool AddingPlugin => AddMode == PluginAddMode.Identifier;
    public bool HasActivity => Activity.Length > 0;
    public bool IsWorking => IsBusy || IsLoadingCatalog;
    public string SelectedSource => Marketplaces.FirstOrDefault(source => source.Name == SelectedPlugin?.Marketplace)?.Source ?? "";
    [ObservableProperty] private string _marketplaceInput = "";
    [ObservableProperty] private string _pluginInput = "";
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private AgentPluginItem? _selectedPlugin;
    [ObservableProperty] private AgentPluginMarketplace? _selectedMarketplace;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _needsRefresh;
    [ObservableProperty] private string _status = "";
    public bool CanWork => !_stopped && !IsBusy;
    public bool CanSelectProvider => CanWork;
    public bool CanChange => CanWork && !IsLoadingCatalog && !NeedsRefresh;
    public bool CanRetry => CanWork && !IsLoadingCatalog && (HasError || NeedsRefresh);
    public bool ShowInstallAction => SelectedPlugin is { IsInstalled: false, CanInstall: true };
    public bool ShowUninstallAction => SelectedPlugin is { IsInstalled: true, CanManage: true };
    public bool ShowUpdateAction => ShowUninstallAction && SupportsUpdate;
    public bool ShowEnableAction => SupportsToggle && SelectedPlugin is { IsInstalled: true, CanToggle: true, Enabled: false };
    public bool ShowDisableAction => SupportsToggle && SelectedPlugin is { IsInstalled: true, CanToggle: true, Enabled: true };
    public bool CanInstall => CanChange && ShowInstallAction;
    public bool CanManage => CanChange && ShowUninstallAction;
    public bool CanUpdate => CanManage && SupportsUpdate;
    public bool CanEnable => CanChange && ShowEnableAction;
    public bool CanDisable => CanChange && ShowDisableAction;
    public bool CanRefreshMarketplace => CanChange && SelectedMarketplace is { CanRefresh: true };
    public bool CanRemoveMarketplace => CanChange && SelectedMarketplace is { CanManage: true };
    private AgentPluginItem[] UpdatablePlugins => Plugins.Where(item => item.IsInstalled && item.CanManage).ToArray();
    public bool ShowUpdateAll => !IsAppHandoff && SupportsUpdate && UpdatablePlugins.Length > 0;
    public bool CanUpdateAll => CanChange && !HasError && ShowUpdateAll;
    public string UpdateAllLabel => CoreTools.Translate("Update all {0}", UpdatablePlugins.Length);
    public bool HasMarketplaceFilter => CanWork && SelectedMarketplace is not null;
    public bool HasSelection => SelectedPlugin is not null;
    public bool ShowBrowseSelected => CanWork && !IsLoadingCatalog && View == PluginLibraryView.Browse && SelectedMarketplace is not null;
    public bool CanLoadMore => _filteredPlugins.Count > Plugins.Count;
    public string LoadMoreLabel => CoreTools.Translate("Show more ({0} of {1})", Plugins.Count, _filteredPlugins.Count);
    public bool ShowOpenPluginInApp => IsCopilot && SelectedPlugin is { IsInstalled: false };
    public bool CanOpenCopilotApp => (IsCopilot || IsAppHandoff) && CanWork && !IsLoadingCatalog;
    public string BrowseSelectedLabel => SelectedMarketplace is { } marketplace && _state.BrowseCatalogs.ContainsKey(marketplace.Name)
        ? CoreTools.Translate("Refresh marketplace") : CoreTools.Translate("Browse marketplace");

    public AgentPluginsViewModel() : this([new ClaudePluginService(), new CodexPluginService(), new CursorPluginService(),
        new CopilotPluginService()], ConfirmationDialog.ShowAsync,
        new AgentPluginCatalogCache(Path.Combine(CoreData.UniGetUICacheDirectory_Data, "AgentPlugins", "Catalogs")),
        new CopilotAppPluginService())
    { }
    internal AgentPluginsViewModel(IAgentPluginService service, Func<string, Task<bool>> confirm,
        AgentPluginCatalogCache? catalogCache = null, IAgentPluginService? copilotApp = null)
        : this([service], confirm, catalogCache, copilotApp) { }
    internal AgentPluginsViewModel(IReadOnlyList<IAgentPluginService> providers, Func<string, Task<bool>> confirm,
        AgentPluginCatalogCache? catalogCache = null, IAgentPluginService? copilotApp = null)
    {
        Providers = providers;
        _service = providers[0];
        _state = GetState(_service);
        _view = _state.View;
        _confirm = confirm;
        _catalogCache = catalogCache;
        _copilotApp = copilotApp ?? new CopilotAppPluginService();
    }

    partial void OnQueryChanged(string value)
    {
        if (!_readingCatalog)
        {
            _state.Query = value;
            _state.VisibleCount = PageSize;
        }
        Filter();
    }
    partial void OnViewChanged(PluginLibraryView value)
    {
        if (!_readingCatalog)
        {
            _state.View = value;
            _state.VisibleCount = PageSize;
        }
        OnPropertyChanged(nameof(InstalledOnly));
        Filter();
    }
    partial void OnAddModeChanged(PluginAddMode value)
    {
        OnPropertyChanged(nameof(AddingMarketplace));
        OnPropertyChanged(nameof(AddingPlugin));
    }
    partial void OnActivityChanged(string value) => OnPropertyChanged(nameof(HasActivity));
    partial void OnHasErrorChanged(bool value) => NotifyActions();
    partial void OnNeedsRefreshChanged(bool value) => NotifyActions();
    partial void OnSelectedPluginChanged(AgentPluginItem? value)
    {
        if (!_readingCatalog) _state.SelectedPlugin = value?.Id;
        NotifyActions();
    }
    partial void OnSelectedMarketplaceChanged(AgentPluginMarketplace? value)
    {
        if (!_readingCatalog) _state.SelectedMarketplace = value?.Name;
        Filter();
        NotifyActions();
    }
    partial void OnIsBusyChanged(bool value)
    {
        NotifyActions();
        OnPropertyChanged(nameof(IsWorking));
    }
    partial void OnIsLoadingCatalogChanged(bool value)
    {
        NotifyActions();
        OnPropertyChanged(nameof(IsWorking));
    }
    private void NotifyActions()
    {
        foreach (string name in new[] { nameof(CanWork), nameof(CanInstall), nameof(CanManage),
                     nameof(CanEnable), nameof(CanDisable), nameof(CanRefreshMarketplace), nameof(HasSelection),
                     nameof(CanUpdate), nameof(HasMarketplaceFilter), nameof(SelectedSource),
                     nameof(CanChange), nameof(CanRetry), nameof(ShowInstallAction), nameof(ShowUninstallAction),
                     nameof(ShowUpdateAction), nameof(ShowEnableAction), nameof(ShowDisableAction), nameof(CanRemoveMarketplace),
                     nameof(ShowUpdateAll), nameof(CanUpdateAll), nameof(UpdateAllLabel), nameof(CanSelectProvider),
                     nameof(ShowBrowseSelected), nameof(CanLoadMore), nameof(LoadMoreLabel),
                     nameof(ShowOpenPluginInApp), nameof(CanOpenCopilotApp), nameof(BrowseSelectedLabel) })
            OnPropertyChanged(name);
    }

    private void Filter(string? preservedSelection = null)
    {
        if (_readingCatalog) return;
        string? selected = preservedSelection ?? SelectedPlugin?.Id;
        string query = Query.Trim();
        _filteredPlugins = GetMergedCatalog().Where(plugin => (!InstalledOnly || plugin.IsInstalled)
                     && (SelectedMarketplace is null || plugin.Marketplace == SelectedMarketplace.Name)
                     && (plugin.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || plugin.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _state.VisibleCount = Math.Max(PageSize, _state.VisibleCount);
        Plugins = _filteredPlugins.Take(_state.VisibleCount).ToArray();
        SelectedPlugin = Plugins.FirstOrDefault(plugin => plugin.Id == selected);
        NotifyActions();
    }

    public IReadOnlyList<(IAgentPluginService Provider, AgentPluginItem Plugin)> GetKnownPlugins() =>
        Providers.SelectMany(provider => MergeCatalog(GetState(provider))
            .Select(plugin => (Provider: provider, Plugin: plugin))).ToArray();

    public IReadOnlyList<(IAgentPluginService Provider, AgentPluginMarketplace Marketplace)> GetKnownMarketplaces() =>
        Providers.SelectMany(provider => GetState(provider).Marketplaces
            .Select(marketplace => (Provider: provider, Marketplace: marketplace))).ToArray();

    public Task ChangeKnownItemAsync(IAgentPluginService provider, AgentPluginItem item, AgentPluginAction action)
    {
        if (!CanChange || !Providers.Contains(provider)) return Task.CompletedTask;
        if (!ReferenceEquals(provider, _service))
        {
            SelectedProvider = provider;
            if (!ReferenceEquals(provider, _service)) return Task.CompletedTask;
        }

        AgentPluginItem? known = MergeCatalog(_state).FirstOrDefault(candidate => candidate.Id == item.Id);
        if (known is null) return Task.CompletedTask;
        bool allowed = action switch
        {
            AgentPluginAction.Install => !known.IsInstalled && known.CanInstall,
            AgentPluginAction.Update => known.IsInstalled && known.CanManage && SupportsUpdate,
            AgentPluginAction.Enable or AgentPluginAction.Disable => known.IsInstalled && known.CanToggle && SupportsToggle,
            AgentPluginAction.Uninstall => known.IsInstalled && known.CanManage,
            _ => false,
        };
        if (!allowed) return Task.CompletedTask;
        SelectedPlugin = known;
        return ChangeAsync(known.Id, action);
    }

    private IReadOnlyList<AgentPluginItem> GetMergedCatalog() => MergeCatalog(_state);

    private static IReadOnlyList<AgentPluginItem> MergeCatalog(ProviderState state)
    {
        Dictionary<string, AgentPluginItem> items = new(StringComparer.Ordinal);
        foreach (var catalog in state.BrowseCatalogs.Values)
            foreach (var item in catalog)
                items[item.Id] = item;
        foreach (var installed in state.Installed)
        {
            items[installed.Id] = items.TryGetValue(installed.Id, out var existing)
                ? existing with
                {
                    Description = string.IsNullOrEmpty(installed.Description) ? existing.Description : installed.Description,
                    Version = installed.Version,
                    IsInstalled = true,
                    Enabled = installed.Enabled,
                    CanManage = installed.CanManage,
                    CanToggle = installed.CanToggle,
                    Notice = installed.Notice,
                    StatusText = installed.StatusText,
                }
                : installed;
        }
        return items.Values.ToArray();
    }

    private ProviderState GetState(IAgentPluginService service)
    {
        if (_providerStates.TryGetValue(service, out var state)) return state;
        state = new() { View = service.SupportsScopedBrowse ? PluginLibraryView.Installed : PluginLibraryView.Browse };
        _providerStates.Add(service, state);
        return state;
    }

    private void SaveCurrentState()
    {
        _state.Query = Query;
        _state.SelectedMarketplace = SelectedMarketplace?.Name;
        _state.SelectedPlugin = SelectedPlugin?.Id;
        _state.View = View;
    }

    private void RestoreCurrentState()
    {
        _readingCatalog = true;
        try
        {
            Marketplaces.Clear();
            foreach (var marketplace in _state.Marketplaces) Marketplaces.Add(marketplace);
            Query = _state.Query;
            View = _state.View;
            SelectedMarketplace = Marketplaces.FirstOrDefault(item => item.Name == _state.SelectedMarketplace);
            SelectedPlugin = GetMergedCatalog().FirstOrDefault(item => item.Id == _state.SelectedPlugin);
        }
        finally { _readingCatalog = false; }
    }

    [RelayCommand] private void ShowAddMarketplace() => AddMode = AddingMarketplace ? PluginAddMode.None : PluginAddMode.Marketplace;
    [RelayCommand] private void ShowInstallIdentifier() => AddMode = AddingPlugin ? PluginAddMode.None : PluginAddMode.Identifier;
    [RelayCommand]
    private Task InstallItemAsync(AgentPluginItem? item) => ChangeItemAsync(item, AgentPluginAction.Install);
    [RelayCommand]
    private Task UpdateItemAsync(AgentPluginItem? item) => ChangeItemAsync(item, AgentPluginAction.Update);
    [RelayCommand]
    private Task ToggleItemAsync(AgentPluginItem? item) =>
        ChangeItemAsync(item, item?.Enabled == true ? AgentPluginAction.Disable : AgentPluginAction.Enable);
    [RelayCommand]
    private Task UninstallItemAsync(AgentPluginItem? item) => ChangeItemAsync(item, AgentPluginAction.Uninstall);

    private Task ChangeItemAsync(AgentPluginItem? item, AgentPluginAction action)
    {
        if (!CanChange || item is null || !Plugins.Any(current => ReferenceEquals(current, item)))
            return Task.CompletedTask;
        bool allowed = action switch
        {
            AgentPluginAction.Install => !item.IsInstalled && item.CanInstall,
            AgentPluginAction.Update => item.IsInstalled && item.CanManage && SupportsUpdate,
            AgentPluginAction.Enable or AgentPluginAction.Disable => item.IsInstalled && item.CanToggle && SupportsToggle,
            AgentPluginAction.Uninstall => item.IsInstalled && item.CanManage,
            _ => false,
        };
        if (!allowed) return Task.CompletedTask;
        SelectedPlugin = item;
        return ChangeAsync(item.Id, action);
    }

    public Task LoadAsync() => !IsAppHandoff && !_state.IsLoaded && CanWork && !IsLoadingCatalog
        ? RefreshCommand.ExecuteAsync(null) : Task.CompletedTask;

    public Task LoadInstalledCatalogAsync() => !IsAppHandoff && !_state.IsLoaded && CanWork && !IsLoadingCatalog
        ? ReadInstalledCatalogAsync(CancellationToken.None) : Task.CompletedTask;

    public async Task LoadInstalledForAllProvidersAsync()
    {
        if (!CanWork || IsAppHandoff) return;
        IAgentPluginService current = _service;
        List<string> failures = [];
        try
        {
            foreach (var provider in Providers)
            {
                if (ReferenceEquals(provider, _copilotApp)) continue;
                if (GetState(provider).IsLoaded) continue;
                SelectedProvider = provider;
                try { await ReadInstalledCatalogAsync(CancellationToken.None, throwOnError: true); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{provider.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (!ReferenceEquals(_service, current) && CanSelectProvider)
                SelectedProvider = current;
        }
        SetProviderInventoryResult(failures, CoreTools.Translate("Loaded installed plugin inventories."));
    }

    public async Task RefreshInstalledForAllProvidersAsync()
    {
        if (!CanWork || IsAppHandoff) return;
        IAgentPluginService current = _service;
        List<string> failures = [];
        try
        {
            foreach (var provider in Providers)
            {
                if (ReferenceEquals(provider, _copilotApp)) continue;
                SelectedProvider = provider;
                try { await ReadInstalledCatalogAsync(CancellationToken.None, throwOnError: true); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{provider.Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (!ReferenceEquals(_service, current) && CanSelectProvider)
                SelectedProvider = current;
        }
        SetProviderInventoryResult(failures, CoreTools.Translate("Refreshed installed plugin inventories."));
    }

    private void SetProviderInventoryResult(IReadOnlyCollection<string> failures, string successMessage)
    {
        HasError = failures.Count > 0;
        Status = failures.Count > 0
            ? CoreTools.Translate("Some plugin inventories could not be loaded: {0}", string.Join(Environment.NewLine, failures))
            : successMessage;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsAppHandoff || !CanWork) return;
        await ReadInstalledCatalogAsync(CancellationToken.None);
        if (!HasError && View == PluginLibraryView.Browse && SelectedMarketplace is { } marketplace
            && _service.SupportsScopedBrowse)
            await BrowseMarketplaceAsync(marketplace.Name, true, CancellationToken.None);
    }

    [RelayCommand]
    private Task BrowseSelectedMarketplaceAsync() => SelectedMarketplace is { } marketplace
        ? BrowseMarketplaceAsync(marketplace.Name, _state.BrowseCatalogs.ContainsKey(marketplace.Name), CancellationToken.None)
        : Task.CompletedTask;

    [RelayCommand]
    private void LoadMore()
    {
        if (!CanLoadMore) return;
        _state.VisibleCount += PageSize;
        Filter(SelectedPlugin?.Id);
    }

    private async Task ReadInstalledCatalogAsync(CancellationToken token, bool throwOnError = false)
    {
        CancellationTokenSource read = BeginCatalogRead(token);
        IAgentPluginService provider = _service;
        int generation = _catalogGeneration;
        Status = CoreTools.Translate("Loading installed plugins for {0}…", provider.Name);
        HasError = false;
        try
        {
            AgentPluginCatalog catalog = await provider.GetInstalledCatalogAsync(read.Token);
            read.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRead(provider, generation)) return;
            string? selected = SelectedPlugin?.Id;
            _readingCatalog = true;
            try
            {
                _state.Marketplaces = catalog.Marketplaces;
                Marketplaces.Clear();
                foreach (var marketplace in catalog.Marketplaces) Marketplaces.Add(marketplace);
                SelectedMarketplace = Marketplaces.FirstOrDefault(item => item.Name == _state.SelectedMarketplace);
                if (provider.SupportsScopedBrowse)
                {
                    _state.Installed = catalog.Plugins;
                }
                else
                {
                    _state.Installed = catalog.Plugins.Where(item => item.IsInstalled).ToArray();
                    _state.BrowseCatalogs.Clear();
                    foreach (var group in catalog.Plugins.Where(item => !item.IsInstalled)
                                 .GroupBy(item => item.Marketplace, StringComparer.Ordinal))
                        _state.BrowseCatalogs[group.Key] = group.ToArray();
                }
                _state.IsLoaded = true;
            }
            finally { _readingCatalog = false; }
            NeedsRefresh = false;
            Filter(selected);
            Status = CoreTools.Translate("Loaded {0} installed plugins for {1}.", _state.Installed.Count, provider.Name);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            if (IsCurrentRead(provider, generation))
                Status = CoreTools.Translate("Plugin catalog loading cancelled.");
            if (throwOnError) throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (IsCurrentRead(provider, generation))
            {
                HasError = true;
                Status = CoreTools.Translate("Could not load installed plugins for {0}: {1}", provider.Name, ex.Message);
                AccessibilityAnnouncementService.Announce(Status, AutomationLiveSetting.Assertive);
            }
            if (throwOnError) throw;
        }
        finally { EndCatalogRead(read); }
    }

    private async Task BrowseMarketplaceAsync(string marketplaceName, bool forceRefresh, CancellationToken token)
    {
        if (IsAppHandoff || !CanWork || SelectedMarketplace?.Name != marketplaceName) return;
        var marketplace = Marketplaces.FirstOrDefault(item => item.Name == marketplaceName)
            ?? throw new InvalidOperationException(CoreTools.Translate("Select a marketplace first."));
        if (!forceRefresh && _state.BrowseCatalogs.ContainsKey(marketplace.Name))
        {
            Filter();
            return;
        }
        if (!_service.SupportsScopedBrowse)
        {
            if (_state.BrowseCatalogs.ContainsKey(marketplaceName)) return;
        }
        CancellationTokenSource read = BeginCatalogRead(token);
        IAgentPluginService provider = _service;
        int generation = _catalogGeneration;
        Status = CoreTools.Translate("Browsing {0}…", marketplace.Name);
        HasError = false;
        try
        {
            if (!forceRefresh && _catalogCache is not null)
            {
                var saved = await _catalogCache.ReadAsync(provider.CacheIdentity, marketplace, read.Token);
                if (saved is not null && IsCurrentRead(provider, generation))
                {
                    _state.BrowseCatalogs[marketplace.Name] = saved.Plugins;
                    Filter();
                    Status = CoreTools.Translate("Loaded {0} cached plugins from {1}.", saved.Plugins.Count, marketplace.Name);
                    return;
                }
            }
            var catalog = await provider.GetMarketplaceCatalogAsync(marketplace.Name, read.Token);
            read.Token.ThrowIfCancellationRequested();
            if (!IsCurrentRead(provider, generation)) return;
            ValidateMarketplaceCatalog(marketplace.Name, catalog);
            _state.BrowseCatalogs[marketplace.Name] = catalog.Plugins;
            if (_catalogCache is not null)
                await _catalogCache.StoreAsync(provider.CacheIdentity, marketplace, catalog.Plugins, read.Token);
            Filter();
            Status = CoreTools.Translate("Loaded {0} plugins from {1}.", catalog.Plugins.Count, marketplace.Name);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            if (IsCurrentRead(provider, generation))
                Status = CoreTools.Translate("Marketplace browsing cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (IsCurrentRead(provider, generation))
            {
                HasError = true;
                Status = CoreTools.Translate("Could not browse {0}: {1}", marketplace.Name, ex.Message);
                AccessibilityAnnouncementService.Announce(Status, AutomationLiveSetting.Assertive);
            }
        }
        finally { EndCatalogRead(read); }
    }

    private static void ValidateMarketplaceCatalog(string marketplace, AgentPluginCatalog catalog)
    {
        if (catalog.Plugins.Any(item => item is null || item.IsInstalled || item.Marketplace != marketplace))
            throw new InvalidDataException(CoreTools.Translate("The marketplace returned plugin entries with unexpected identities."));
    }

    private CancellationTokenSource BeginCatalogRead(CancellationToken token)
    {
        CancelCatalogRead();
        var read = CancellationTokenSource.CreateLinkedTokenSource(token);
        _catalogRead = read;
        _catalogGeneration++;
        IsLoadingCatalog = true;
        return read;
    }

    private bool IsCurrentRead(IAgentPluginService provider, int generation) =>
        !_stopped && ReferenceEquals(provider, _service) && generation == _catalogGeneration;

    private void CancelCatalogRead()
    {
        _catalogGeneration++;
        if (_catalogRead is not { } read) return;
        _catalogRead = null;
        read.Cancel();
        IsLoadingCatalog = false;
    }

    private void EndCatalogRead(CancellationTokenSource read)
    {
        if (ReferenceEquals(_catalogRead, read))
        {
            _catalogRead = null;
            IsLoadingCatalog = false;
        }
        read.Dispose();
    }

    [RelayCommand]
    private Task AddMarketplaceAsync() => !CanChange ? Task.CompletedTask : RunAsync(async token =>
    {
        if (!await _confirm(CoreTools.Translate(
                "Add this source for {0}? Only add sources you trust. Plugins can include executable hooks and MCP servers.", _service.Name) + Environment.NewLine + MarketplaceInput))
            return CoreTools.Translate("Cancelled");
        token.ThrowIfCancellationRequested();
        if (IsAppHandoff)
            return await _service.AddMarketplaceAsync(MarketplaceInput, token);
        var knownMarketplaces = Marketplaces.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        NeedsRefresh = true;
        string result = await _service.AddMarketplaceAsync(MarketplaceInput, token);
        MarketplaceInput = "";
        Query = "";
        InstalledOnly = false;
        await ReadInstalledCatalogAsync(token, throwOnError: true);
        SelectedMarketplace = Marketplaces.FirstOrDefault(item => !knownMarketplaces.Contains(item.Name))
            ?? SelectedMarketplace;
        if (_service.SupportsScopedBrowse && SelectedMarketplace is { } added && !knownMarketplaces.Contains(added.Name))
            await BrowseAddedMarketplaceAsync(added, token);
        AddMode = PluginAddMode.None;
        return CoreTools.Translate("Plugin marketplace added.") + Environment.NewLine + result;
    });

    private async Task BrowseAddedMarketplaceAsync(AgentPluginMarketplace marketplace, CancellationToken token)
    {
        try
        {
            var catalog = await _service.GetMarketplaceCatalogAsync(marketplace.Name, token);
            ValidateMarketplaceCatalog(marketplace.Name, catalog);
            _state.BrowseCatalogs[marketplace.Name] = catalog.Plugins;
            if (_catalogCache is not null)
                await _catalogCache.StoreAsync(_service.CacheIdentity, marketplace, catalog.Plugins, token);
            Filter();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The marketplace was added; browsing it can be retried from the browse prompt.
            Logger.Warn(ex);
        }
    }

    [RelayCommand]
    private void AllMarketplaces() => SelectedMarketplace = null;

    [RelayCommand]
    private Task RefreshMarketplaceAsync() => !CanChange ? Task.CompletedTask : RunAsync(async token =>
    {
        var marketplace = SelectedMarketplace ?? throw new InvalidOperationException(CoreTools.Translate("Select a marketplace first."));
        if (!marketplace.CanRefresh) throw new InvalidOperationException(CoreTools.Translate("This marketplace is managed by the agent and cannot be changed here."));
        NeedsRefresh = true;
        string result = await _service.UpdateMarketplaceAsync(marketplace.Name, token);
        var catalog = await _service.GetMarketplaceCatalogAsync(marketplace.Name, token);
        ValidateMarketplaceCatalog(marketplace.Name, catalog);
        _state.BrowseCatalogs[marketplace.Name] = catalog.Plugins;
        if (_catalogCache is not null)
            await _catalogCache.StoreAsync(_service.CacheIdentity, marketplace, catalog.Plugins, token);
        await ReadInstalledCatalogAsync(token, throwOnError: true);
        return CoreTools.Translate("Plugin marketplace refreshed.") + Environment.NewLine + result;
    });

    [RelayCommand]
    private Task RemoveMarketplaceAsync() => !CanChange ? Task.CompletedTask : RunAsync(async token =>
    {
        var marketplace = SelectedMarketplace ?? throw new InvalidOperationException(CoreTools.Translate("Select a marketplace first."));
        if (!marketplace.CanManage) throw new InvalidOperationException(CoreTools.Translate("This marketplace is managed by the agent and cannot be changed here."));
        string affected = string.Join(Environment.NewLine, _state.Installed
            .Where(item => item.IsInstalled && item.Marketplace == marketplace.Name).Select(item => item.Id));
        if (!await _confirm(_service.RemovalWarning
                + Environment.NewLine + marketplace.Name + Environment.NewLine + affected))
            return CoreTools.Translate("Cancelled");
        token.ThrowIfCancellationRequested();
        NeedsRefresh = true;
        string result = await _service.RemoveMarketplaceAsync(marketplace.Name, token);
        _catalogCache?.Forget(_service.CacheIdentity, marketplace);
        _state.BrowseCatalogs.Remove(marketplace.Name);
        await ReadInstalledCatalogAsync(token, throwOnError: true);
        return CoreTools.Translate("Plugin marketplace removed.") + Environment.NewLine + result;
    });

    [RelayCommand]
    private Task OpenMarketplaceInCopilotAppAsync() => OpenCopilotAppAsync(() =>
        _copilotApp.AddMarketplaceAsync(MarketplaceInput));

    [RelayCommand]
    private Task OpenPluginInCopilotAppAsync() => OpenCopilotAppAsync(() =>
        _copilotApp.PerformAsync(AddingPlugin ? PluginInput : SelectedPlugin?.Id ?? "", AgentPluginAction.Install));

    private Task OpenCopilotAppAsync(Func<Task<string>> open)
    {
        if (!CanOpenCopilotApp) return Task.CompletedTask;
        return RunAsync(async _ => await open());
    }

    [RelayCommand]
    private Task InstallByIdAsync() => CanChange ? ChangeAsync(PluginInput, AgentPluginAction.Install) : Task.CompletedTask;
    [RelayCommand]
    private Task InstallAsync() => CanInstall && SelectedPlugin is { } plugin
        ? ChangeAsync(plugin.Id, AgentPluginAction.Install) : Task.CompletedTask;
    [RelayCommand]
    private Task UpdateAsync() => CanUpdate && SelectedPlugin is { } plugin
        ? ChangeAsync(plugin.Id, AgentPluginAction.Update) : Task.CompletedTask;
    [RelayCommand]
    private Task EnableAsync() => CanEnable && SelectedPlugin is { } plugin
        ? ChangeAsync(plugin.Id, AgentPluginAction.Enable) : Task.CompletedTask;
    [RelayCommand]
    private Task DisableAsync() => CanDisable && SelectedPlugin is { } plugin
        ? ChangeAsync(plugin.Id, AgentPluginAction.Disable) : Task.CompletedTask;
    [RelayCommand]
    private Task UninstallAsync() => CanManage && SelectedPlugin is { IsInstalled: true } plugin
        ? ChangeAsync(plugin.Id, AgentPluginAction.Uninstall) : Task.CompletedTask;

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll) return;
        var plugins = UpdatablePlugins;
        await RunAsync(async token =>
        {
            if (!await _confirm(CoreTools.Translate("Run Update for these {0} installed plugins in {1}? Only the plugins shown by the current filters are included. This does not mean newer versions were detected.", plugins.Length, _service.Name)
                + Environment.NewLine + string.Join(Environment.NewLine, plugins.Select(item => item.Id))))
                return CoreTools.Translate("Cancelled");
            List<string> failures = [];
            List<string> output = [];
            int completed = 0;
            NeedsRefresh = true;
            foreach (var plugin in plugins)
            {
                token.ThrowIfCancellationRequested();
                Status = CoreTools.Translate("Updating {0} ({1}/{2})", plugin.Name, completed + failures.Count + 1, plugins.Length);
                try
                {
                    string result = await _service.PerformAsync(plugin.Id, AgentPluginAction.Update, token);
                    token.ThrowIfCancellationRequested();
                    completed++;
                    output.Add(plugin.Id + ": " + result);
                    Activity = string.Join(Environment.NewLine, output);
                    ShowRestartNotice = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex);
                    failures.Add(plugin.Id + ": " + ex.Message);
                }
            }
            await ReadInstalledCatalogAsync(token, throwOnError: true);
            if (failures.Count > 0)
                throw new InvalidOperationException(CoreTools.Translate("{0} plugins updated; {1} failed. {2}", completed, failures.Count,
                    string.Join(Environment.NewLine, failures)));
            return CoreTools.Translate("Updated {0} plugins", completed) + Environment.NewLine + string.Join(Environment.NewLine, output);
        });
    }

    private Task ChangeAsync(string id, AgentPluginAction action) => RunAsync(async token =>
    {
        string message = action == AgentPluginAction.Uninstall
            ? CoreTools.Translate("Uninstall this plugin for {0}? The agent may also delete its saved data, options and secrets.", _service.Name)
            : CoreTools.Translate("Change this plugin for {0}? Plugins can run hooks, commands and MCP servers. Only install plugins you trust.", _service.Name);
        if (!await _confirm(message + Environment.NewLine + id)) return CoreTools.Translate("Cancelled");
        token.ThrowIfCancellationRequested();
        if (IsAppHandoff)
            return await _service.PerformAsync(id, action, token);
        NeedsRefresh = true;
        string result = await _service.PerformAsync(id, action, token);
        await ReadInstalledCatalogAsync(token, throwOnError: true);
        ShowRestartNotice = true;
        AddMode = PluginAddMode.None;
        return CoreTools.Translate("Plugin operation completed.") + Environment.NewLine + result
            + Environment.NewLine + _service.RestartNotice;
    });

    private async Task RunAsync(Func<CancellationToken, Task<string>> action)
    {
        if (!CanWork) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        HasError = false;
        Status = CoreTools.Translate("Working…");
        try
        {
            string result = await action(operation.Token);
            if (!_stopped)
            {
                Activity = result;
                Status = result.Split('\n')[0].TrimEnd('\r');
                AccessibilityAnnouncementService.Announce(Status);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_stopped) Status = NeedsRefresh
                ? CoreTools.Translate("Cancelled. Reload the list to verify the current installation state.")
                : CoreTools.Translate("Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (!_stopped)
            {
                HasError = true;
                Status = CoreTools.Translate("Could not manage {0} plugins: {1}", _service.Name, ex.Message);
                Activity = Status;
                AccessibilityAnnouncementService.Announce(Status, AutomationLiveSetting.Assertive);
            }
        }
        finally { _operation = null; IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        bool cancelledCatalog = _operation is null && IsLoadingCatalog;
        _operation?.Cancel();
        CancelCatalogRead();
        if (!_stopped && cancelledCatalog) Status = CoreTools.Translate("Plugin catalog loading cancelled.");
    }
    public void Stop()
    {
        _stopped = true;
        Cancel();
        NotifyActions();
    }
}
