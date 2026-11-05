using System.Collections.ObjectModel;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public enum AgentExtensionSection { Skills, Plugins }
public enum SkillLibraryView { Discover, Installed, Updates }

public partial class AgentSkillsViewModel : ViewModelBase
{
    private readonly AgentSkillsService _service;
    private readonly Func<SkillSourceLocator, Task>? _prepareSource;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly Func<IReadOnlyList<SkillItem>, IReadOnlyList<AgentInfo>, IReadOnlyList<string>?,
        Task<AgentSkillsInstallSelection?>> _selectInstallTargets;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly List<SkillItem> _results = [];
    private readonly Dictionary<string, SkillItem> _installedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<AgentInfo> _supportedAgents;
    private readonly Dictionary<string, SkillItem> _knownUpdates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _backgroundOperation;
    private CancellationTokenSource? _previewOperation;
    private bool _stopped;
    private bool _loaded;
    private string _notifiedUpdates = "";
    internal Action<int>? UpdatesAvailable { get; set; }
    internal AgentSkillsService Service => _service;
    public ObservableCollection<SkillItem> Skills { get; } = [];
    public ObservableCollection<string> Sources { get; } = [CoreTools.Translate("All sources")];
    public ObservableCollection<string> Agents { get; } = [CoreTools.Translate("All agents")];
    public ObservableCollection<AgentLibraryToolOption> ToolOptions { get; } = [];
    public ObservableCollection<AgentLibraryItem> Items { get; } = [];
    public IReadOnlyList<AgentLibraryTypeFilter> ContentTypes { get; } = Enum.GetValues<AgentLibraryTypeFilter>();
    [ObservableProperty] private SkillLibraryView _view;
    [ObservableProperty] private AgentExtensionSection _section;
    [ObservableProperty] private bool _showDetails;
    [ObservableProperty] private AgentLibraryView _libraryView;
    [ObservableProperty] private AgentLibraryTypeFilter _contentType;
    [ObservableProperty] private AgentLibraryToolOption? _selectedTool;
    [ObservableProperty] private AgentLibraryItem? _selectedItem;
    [ObservableProperty] private bool _addSourceExpanded;
    [ObservableProperty] private bool _searchPublicCatalog;
    [ObservableProperty] private string _catalogQuery = "";
    public int SectionIndex { get => (int)Section; set => Section = (AgentExtensionSection)value; }
    public int SkillViewIndex { get => (int)View; set => View = (SkillLibraryView)value; }
    // Retain the existing notification/navigation entry point without mixing sections in the UI.
    public int Mode
    {
        get => ContentType == AgentLibraryTypeFilter.Plugins ? 3 : (int)LibraryView;
        set
        {
            ContentType = value == 3 ? AgentLibraryTypeFilter.Plugins : AgentLibraryTypeFilter.Skills;
            LibraryView = value == 3 ? AgentLibraryView.Discover : (AgentLibraryView)value;
        }
    }
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _sourceFilter;
    [ObservableProperty] private string? _agentFilter;
    [ObservableProperty] private SkillItem? _selectedSkill;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _backgroundStatus = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _showSources;
    [ObservableProperty] private int? _installedCount;
    [ObservableProperty] private int? _updateCount;
    [ObservableProperty] private string _sourceInput = "";
    [ObservableProperty] private string _sourceInputMessage = "";
    [ObservableProperty] private SkillPreview? _skillPreview;
    [ObservableProperty] private string _previewError = "";
    [ObservableProperty] private bool _isLoadingPreview;
    [ObservableProperty] private bool _isChoosingTargets;
    [ObservableProperty] private bool _needsRefresh;
    public bool IsDiscover => View == SkillLibraryView.Discover;
    public bool IsInstalled => View == SkillLibraryView.Installed;
    public bool IsUpdates => View == SkillLibraryView.Updates;
    public bool IsAgentPlugins => Section == AgentExtensionSection.Plugins;
    public bool ShowSkills => !IsAgentPlugins && !ShowSources;
    public bool ShowLibrary => ShowSkills && !ShowDetails;
    public bool IsLibraryView => LibraryView != AgentLibraryView.Sources;
    public bool IsDiscoverView => LibraryView == AgentLibraryView.Discover;
    public bool IsInstalledView => LibraryView == AgentLibraryView.Installed;
    public bool IsUpdatesView => LibraryView == AgentLibraryView.Updates;
    public bool IsSourcesView => LibraryView == AgentLibraryView.Sources;
    public bool ShowSkillSources => ContentType != AgentLibraryTypeFilter.Plugins;
    public bool ShowPluginMarketplaces => ContentType != AgentLibraryTypeFilter.Skills;
    public bool HasSelectedPluginProvider => SelectedTool?.PluginProvider is not null;
    public bool ShowPublicDiscovery => IsDiscoverView && ContentType != AgentLibraryTypeFilter.Plugins
        && PublicDiscoveryEnabled;
    public bool HasLibraryItems => Items.Count > 0;
    public bool HasLibraryError => ContentType == AgentLibraryTypeFilter.Skills ? HasError
        : ContentType == AgentLibraryTypeFilter.Plugins ? AgentPlugins.HasError : HasError || AgentPlugins.HasError;
    public bool ShowLibraryEmpty => IsLibraryView && !HasLibraryItems && !IsLibraryBusy && !HasLibraryError;
    public bool IsLibraryBusy => IsBusy || AgentPlugins.IsWorking;
    public bool IsAllTools => SelectedTool is { AgentId: null, PluginProvider: null };
    public bool IsSkillDetails => SelectedItem?.Skill is not null;
    public bool IsPluginDetails => SelectedItem?.Plugin is not null;
    public int LibraryViewIndex { get => (int)LibraryView; set => LibraryView = (AgentLibraryView)value; }
    public bool CanChangeLibrary => ContentType switch
    {
        AgentLibraryTypeFilter.Skills => CanChange,
        AgentLibraryTypeFilter.Plugins => AgentPlugins.CanChange && !AgentPlugins.HasError,
        _ => CanNavigate && !NeedsRefresh && !HasLibraryError
            && !AgentPlugins.IsLoadingCatalog && !AgentPlugins.NeedsRefresh,
    };
    public bool CanAddSkillSource => IsDiscoverView && ContentType != AgentLibraryTypeFilter.Plugins && CanAddSource;
    public bool CanAddPluginMarketplace => IsDiscoverView && ContentType != AgentLibraryTypeFilter.Skills
        && SelectedTool?.PluginProvider is not null && AgentPlugins.CanChange;
    public bool ShowPluginBrowseActions => IsDiscoverView && ContentType != AgentLibraryTypeFilter.Skills
        && SelectedTool?.PluginProvider is not null;
    public bool ShowLoadMorePlugins => IsDiscoverView && ContentType != AgentLibraryTypeFilter.Skills
        && SelectedTool?.PluginProvider is not null && AgentPlugins.CanLoadMore;
    public bool ShowPluginUpdateOperation => IsInstalledView && ContentType != AgentLibraryTypeFilter.Skills
        && SelectedTool?.PluginProvider is { SupportsUpdate: true };
    public string LibraryResultCount => Items.Count == 1
        ? CoreTools.Translate("1 item") : CoreTools.Translate("{0} items", Items.Count);
    public string LibraryEmptyTitle => LibraryView switch
    {
        _ when HasLibraryError => CoreTools.Translate("Could not load items"),
        AgentLibraryView.Installed => CoreTools.Translate("No items installed for these filters"),
        AgentLibraryView.Updates => CoreTools.Translate("No known updates for these filters"),
        _ when NeedsSetup => CoreTools.Translate("Add your first skill source"),
        _ when IsDiscoverView && !_loaded && HasSources => CoreTools.Translate("Browse your skill sources"),
        _ => CoreTools.Translate("No items match these filters"),
    };
    public string LibraryEmptyDescription => HasLibraryError
        ? ContentType == AgentLibraryTypeFilter.Plugins ? AgentPlugins.Status : Status
        : NeedsSetup
            ? CoreTools.Translate("Add a source to discover skills. Nothing is installed until you choose Install.")
            : IsDiscoverView && !_loaded && HasSources
                ? CoreTools.Translate("Refresh to load skills from your configured sources.")
        : LibraryView == AgentLibraryView.Updates
            ? CoreTools.Translate("Plugins do not report detected updates here; use the provider's update operation when available.")
            : CoreTools.Translate("Try another tool or type filter, or clear the search.");
    public string PluginUpdateLabel => CoreTools.Translate("Update installed plugins");
    public string LibraryStatus => AgentPlugins.Status.Length > 0 && ContentType != AgentLibraryTypeFilter.Skills
        ? AgentPlugins.Status : Status;
    public bool HasLibraryActivity => Output.Length > 0 || AgentPlugins.HasActivity;
    public string LibraryActivity => Output.Length > 0 ? Output : AgentPlugins.Activity;
    public bool ShowSourceEditor => IsDiscover && (AddSourceExpanded || NeedsSetup);
    public bool PublicDiscoveryEnabled => _service.SkillsShDiscoveryEnabled;
    public bool CanNavigate => !_stopped && !IsBusy && !IsChoosingTargets && !AgentPlugins.IsBusy;
    public bool CanChange => CanNavigate && !HasError && !NeedsRefresh;
    public bool ShowRefresh => !IsDiscover || !SearchPublicCatalog;
    public string BackLabel => IsUpdates ? CoreTools.Translate("Back to Updates")
        : IsInstalled ? CoreTools.Translate("Back to Installed") : CoreTools.Translate("Back to Discover");
    public string ResultCount => CoreTools.Translate("{0} skills", Skills.Count);
    public AgentPluginsViewModel AgentPlugins { get; } = new();
    public bool HasSelection => SelectedSkill is not null;
    public bool CanOpenSource => SelectedSkill?.Source?.Url.Scheme == "https";
    public bool HasOutput => Output.Length > 0;
    public bool ShowEmpty => Skills.Count == 0 && !IsBusy;
    public bool ShowInstallAction => IsDiscover && SelectedSkill?.CanInstallFromDiscover == true;
    public bool ShowUpdateAction => SelectedSkill is { HasUpdate: true };
    public bool ShowRemoveAction => SelectedSkill is { IsInstalled: true };
    public bool CanInstall => CanNavigate && !HasError && !NeedsRefresh && ShowInstallAction;
    public bool CanRemove => CanNavigate && !HasError && !NeedsRefresh && ShowRemoveAction;
    public bool CanUpdate => CanNavigate && !HasError && !NeedsRefresh && ShowUpdateAction;
    public bool CanRetry => CanNavigate && (HasError || NeedsRefresh);
    public bool HasSourceInputMessage => SourceInputMessage.Length > 0;
    public bool HasPreview => SkillPreview is not null;
    public bool HasPreviewError => PreviewError.Length > 0;
    public bool CanLoadPreview => !_stopped && !IsBusy && !IsLoadingPreview && IsDiscover && SelectedSkill is not null;
    public bool HasPreviewWarnings => SkillPreview is { } preview
        && (preview.ContainsScripts || preview.ContainsExecutables || preview.AllowedTools.Count > 0);
    public bool HasPreviewLicense => !string.IsNullOrWhiteSpace(SkillPreview?.License);
    public bool HasPreviewCompatibility => !string.IsNullOrWhiteSpace(SkillPreview?.Compatibility);
    public string PreviewLicense => SkillPreview?.License is { } license
        ? CoreTools.Translate("License: {0}", license) : "";
    public string PreviewCompatibility => SkillPreview?.Compatibility is { } compatibility
        ? CoreTools.Translate("Compatibility: {0}", compatibility) : "";
    public string PreviewWarnings => SkillPreview is not { } preview ? ""
        : string.Join(Environment.NewLine, new[]
        {
            preview.ContainsScripts ? CoreTools.Translate("Contains files under scripts/.") : null,
            preview.ContainsExecutables ? CoreTools.Translate("Contains executable or script files.") : null,
            preview.AllowedTools.Count > 0
                ? CoreTools.Translate("Requests tool access: {0}", string.Join(", ", preview.AllowedTools)) : null,
        }.Where(message => message is not null));
    public bool CanAddSource => !_stopped && !IsBusy && SourceInput.Trim().Length > 0;
    private SkillItem[] InstallableSkills => Skills.Where(skill => skill.CanInstallFromDiscover).ToArray();
    private string? SingleShownSource => InstallableSkills.Select(skill => skill.SourceName).Distinct().ToArray() is [var only] ? only : null;
    public bool ShowInstallAll => IsDiscover && ContentType != AgentLibraryTypeFilter.Plugins
        && InstallableSkills.Length > 1 && SingleShownSource is not null;
    public bool CanInstallAll => CanChangeLibrary && ShowInstallAll;
    public string InstallAllLabel => CoreTools.Translate("Install all {0}", InstallableSkills.Length);
    private SkillItem[] UpdatableSkills => Skills.Where(skill => skill.HasUpdate).ToArray();
    public bool ShowUpdateAll => IsLibraryView && ContentType != AgentLibraryTypeFilter.Plugins
        && !ShowDetails && UpdatableSkills.Length > 0;
    public bool CanUpdateAll => CanChangeLibrary && ShowUpdateAll;
    public string UpdateAllLabel => CoreTools.Translate("Update all {0}", UpdatableSkills.Length);
    public bool ShowCachedSourceNotice => IsDiscover && !SearchPublicCatalog && _loaded && !HasError;
    public string InstalledHeader => InstalledCount is { } count
        ? CoreTools.Translate("Installed ({0})", count) : CoreTools.Translate("Installed");
    public string UpdatesHeader => UpdateCount is { } count
        ? CoreTools.Translate("Updates ({0})", count) : CoreTools.Translate("Updates");
    public string SearchLabel => CoreTools.Translate("Filter skills");
    public string RefreshLabel => LibraryView switch
    {
        AgentLibraryView.Discover => CoreTools.Translate("Refresh sources"),
        AgentLibraryView.Updates => CoreTools.Translate("Check for updates"),
        _ => CoreTools.Translate("Reload list"),
    };
    private static bool HasSources => AgentSkillsService.GetConfiguredSources().Count > 0;
    public bool NeedsSetup => IsDiscover && !HasSources && !SearchPublicCatalog;
    public string EmptyTitle => HasError ? CoreTools.Translate("Could not load skills")
        : NeedsSetup ? CoreTools.Translate("Add skills from a repository")
        : !_loaded && IsDiscover ? (HasSources ? CoreTools.Translate("Browse your sources") : CoreTools.Translate("Search skills.sh"))
        : _results.Count > 0 || (IsDiscover && Query.Length > 0) ? CoreTools.Translate("No matching skills")
        : IsInstalled ? CoreTools.Translate("No skills installed")
        : IsUpdates ? CoreTools.Translate("No skill updates found")
        : CoreTools.Translate("No skills found");
    public string EmptyDescription => HasError ? Status
        : NeedsSetup ? CoreTools.Translate("Add a repository or website to browse its skills. Nothing is installed until you choose Install.")
        : IsDiscover && SearchPublicCatalog && !_loaded ? CoreTools.Translate("Enter at least two characters and choose Search skills.sh. Searching the public catalog does not install anything.")
        : !_loaded && IsDiscover ? (HasSources
            ? CoreTools.Translate("Refresh to list the skills your sources offer.")
            : CoreTools.Translate("Type a name and press Enter to search the public skills.sh catalog, or add a repository above."))
        : _results.Count > 0 ? CoreTools.Translate("Try a different filter or clear the source and agent filters.")
        : IsInstalled ? CoreTools.Translate("Skills installed for your user account will appear here. Open Discover to install your first skill.")
        : IsUpdates ? CoreTools.Translate("No updates were found in your configured sources. Refresh to check again.")
        : CoreTools.Translate("Your sources do not offer skills matching this filter. skills.sh searches need at least two characters.");
    public string InstallationSummary => AgentSkillsService.TargetAgents is { Count: > 0 } targets
        ? CoreTools.Translate("Choose the agents for this install. Current default: {0}", string.Join(", ", targets))
        : CoreTools.Translate("Choose the agents for this install. Detected agents and the shared skills folder are selected by default.");

    public AgentSkillsViewModel() : this(new AgentSkillsService(), async source =>
    {
        if (AgentSkillsDependencies.ForSource(source) is { } dependency)
            await AvaloniaBootstrapper.PromptForMissingDependencyAsync(dependency);
    }, AgentSkillsInstallDialog.ShowAsync)
    { }

    internal AgentSkillsViewModel(AgentSkillsService service, Func<SkillSourceLocator, Task>? prepareSource = null,
        Func<IReadOnlyList<SkillItem>, IReadOnlyList<AgentInfo>, IReadOnlyList<string>?,
            Task<AgentSkillsInstallSelection?>>? selectInstallTargets = null,
        Func<string, Task<bool>>? confirm = null)
    {
        _service = service;
        _prepareSource = prepareSource;
        _selectInstallTargets = selectInstallTargets ?? AgentSkillsInstallDialog.ShowAsync;
        _confirm = confirm ?? ConfirmationDialog.ShowAsync;
        _sourceFilter = Sources[0];
        _agentFilter = Agents[0];
        Skills.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ResultCount));
            NotifyActions();
        };
        AgentPlugins.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentPluginsViewModel.IsBusy)
                or nameof(AgentPluginsViewModel.IsLoadingCatalog)
                or nameof(AgentPluginsViewModel.Plugins)
                or nameof(AgentPluginsViewModel.SelectedProvider)
                or nameof(AgentPluginsViewModel.Marketplaces)
                or nameof(AgentPluginsViewModel.SelectedPlugin)
                or nameof(AgentPluginsViewModel.CanLoadMore)
                or nameof(AgentPluginsViewModel.Status)
                or nameof(AgentPluginsViewModel.HasError)
                or nameof(AgentPluginsViewModel.Activity)
                or nameof(AgentPluginsViewModel.HasActivity))
                RefreshLibraryItems();
            if (e.PropertyName == nameof(AgentPluginsViewModel.CanLoadMore))
                OnPropertyChanged(nameof(ShowLoadMorePlugins));
            if (e.PropertyName is nameof(AgentPluginsViewModel.IsBusy) or nameof(AgentPluginsViewModel.IsLoadingCatalog))
            {
                OnPropertyChanged(nameof(IsLibraryBusy));
                OnPropertyChanged(nameof(CanNavigate));
                OnPropertyChanged(nameof(CanChangeLibrary));
                OnPropertyChanged(nameof(CanAddPluginMarketplace));
                OnPropertyChanged(nameof(ShowLibraryEmpty));
            }
            if (e.PropertyName is nameof(AgentPluginsViewModel.Status) or nameof(AgentPluginsViewModel.HasError))
            {
                OnPropertyChanged(nameof(LibraryStatus));
                OnPropertyChanged(nameof(LibraryEmptyTitle));
                OnPropertyChanged(nameof(LibraryEmptyDescription));
                OnPropertyChanged(nameof(HasLibraryError));
                OnPropertyChanged(nameof(CanChangeLibrary));
            }
            if (e.PropertyName == nameof(AgentPluginsViewModel.NeedsRefresh))
                OnPropertyChanged(nameof(CanChangeLibrary));
            if (e.PropertyName is nameof(AgentPluginsViewModel.Activity) or nameof(AgentPluginsViewModel.HasActivity))
            {
                OnPropertyChanged(nameof(LibraryActivity));
                OnPropertyChanged(nameof(HasLibraryActivity));
            }
        };
        Skills.CollectionChanged += (_, _) => RefreshLibraryItems();
        _supportedAgents = _service.GetSupportedAgents();
        ToolOptions.Add(new(CoreTools.Translate("All tools"), null, null));
        SyncToolOptions();
        SelectedTool = ToolOptions[0];
    }

    private static string SkillKey(SkillItem skill) => $"{skill.Id}\0{skill.SourceName}";

    // Only list tools that matter here; the engine supports dozens of agents that most users never have.
    // Options are only ever added, so the current selection never disappears from under the user.
    private void SyncToolOptions()
    {
        var inUse = new HashSet<string>(AgentSkillsService.TargetAgents, StringComparer.OrdinalIgnoreCase);
        foreach (var skill in _installedSkills.Values) inUse.UnionWith(skill.Agents ?? []);
        List<AgentLibraryToolOption> relevant = [];
        foreach (var agent in _supportedAgents)
        {
            var provider = AgentPlugins.Providers.FirstOrDefault(item =>
                string.Equals(item.Name, agent.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (provider is not null || agent.IsDetected || inUse.Contains(agent.Id))
                relevant.Add(new(agent.DisplayName, agent.Id, provider));
        }
        foreach (var provider in AgentPlugins.Providers.Where(provider =>
                     !relevant.Any(option => ReferenceEquals(option.PluginProvider, provider))))
            relevant.Add(new(provider.Name, null, provider));
        foreach (var option in relevant.Where(option => !ToolOptions.Contains(option)))
        {
            int index = 1;
            while (index < ToolOptions.Count && string.Compare(ToolOptions[index].Name, option.Name,
                       StringComparison.CurrentCultureIgnoreCase) < 0)
                index++;
            ToolOptions.Insert(index, option);
        }
    }

    partial void OnLibraryViewChanged(AgentLibraryView value)
    {
        if (value != AgentLibraryView.Sources)
        {
            View = value switch
            {
                AgentLibraryView.Installed => SkillLibraryView.Installed,
                AgentLibraryView.Updates => SkillLibraryView.Updates,
                _ => SkillLibraryView.Discover,
            };
            AgentPlugins.View = value == AgentLibraryView.Installed
                ? PluginLibraryView.Installed : PluginLibraryView.Browse;
        }
        foreach (string name in new[]
                 {
                     nameof(LibraryViewIndex), nameof(Mode), nameof(HasLibraryError), nameof(LibraryEmptyTitle),
                     nameof(LibraryEmptyDescription),
                     nameof(IsLibraryView), nameof(IsDiscoverView), nameof(IsInstalledView), nameof(IsUpdatesView),
                     nameof(IsSourcesView), nameof(ShowPluginBrowseActions), nameof(ShowPluginUpdateOperation),
                     nameof(ShowLoadMorePlugins), nameof(ShowInstallAll), nameof(ShowPublicDiscovery),
                     nameof(CanAddSkillSource), nameof(CanAddPluginMarketplace), nameof(ShowLibraryEmpty),
                     nameof(CanChangeLibrary), nameof(ShowUpdateAll),
                 })
            OnPropertyChanged(name);
        RefreshLibraryItems();
    }

    partial void OnContentTypeChanged(AgentLibraryTypeFilter value)
    {
        foreach (string name in new[]
                 {
                     nameof(HasLibraryError), nameof(LibraryEmptyTitle), nameof(LibraryEmptyDescription),
                     nameof(ShowPluginBrowseActions), nameof(ShowPluginUpdateOperation), nameof(CanAddSkillSource),
                     nameof(CanAddPluginMarketplace), nameof(ShowLibraryEmpty), nameof(ShowSkillSources),
                     nameof(ShowPluginMarketplaces), nameof(ShowLoadMorePlugins), nameof(CanChangeLibrary),
                     nameof(ShowUpdateAll), nameof(ShowInstallAll), nameof(ShowPublicDiscovery),
                 })
            OnPropertyChanged(name);
        RefreshLibraryItems();
    }

    partial void OnSelectedToolChanged(AgentLibraryToolOption? value)
    {
        if (value?.PluginProvider is { } provider && !ReferenceEquals(AgentPlugins.SelectedProvider, provider))
            AgentPlugins.SelectedProvider = provider;
        OnPropertyChanged(nameof(CanAddPluginMarketplace));
        OnPropertyChanged(nameof(ShowPluginUpdateOperation));
        OnPropertyChanged(nameof(IsAllTools));
        OnPropertyChanged(nameof(ShowPluginBrowseActions));
        OnPropertyChanged(nameof(ShowLoadMorePlugins));
        OnPropertyChanged(nameof(HasSelectedPluginProvider));
        OnPropertyChanged(nameof(ShowPublicDiscovery));
        RefreshLibraryItems();
    }

    partial void OnSelectedItemChanged(AgentLibraryItem? value)
    {
        if (value?.Skill is { } skill)
            SelectedSkill = skill;
        else if (value?.Plugin is { } plugin)
            AgentPlugins.SelectedPlugin = plugin;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsSkillDetails));
        OnPropertyChanged(nameof(IsPluginDetails));
        OnPropertyChanged(nameof(CanChangeLibrary));
    }

    private void RefreshLibraryItems()
    {
        string? selectedKey = SelectedItem?.Key;
        string query = Query.Trim();
        IEnumerable<AgentLibraryItem> skillItems = [];
        IEnumerable<AgentLibraryItem> pluginItems = [];
        if (LibraryView != AgentLibraryView.Sources && ContentType != AgentLibraryTypeFilter.Plugins)
        {
            skillItems = Skills
                .Where(skill => LibraryView switch
                {
                    AgentLibraryView.Installed => skill.IsInstalled,
                    AgentLibraryView.Updates => skill.HasUpdate,
                    _ => true,
                })
                .Where(skill => IsAllTools || SelectedTool?.AgentId is { } agentId
                    && skill.Agents?.Contains(agentId, StringComparer.OrdinalIgnoreCase) == true)
                .Where(skill => MatchesQuery(skill.Name, skill.Description, skill.SourceName, query))
                .Select(skill => new AgentLibraryItem(
                    $"skill:{SkillKey(skill)}", skill.Name, skill.Description, AgentLibraryItemType.Skill,
                    skill.SourceName,
                    skill.HasUpdate ? CoreTools.Translate("Update available")
                        : skill.IsInstalled ? CoreTools.Translate("Installed") : "",
                    skill.IsInstalled ? skill.AgentNames : "",
                    skill.IsInstalled, skill.HasUpdate, skill.CanInstallFromDiscover, skill.HasUpdate,
                    skill, null, null));
        }
        if (LibraryView != AgentLibraryView.Sources && LibraryView != AgentLibraryView.Updates
            && ContentType != AgentLibraryTypeFilter.Skills)
        {
            pluginItems = AgentPlugins.GetKnownPlugins()
                .Where(entry => IsAllTools || SelectedTool?.PluginProvider is { } provider
                    && ReferenceEquals(provider, entry.Provider))
                .Select(entry => (entry.Provider, entry.Plugin))
                .Where(entry => LibraryView != AgentLibraryView.Installed || entry.Plugin.IsInstalled)
                .Where(entry => MatchesQuery(entry.Plugin.Name, entry.Plugin.Description,
                    entry.Plugin.Marketplace, query))
                .Select(entry => new AgentLibraryItem(
                    $"plugin:{entry.Provider.CacheIdentity}:{entry.Plugin.Id}", entry.Plugin.Name,
                    entry.Plugin.Description, AgentLibraryItemType.Plugin,
                    string.IsNullOrEmpty(entry.Plugin.Marketplace) ? entry.Provider.Name : entry.Plugin.Marketplace,
                    entry.Plugin.Status,
                    entry.Plugin.IsInstalled ? entry.Provider.Name : "",
                    entry.Plugin.IsInstalled, false, entry.Plugin.ShowInstall,
                    entry.Plugin.IsInstalled && entry.Plugin.CanManage && entry.Provider.SupportsUpdate,
                    null, entry.Plugin, entry.Provider));
        }

        var merged = skillItems.Concat(pluginItems)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Type)
            .ToArray();
        Items.Clear();
        foreach (var item in merged) Items.Add(item);
        SelectedItem = Items.FirstOrDefault(item => item.Key == selectedKey);
        OnPropertyChanged(nameof(HasLibraryItems));
        OnPropertyChanged(nameof(ShowLibraryEmpty));
        OnPropertyChanged(nameof(LibraryResultCount));
    }

    private static bool MatchesQuery(string name, string description, string source, string query) =>
        query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || source.Contains(query, StringComparison.OrdinalIgnoreCase);

    public async Task LoadLibraryViewAsync()
    {
        if (IsBusy) return;
        SyncToolOptions();
        if (LibraryView == AgentLibraryView.Sources)
        {
            if (SelectedTool?.PluginProvider is not null)
            {
                await AgentPlugins.LoadInstalledCatalogAsync();
            }
            return;
        }
        await LoadCurrentModeAsync();
        if (ContentType == AgentLibraryTypeFilter.Skills)
        {
            RefreshLibraryItems();
            return;
        }
        if (LibraryView == AgentLibraryView.Discover && IsAllTools)
        {
            await AgentPlugins.LoadInstalledForAllProvidersAsync();
            return;
        }
        if (LibraryView == AgentLibraryView.Installed && IsAllTools)
        {
            await AgentPlugins.LoadInstalledForAllProvidersAsync();
            return;
        }
        if (SelectedTool?.AgentId is not null && SelectedTool.PluginProvider is null) return;
        if (SelectedTool?.PluginProvider is null) return;
        await AgentPlugins.LoadInstalledCatalogAsync();
        RefreshLibraryItems();
    }

    [RelayCommand]
    private void OpenLibraryItem(AgentLibraryItem? item)
    {
        if (item is null || !Items.Contains(item) || IsBusy) return;
        SelectedItem = item;
        if (item.Skill is { } skill)
        {
            SelectedSkill = skill;
            ShowDetails = true;
            return;
        }
        if (item.Plugin is { } plugin && item.PluginProvider is { } provider
            && AgentPlugins.Providers.Contains(provider))
        {
            AgentPlugins.SelectedProvider = provider;
            AgentPlugins.SelectedPlugin = plugin;
            ShowDetails = true;
        }
    }

    [RelayCommand]
    private Task InstallLibraryItemAsync(AgentLibraryItem? item) => item?.Skill is { } skill
        ? InstallSkillCommand.ExecuteAsync(skill)
        : item?.Plugin is { } plugin && item.PluginProvider is { } provider
            ? AgentPlugins.ChangeKnownItemAsync(provider, plugin, AgentPluginAction.Install)
            : Task.CompletedTask;

    [RelayCommand]
    private Task UpdateLibraryItemAsync(AgentLibraryItem? item) => item?.Skill is { } skill
        ? UpdateSkillCommand.ExecuteAsync(skill)
        : item?.Plugin is { } plugin && item.PluginProvider is { } provider
            ? AgentPlugins.ChangeKnownItemAsync(provider, plugin, AgentPluginAction.Update)
            : Task.CompletedTask;

    [RelayCommand]
    private Task ReloadLibraryAsync()
    {
        if (ContentType == AgentLibraryTypeFilter.Skills)
            return RefreshCommand.ExecuteAsync(null);
        if (ContentType == AgentLibraryTypeFilter.Plugins)
            return AgentPlugins.RefreshCommand.ExecuteAsync(null);
        return Task.WhenAll(RefreshCommand.ExecuteAsync(null), ReloadKnownPluginsAsync());
    }

    private async Task ReloadKnownPluginsAsync()
    {
        if (IsAllTools)
            await AgentPlugins.RefreshInstalledForAllProvidersAsync();
        else if (SelectedTool?.PluginProvider is not null)
            await AgentPlugins.RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task ToggleLibraryItemAsync(AgentLibraryItem? item) => item?.Plugin is { } plugin
        && item.PluginProvider is { } provider
        ? AgentPlugins.ChangeKnownItemAsync(provider, plugin,
            plugin.Enabled ? AgentPluginAction.Disable : AgentPluginAction.Enable)
        : Task.CompletedTask;

    [RelayCommand]
    private Task UninstallLibraryItemAsync(AgentLibraryItem? item) => item?.Plugin is { } plugin
        && item.PluginProvider is { } provider
        ? AgentPlugins.ChangeKnownItemAsync(provider, plugin, AgentPluginAction.Uninstall)
        : Task.CompletedTask;

    private void RefreshDiscoverStates()
    {
        for (int i = 0; i < _results.Count; i++)
        {
            var skill = _results[i];
            bool isInstalled = _installedSkills.TryGetValue(SkillKey(skill), out var installed);
            bool hasUpdate = _knownUpdates.TryGetValue(SkillKey(skill), out var update);
            _results[i] = skill with
            {
                IsInstalled = isInstalled,
                HasUpdate = hasUpdate,
                Agents = installed?.Agents,
                Version = installed?.Version ?? skill.Version,
                NewVersion = update?.NewVersion,
            };
        }
        if (IsDiscover) ApplyFilters();
    }

    partial void OnViewChanged(SkillLibraryView value)
    {
        _operation?.Cancel();
        _previewOperation?.Cancel();
        ShowSources = false;
        _loaded = false;
        SelectedSkill = null;
        ShowDetails = false;
        _results.Clear();
        Query = "";
        ResetFilters();
        Skills.Clear();
        SkillPreview = null;
        PreviewError = "";
        Status = "";
        HasError = false;
        foreach (string property in new[] { nameof(IsDiscover), nameof(IsInstalled), nameof(IsUpdates), nameof(Mode), nameof(SkillViewIndex),
                     nameof(SearchLabel), nameof(RefreshLabel), nameof(BackLabel), nameof(ShowSourceEditor), nameof(ShowRefresh) })
            OnPropertyChanged(property);
        NotifyEmpty();
        NotifyActions();
        RefreshLibraryItems();
    }

    partial void OnSectionChanged(AgentExtensionSection value)
    {
        _previewOperation?.Cancel();
        foreach (string name in new[] { nameof(SectionIndex), nameof(Mode), nameof(IsAgentPlugins), nameof(ShowSkills), nameof(ShowLibrary) })
            OnPropertyChanged(name);
    }
    partial void OnShowDetailsChanged(bool value)
    {
        if (!value) _previewOperation?.Cancel();
        OnPropertyChanged(nameof(ShowLibrary));
        NotifyActions();
    }
    partial void OnAddSourceExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowSourceEditor));
    partial void OnSearchPublicCatalogChanged(bool value)
    {
        _loaded = false;
        _results.Clear();
        Query = "";
        SelectedSkill = null;
        ResetFilters();
        ApplyFilters();
        NotifyEmpty();
        OnPropertyChanged(nameof(ShowRefresh));
    }
    [RelayCommand]
    private void OpenDetails(SkillItem? skill)
    {
        if (skill is null || !Skills.Contains(skill) || IsBusy) return;
        SelectedSkill = skill;
        ShowDetails = true;
    }
    [RelayCommand] private void Back() => ShowDetails = false;
    [RelayCommand] private void ToggleAddSource() => AddSourceExpanded = !AddSourceExpanded;
    [RelayCommand]
    private Task SearchCatalogAsync() => RunAsync(async (progress, token) =>
    {
        if (!_service.SkillsShDiscoveryEnabled || CatalogQuery.Trim().Length < 2)
            throw new InvalidOperationException(CoreTools.Translate("Enable skills.sh discovery and enter at least two characters to search."));
        var skills = await Task.Run(() => _service.Search(CatalogQuery, progress, token,
            scope: SkillDiscoveryScope.PublicCatalog), token);
        var installed = await Task.Run(() => _service.ListInstalled(token), token);
        token.ThrowIfCancellationRequested();
        if (_stopped) return;
        SetInstalledInventory(installed);
        Query = "";
        SetResults(skills);
        NeedsRefresh = false;
        Status = ResultCount;
    });

    partial void OnQueryChanged(string value)
    {
        ApplyFilters();
        if (!string.Equals(AgentPlugins.Query, value, StringComparison.Ordinal))
            AgentPlugins.Query = value;
        RefreshLibraryItems();
    }
    partial void OnShowSourcesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSkills));
        OnPropertyChanged(nameof(ShowLibrary));
        NotifyActions();
    }
    partial void OnSourceInputChanged(string value)
    {
        SourceInputMessage = "";
        OnPropertyChanged(nameof(CanAddSource));
    }
    partial void OnSourceInputMessageChanged(string value) => OnPropertyChanged(nameof(HasSourceInputMessage));
    partial void OnSourceFilterChanged(string? value) => ApplyFilters();
    partial void OnAgentFilterChanged(string? value) => ApplyFilters();
    partial void OnSelectedSkillChanged(SkillItem? value)
    {
        _previewOperation?.Cancel();
        SkillPreview = null;
        PreviewError = "";
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenSource));
        OnPropertyChanged(nameof(CanLoadPreview));
        OnPropertyChanged(nameof(HasPreviewWarnings));
        OnPropertyChanged(nameof(PreviewWarnings));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasPreviewError));
        OnPropertyChanged(nameof(HasPreviewLicense));
        OnPropertyChanged(nameof(HasPreviewCompatibility));
        OnPropertyChanged(nameof(PreviewLicense));
        OnPropertyChanged(nameof(PreviewCompatibility));
        NotifyActions();
    }
    partial void OnIsBusyChanged(bool value)
    {
        NotifyActions();
        NotifyEmpty();
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(CanChangeLibrary));
        OnPropertyChanged(nameof(IsLibraryBusy));
        OnPropertyChanged(nameof(ShowLibraryEmpty));
    }
    partial void OnIsChoosingTargetsChanged(bool value)
    {
        NotifyActions();
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(CanChangeLibrary));
    }
    partial void OnNeedsRefreshChanged(bool value)
    {
        NotifyActions();
        OnPropertyChanged(nameof(CanChangeLibrary));
    }
    partial void OnHasErrorChanged(bool value)
    {
        NotifyEmpty();
        NotifyActions();
        OnPropertyChanged(nameof(HasLibraryError));
        OnPropertyChanged(nameof(ShowLibraryEmpty));
        OnPropertyChanged(nameof(LibraryEmptyTitle));
        OnPropertyChanged(nameof(LibraryEmptyDescription));
        OnPropertyChanged(nameof(CanChangeLibrary));
    }
    partial void OnOutputChanged(string value)
    {
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(LibraryActivity));
        OnPropertyChanged(nameof(HasLibraryActivity));
    }
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(LibraryStatus));
        OnPropertyChanged(nameof(LibraryEmptyDescription));
    }
    partial void OnSkillPreviewChanged(SkillPreview? value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasPreviewWarnings));
        OnPropertyChanged(nameof(PreviewWarnings));
        OnPropertyChanged(nameof(HasPreviewLicense));
        OnPropertyChanged(nameof(HasPreviewCompatibility));
        OnPropertyChanged(nameof(PreviewLicense));
        OnPropertyChanged(nameof(PreviewCompatibility));
    }
    partial void OnPreviewErrorChanged(string value) => OnPropertyChanged(nameof(HasPreviewError));
    partial void OnIsLoadingPreviewChanged(bool value) => OnPropertyChanged(nameof(CanLoadPreview));
    partial void OnInstalledCountChanged(int? value) => OnPropertyChanged(nameof(InstalledHeader));
    partial void OnUpdateCountChanged(int? value) => OnPropertyChanged(nameof(UpdatesHeader));

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanAddSource));
        OnPropertyChanged(nameof(ShowInstallAll));
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(InstallAllLabel));
        OnPropertyChanged(nameof(CanLoadPreview));
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowUpdateAction));
        OnPropertyChanged(nameof(ShowRemoveAction));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(ShowUpdateAll));
        OnPropertyChanged(nameof(CanUpdateAll));
        OnPropertyChanged(nameof(UpdateAllLabel));
    }

    private void NotifyEmpty()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(ShowSourceEditor));
        OnPropertyChanged(nameof(ShowCachedSourceNotice));
    }

    public void RefreshPreferences()
    {
        OnPropertyChanged(nameof(InstallationSummary));
        OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(PublicDiscoveryEnabled));
        OnPropertyChanged(nameof(ShowPublicDiscovery));
        if (!PublicDiscoveryEnabled) SearchPublicCatalog = false;
        NotifyEmpty();
    }

    /// <summary>The configured sources changed elsewhere: browse them again when they are shown.</summary>
    public async Task OnSourcesChangedAsync()
    {
        if (IsDiscover)
        {
            _loaded = false;
            if (!HasSources)
            {
                _results.Clear();
                SelectedSkill = null;
                ResetFilters();
                ApplyFilters();
            }
        }
        await LoadCurrentModeAsync();
    }

    private void ResetFilters()
    {
        Sources.Clear();
        Sources.Add(CoreTools.Translate("All sources"));
        Agents.Clear();
        Agents.Add(CoreTools.Translate("All agents"));
        SourceFilter = Sources[0];
        AgentFilter = Agents[0];
    }

    private void SetResults(IEnumerable<SkillItem> skills)
    {
        _results.Clear();
        _results.AddRange(skills.Select(skill => IsDiscover
            ? skill with
            {
                IsInstalled = _installedSkills.ContainsKey(SkillKey(skill)),
                HasUpdate = _knownUpdates.ContainsKey(SkillKey(skill)),
                Agents = _installedSkills.GetValueOrDefault(SkillKey(skill))?.Agents,
                Version = _installedSkills.GetValueOrDefault(SkillKey(skill))?.Version ?? skill.Version,
                NewVersion = _knownUpdates.GetValueOrDefault(SkillKey(skill))?.NewVersion,
            }
            : IsInstalled ? skill with
            {
                IsInstalled = true,
                HasUpdate = _knownUpdates.ContainsKey(SkillKey(skill)),
                NewVersion = _knownUpdates.GetValueOrDefault(SkillKey(skill))?.NewVersion
            }
            : IsUpdates ? skill with { IsInstalled = true, HasUpdate = true }
            : skill));
        SelectedSkill = null;
        ResetFilters();
        foreach (string source in _results.Select(skill => skill.SourceName).Distinct().Order())
            Sources.Add(source);
        foreach (string agent in _results.SelectMany(skill => skill.Agents ?? []).Distinct().Order())
            Agents.Add(agent);
        _loaded = true;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var selectedKey = SelectedSkill is { } selected ? SkillKey(selected) : null;
        Skills.Clear();
        foreach (var skill in _results.Where(skill =>
                     (SourceFilter is null || SourceFilter == Sources[0] || skill.SourceName == SourceFilter)
                     && (AgentFilter is null || AgentFilter == Agents[0] || (skill.Agents?.Contains(AgentFilter) ?? false))
                     && MatchesQuery(skill)))
            Skills.Add(skill);
        SelectedSkill = selectedKey is null ? null : Skills.FirstOrDefault(skill => SkillKey(skill) == selectedKey);
        NotifyEmpty();
        NotifyActions();
    }

    private bool MatchesQuery(SkillItem skill) =>
        skill.Id.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)
        || skill.Description.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private Task RefreshAsync() => LoadSkillsAsync(refresh: true);

    private Task LoadSkillsAsync(bool refresh) => IsAgentPlugins ? AgentPlugins.RefreshCommand.ExecuteAsync(null)
        : IsDiscover && SearchPublicCatalog ? SearchCatalogCommand.ExecuteAsync(null) : RunAsync(async (progress, token) =>
    {
        int mode = Mode;
        var skills = await Task.Run(() => mode switch
        {
            0 => _service.Search("", progress, token, filterSources: false, refreshSources: refresh, scope: SkillDiscoveryScope.Sources),
            1 => _service.ListInstalled(token),
            _ => _service.CheckUpdates(progress, token, refresh),
        }, token);
        var installed = mode == 1 ? skills : await Task.Run(() => _service.ListInstalled(token), token);
        if (_stopped || token.IsCancellationRequested || mode != Mode) return;
        SetInstalledInventory(installed);
        if (mode == 2)
        {
            SetKnownUpdates(skills);
        }
        string? selectedKey = SelectedSkill is { } selected ? SkillKey(selected) : null;
        string oldQuery = Query;
        string? oldSource = SourceFilter;
        string? oldAgent = AgentFilter;
        SetResults(skills);
        Query = oldQuery;
        if (Sources.Contains(oldSource ?? "")) SourceFilter = oldSource;
        if (Agents.Contains(oldAgent ?? "")) AgentFilter = oldAgent;
        SelectedSkill = Skills.FirstOrDefault(skill => SkillKey(skill) == selectedKey);
        if (mode == 1) InstalledCount = skills.Count;
        if (mode == 2) UpdateCount = skills.Count;
        NeedsRefresh = false;
        Status = CoreTools.Translate("{0} skills", Skills.Count);
    });

    private void SetInstalledInventory(IEnumerable<SkillItem> installed)
    {
        _installedSkills.Clear();
        foreach (var skill in installed) _installedSkills[SkillKey(skill)] = skill;
        SyncToolOptions();
    }

    public async Task LoadCurrentModeAsync()
    {
        if (IsAgentPlugins)
        {
            await AgentPlugins.LoadAsync();
            return;
        }
        RefreshPreferences();
        if (!_loaded && !IsBusy && (!IsDiscover || (!SearchPublicCatalog && HasSources)))
            await LoadSkillsAsync(refresh: false);
    }

    private void SetKnownUpdates(IEnumerable<SkillItem> updates)
    {
        _knownUpdates.Clear();
        foreach (var skill in updates) _knownUpdates[SkillKey(skill)] = skill;
        UpdateCount = _knownUpdates.Count;
    }

    /// <summary>
    /// Adds what was pasted as a source, or picks it if it is already added, and shows its skills.
    /// Accepts what the skills CLI does, including a whole "npx skills add" command.
    /// </summary>
    [RelayCommand]
    private async Task AddFromSourceAsync()
    {
        if (!CanAddSource) return;
        if (SkillSourceLocator.ParseUserInput(SourceInput) is not { } parsed)
        {
            SourceInputMessage = CoreTools.Translate("Enter a GitHub repository (owner/repo), a git repository URL, an HTTPS skills index, or a Notion database.");
            AccessibilityAnnouncementService.Announce(SourceInputMessage, AutomationLiveSetting.Assertive);
            return;
        }
        if (Mode != 0) Mode = 0;
        await RunAsync(async (progress, token) =>
        {
            if (AgentSkillsService.FindConfiguredSource(parsed) is null && _prepareSource is not null)
                await _prepareSource(parsed);
            token.ThrowIfCancellationRequested();
            var added = await _service.AddSourceAsync(parsed.InstallSource, progress, token);
            SearchPublicCatalog = false;
            var skills = await Task.Run(() => _service.Search("", progress, token, filterSources: false,
                scope: SkillDiscoveryScope.Sources), token);
            var installed = await Task.Run(() => _service.ListInstalled(token), token);
            if (_stopped || token.IsCancellationRequested) return;
            SetInstalledInventory(installed);
            SourceInput = "";
            AddSourceExpanded = false;
            Query = "";
            SetResults(skills);
            if (Sources.Contains(added.Source.Name)) SourceFilter = added.Source.Name;
            int count = Skills.Count;
            Status = added.Added
                ? CoreTools.Translate("Added {0}. It offers {1} skills.", added.Source.Name, count)
                : CoreTools.Translate("{0} is already one of your sources. It offers {1} skills.", added.Source.Name, count);
            AccessibilityAnnouncementService.Announce(Status);
        });
        if (HasError) SourceInputMessage = Status;
    }

    [RelayCommand]
    private Task InstallAsync() => RequestInstallAsync(SelectedSkill is { } skill ? [skill] : []);

    [RelayCommand]
    private Task InstallSkillAsync(SkillItem? skill)
    {
        if (skill is null || _stopped || IsBusy || !IsDiscover || !Skills.Contains(skill)) return Task.CompletedTask;
        SelectedSkill = skill;
        return RequestInstallAsync([skill]);
    }

    [RelayCommand]
    private Task UpdateSkillAsync(SkillItem? skill)
    {
        if (skill is null || IsBusy || !Skills.Contains(skill)) return Task.CompletedTask;
        SelectedSkill = skill;
        return CanUpdate ? ChangeAsync(SkillAction.Update) : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task InstallAllAsync()
    {
        if (!CanInstallAll) return;
        var skills = InstallableSkills;
        var targetAgents = await SelectInstallTargetsAsync(skills);
        if (targetAgents is null || _stopped || IsBusy) return;
        await RunAsync(async (progress, token) =>
        {
            if (_prepareSource is not null && _service.GetInstallSource(skills[0]) is { } source)
                await _prepareSource(source);
            List<string> failed = [];
            NeedsRefresh = true;
            foreach (var skill in skills)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(CoreTools.Translate("Installing {0}…", skill.Name));
                try
                {
                    await _service.PerformAsync(skill, SkillAction.Install, progress, token, targetAgents);
                    _installedSkills[SkillKey(skill)] = skill with { Agents = targetAgents };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex);
                    failed.Add($"{skill.Name}: {ex.Message}");
                }
            }
            if (_stopped) return;
            SyncToolOptions();
            RefreshDiscoverStates();
            InstalledCount = null;
            UpdateCount = null;
            if (failed.Count == 0)
            {
                NeedsRefresh = false;
                Status = CoreTools.Translate("Installed {0} skills", skills.Length);
                AccessibilityAnnouncementService.Announce(Status);
                return;
            }
            NeedsRefresh = true;
            throw new SkillsException(CoreTools.Translate("{0} of {1} skills could not be installed. {2}",
                failed.Count, skills.Length, string.Join(" ", failed)));
        });
    }
    [RelayCommand] private Task UpdateAsync() => ChangeAsync(SkillAction.Update);
    [RelayCommand] private Task RemoveAsync() => ChangeAsync(SkillAction.Remove);

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (!CanUpdateAll) return;
        var updates = UpdatableSkills;
        bool confirmed;
        IsChoosingTargets = true;
        try
        {
            confirmed = await _confirm(CoreTools.Translate("Update these {0} skills for their installed agents? Only the updates shown by the current filters are included.", updates.Length)
                + Environment.NewLine + string.Join(Environment.NewLine, updates.Select(skill => $"{skill.Name} ({skill.SourceName})")));
        }
        finally { IsChoosingTargets = false; }
        if (!confirmed || _stopped) return;
        await RunAsync(async (progress, token) =>
        {
            List<string> failures = [];
            int completed = 0;
            NeedsRefresh = true;
            foreach (var skill in updates)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(CoreTools.Translate("Updating {0} ({1}/{2})", skill.Name, completed + failures.Count + 1, updates.Length));
                try
                {
                    await _service.PerformAsync(skill, SkillAction.Update, progress, token);
                    token.ThrowIfCancellationRequested();
                    ReconcileSkillChange(skill, SkillAction.Update);
                    completed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex);
                    failures.Add($"{skill.Name}: {ex.Message}");
                }
            }
            if (failures.Count > 0)
                throw new SkillsException(CoreTools.Translate("{0} skills updated; {1} failed. {2}", completed, failures.Count, string.Join(Environment.NewLine, failures)));
            NeedsRefresh = false;
            Status = CoreTools.Translate("Updated {0} skills", completed);
            AccessibilityAnnouncementService.Announce(Status);
        });
    }

    [RelayCommand]
    private async Task LoadPreviewAsync()
    {
        if (!CanLoadPreview || SelectedSkill is not { } skill) return;
        _previewOperation?.Cancel();
        using var operation = new CancellationTokenSource();
        _previewOperation = operation;
        IsLoadingPreview = true;
        SkillPreview = null;
        PreviewError = "";
        try
        {
            var preview = await _service.PreviewAsync(skill, operation.Token);
            if (!_stopped && !operation.IsCancellationRequested && ReferenceEquals(SelectedSkill, skill))
                SkillPreview = preview;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (!_stopped && ReferenceEquals(SelectedSkill, skill))
            {
                PreviewError = CoreTools.Translate("Could not load the skill preview: {0}", ex.Message);
                AccessibilityAnnouncementService.Announce(PreviewError, AutomationLiveSetting.Assertive);
            }
        }
        finally
        {
            if (ReferenceEquals(_previewOperation, operation))
            {
                _previewOperation = null;
                IsLoadingPreview = false;
            }
        }
    }

    [RelayCommand]
    private void OpenSource()
    {
        if (SelectedSkill?.Source?.Url is { Scheme: "https" } url)
            CoreTools.Launch(url.AbsoluteUri);
    }
    private async Task RequestInstallAsync(IReadOnlyList<SkillItem> skills)
    {
        if (skills.Count == 0 || skills.Any(skill => !skill.CanInstallFromDiscover)
            || !CanNavigate || HasError || NeedsRefresh || !IsDiscover) return;
        var skill = skills.Count == 1 ? skills[0] : null;
        var selection = await SelectInstallTargetsAsync(skills);
        if (selection is not null && skill is not null)
            await ChangeAsync(SkillAction.Install, selection, skill);
    }

    private async Task<IReadOnlyList<string>?> SelectInstallTargetsAsync(IReadOnlyList<SkillItem> skills)
    {
        if (IsChoosingTargets || _stopped) return null;
        IsChoosingTargets = true;
        try
        {
            var selection = await _selectInstallTargets(skills, _service.GetSupportedAgents(), AgentSkillsService.TargetAgents);
            if (_stopped || selection is not { AgentIds.Count: > 0 }) return null;
            var targets = selection.AgentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (selection.RememberAsDefault) AgentSkillsService.TargetAgents = targets;
            return targets;
        }
        finally { IsChoosingTargets = false; }
    }

    private async Task ChangeAsync(SkillAction action, IReadOnlyList<string>? targetAgents = null, SkillItem? requestedSkill = null)
    {
        if ((requestedSkill ?? SelectedSkill) is not { } skill
            || (action == SkillAction.Install && (_stopped || IsBusy || !IsDiscover || !skill.CanInstallFromDiscover))
            || (action == SkillAction.Update && !CanUpdate)
            || (action == SkillAction.Remove && !CanRemove)) return;
        await RunAsync(async (progress, token) =>
        {
            if (action == SkillAction.Install && _prepareSource is not null && _service.GetInstallSource(skill) is { } source)
                await _prepareSource(source);
            token.ThrowIfCancellationRequested();
            NeedsRefresh = true;
            await _service.PerformAsync(skill, action, progress, token, targetAgents);
            token.ThrowIfCancellationRequested();
            if (_stopped) return;
            NeedsRefresh = false;
            Status = CoreTools.Translate("Completed: {0}", skill.Name);
            AccessibilityAnnouncementService.Announce(Status);
            ReconcileSkillChange(skill, action, targetAgents);
        });
    }

    private void ReconcileSkillChange(SkillItem skill, SkillAction action, IReadOnlyList<string>? targetAgents = null)
    {
        string key = SkillKey(skill);
        if (action == SkillAction.Install)
        {
            _installedSkills[key] = skill with { Agents = targetAgents };
            SyncToolOptions();
        }
        if (action == SkillAction.Remove) _installedSkills.Remove(key);
        if (action == SkillAction.Update && _installedSkills.TryGetValue(key, out var installed))
            _installedSkills[key] = installed with { Version = skill.NewVersion ?? installed.Version };
        if (action is SkillAction.Update or SkillAction.Remove) _knownUpdates.Remove(key);
        UpdateCount = _knownUpdates.Count;
        InstalledCount = _installedSkills.Count;
        bool removeRow = action == SkillAction.Remove ? IsInstalled || IsUpdates : action == SkillAction.Update && IsUpdates;
        if (removeRow) _results.RemoveAll(item => SkillKey(item).Equals(key, StringComparison.OrdinalIgnoreCase));
        RefreshDiscoverStates();
        if (!IsDiscover) ApplyFilters();
        if (removeRow)
        {
            ShowDetails = false;
            SelectedSkill = Skills.FirstOrDefault();
        }
    }

    private async Task RunAsync(Func<IProgress<string>, CancellationToken, Task> action)
    {
        if (_stopped || IsBusy) return;
        using var cancel = new CancellationTokenSource();
        _operation = cancel;
        IsBusy = true;
        HasError = false;
        Output = "";
        Status = CoreTools.Translate("Working…");
        _backgroundOperation?.Cancel();
        bool entered = false;
        try
        {
            await _workGate.WaitAsync(cancel.Token);
            entered = true;
            await action(new Progress<string>(line =>
            {
                if (_stopped || cancel.IsCancellationRequested || !ReferenceEquals(_operation, cancel)) return;
                Status = line;
                string output = Output + line + Environment.NewLine;
                Output = output.Length > 65536 ? output[^65536..] : output;
            }), cancel.Token);
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
                Status = CoreTools.Translate("Could not complete the operation: {0}", ex.Message);
                HasError = true;
                Output += Environment.NewLine + Status;
                AccessibilityAnnouncementService.Announce(Status, AutomationLiveSetting.Assertive);
            }
        }
        finally
        {
            if (entered) _workGate.Release();
            _operation = null;
            IsBusy = false;
        }
    }

    [RelayCommand] private void Cancel() => _operation?.Cancel();
    public void Stop()
    {
        AgentPlugins.Stop();
        _stopped = true;
        _operation?.Cancel();
        _backgroundOperation?.Cancel();
        _previewOperation?.Cancel();
        _service.ClearRegistryResults();
        NotifyActions();
        OnPropertyChanged(nameof(CanNavigate));
    }

    public async Task CheckForUpdatesInBackgroundAsync()
    {
        if (_stopped || IsBusy || !await _workGate.WaitAsync(0)) return;
        using var cancel = new CancellationTokenSource();
        _backgroundOperation = cancel;
        IsCheckingUpdates = true;
        try
        {
            var updates = await Task.Run(() => _service.CheckUpdates(null, cancel.Token), cancel.Token);
            if (_stopped || cancel.IsCancellationRequested) return;
            SetKnownUpdates(updates);
            RefreshDiscoverStates();
            UpdateCount = updates.Count;
            BackgroundStatus = updates.Count == 1
                ? CoreTools.Translate("1 skill update")
                : CoreTools.Translate("{0} skill updates", updates.Count);
            string signature = string.Join('|', updates.Select(skill => $"{skill.SourceName}:{skill.Id}:{skill.NewVersion}").Order());
            if (updates.Count > 0 && signature != _notifiedUpdates
                && !Settings.AreUpdatesNotificationsDisabled()
                && !Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledPackageManagerNotifications, "Skills"))
            {
                if (UpdatesAvailable is { } notify) notify(updates.Count);
                else if (OperatingSystem.IsWindows())
                    WindowsAppNotificationBridge.ShowExtensionUpdatesAvailableNotification(
                        CoreTools.Translate("Agent Skills"), updates.Count, NotificationArguments.ShowOnAgentSkillsTab);
                else if (OperatingSystem.IsMacOS())
                    MacOsNotificationBridge.ShowExtensionUpdatesAvailableNotification(
                        CoreTools.Translate("Agent Skills"), updates.Count, NotificationArguments.ShowOnAgentSkillsTab);
            }
            _notifiedUpdates = signature;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (!_stopped) BackgroundStatus = CoreTools.Translate("Could not check skill updates: {0}", ex.Message);
        }
        finally
        {
            _backgroundOperation = null;
            IsCheckingUpdates = false;
            _workGate.Release();
        }
    }
}
