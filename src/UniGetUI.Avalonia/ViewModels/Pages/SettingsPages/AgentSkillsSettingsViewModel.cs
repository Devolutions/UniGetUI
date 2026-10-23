using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Devolutions.AgentSkills;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Managers.SkillsManager;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using SkillsManager = UniGetUI.PackageEngine.Managers.SkillsManager.AgentSkills;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

internal interface IAgentSkillsSettingsService
{
    IReadOnlyList<AgentInfo> GetAgents();
    IReadOnlyList<string> TargetAgents { get; set; }
    bool NotificationsEnabled { get; set; }
    bool SkillsShDiscoveryEnabled { get; set; }
    string SavedVersion { get; }
    bool SetVersion(string version);
    bool HasNotionSources { get; }
    Task<NotionStatus> GetNotionStatusAsync();
    Task SignOutAsync();
    void RefreshNotionSources();
}

internal sealed class AgentSkillsSettingsService(SkillsManager manager) : IAgentSkillsSettingsService
{
    public IReadOnlyList<AgentInfo> GetAgents() => manager.GetSupportedAgents();
    public IReadOnlyList<string> TargetAgents { get => SkillsManager.TargetAgents; set => SkillsManager.TargetAgents = value; }
    public bool NotificationsEnabled
    {
        get => !CoreSettings.GetDictionaryItem<string, bool>(CoreSettings.K.DisabledPackageManagerNotifications, "Skills");
        set => CoreSettings.SetDictionaryItem(CoreSettings.K.DisabledPackageManagerNotifications, "Skills", !value);
    }
    public string SavedVersion => CoreSettings.GetValue(CoreSettings.K.SkillsNotionCliVersion);
    public bool SkillsShDiscoveryEnabled { get => manager.SkillsShDiscoveryEnabled; set => manager.SkillsShDiscoveryEnabled = value; }
    public bool SetVersion(string version) => NotionCliTool.TrySetPinnedVersion(version);
    public bool HasNotionSources => manager.HasNotionSources;
    public Task<NotionStatus> GetNotionStatusAsync() => manager.GetNotionStatusAsync();
    public Task SignOutAsync() => manager.SignOutOfNotionAsync();
    public void RefreshNotionSources() => manager.RefreshNotionSources();
}

public partial class SkillTargetViewModel(string id, string name, bool detected, bool selected) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string DetectionLabel { get; } = detected ? "" : CoreTools.Translate("Not detected");
    public bool IsNotDetected => !detected;
    [ObservableProperty] private bool _isSelected = selected;
}

public partial class AgentSkillsSettingsViewModel : ViewModelBase
{
    private readonly IAgentSkillsSettingsService _service;
    private readonly bool _loading = true;
    private bool _active = true;
    private int _generation;
    private bool _notionExpansionChosen;
    private bool _autoExpanded;
    private bool _refreshPending;

    public ObservableCollection<SkillTargetViewModel> Agents { get; } = [];
    [ObservableProperty] private int _targetMode;
    [ObservableProperty] private string _targetError = "";
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _skillsShDiscoveryEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkillsShDiscoveryError))]
    private string _skillsShDiscoveryError = "";
    public bool HasSkillsShDiscoveryError => SkillsShDiscoveryError.Length > 0;
    [ObservableProperty] private int _versionPolicy;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _versionError = "";
    [ObservableProperty] private string _notionError = "";
    [ObservableProperty] private string _notionSummary = CoreTools.Translate("Checking the Notion CLI…");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isNotionExpanded;
    [ObservableProperty] private bool _isAdvancedExpanded;
    [ObservableProperty] private NotionCliState _notionState = NotionCliState.Unknown;

    public IReadOnlyList<string> TargetModes { get; }
    public IReadOnlyList<string> VersionPolicies { get; } =
        [CoreTools.Translate("Latest available"), CoreTools.Translate("Specific version")];
    public bool ChooseAgents => TargetMode == 1;
    public bool NoDetectedAgents => Agents.All(agent => agent.IsNotDetected);
    public bool HasTargetError => TargetError.Length > 0;
    public bool SpecificVersion => VersionPolicy == 1;
    public bool HasVersionError => VersionError.Length > 0;
    public bool HasNotionError => NotionError.Length > 0;
    public bool CanInstall => _active && !IsBusy && !HasVersionError;
    public bool CanAct => _active && !IsBusy;
    public bool ShowInstall => NotionState == NotionCliState.NotInstalled;
    public bool ShowSignIn => NotionState == NotionCliState.SignedOut;
    public bool ShowSignOut => NotionState == NotionCliState.SignedIn;
    public string VersionSummary => HasVersionError
        ? CoreTools.Translate("Version needs attention")
        : SpecificVersion ? VersionText.Trim() : CoreTools.Translate("Latest available");

    public AgentSkillsSettingsViewModel(SkillsManager manager) : this(new AgentSkillsSettingsService(manager)) { }

    internal AgentSkillsSettingsViewModel(IAgentSkillsSettingsService service)
    {
        _service = service;
        var selected = service.TargetAgents;
        try
        {
            foreach (var agent in service.GetAgents().Where(agent => agent.IsDetected || selected.Contains(agent.Id)))
                AddAgent(new(agent.Id, agent.DisplayName, agent.IsDetected, selected.Count == 0 || selected.Contains(agent.Id)));
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load Agent Skills installation targets");
            Logger.Error(ex);
            TargetError = CoreTools.Translate("Could not load coding agents: {0}", ex.Message);
        }
        foreach (string id in selected.Where(id => Agents.All(agent => agent.Id != id)))
            AddAgent(new(id, id, false, true));
        TargetModes = Agents.Count > 0
            ? [CoreTools.Translate("Automatic (recommended)"), CoreTools.Translate("Choose agents")]
            : [CoreTools.Translate("Automatic (recommended)")];
        _targetMode = selected.Count == 0 ? 0 : 1;
        _notificationsEnabled = service.NotificationsEnabled;
        _skillsShDiscoveryEnabled = service.SkillsShDiscoveryEnabled;
        _versionText = service.SavedVersion;
        _versionPolicy = string.IsNullOrWhiteSpace(_versionText) ? 0 : 1;
        ValidateVersion(save: false);
        _loading = false;
    }

    private void AddAgent(SkillTargetViewModel agent)
    {
        Agents.Add(agent);
        agent.PropertyChanged += (_, e) =>
        {
            if (!_loading && ChooseAgents && e.PropertyName == nameof(SkillTargetViewModel.IsSelected))
                SaveTargets();
        };
    }

    partial void OnTargetModeChanged(int value)
    {
        OnPropertyChanged(nameof(ChooseAgents));
        if (_loading) return;
        if (value == 0)
        {
            _service.TargetAgents = [];
            TargetError = "";
        }
        else
        {
            if (!Agents.Any(agent => agent.IsSelected) && Agents.FirstOrDefault() is { } first)
                first.IsSelected = true;
            SaveTargets();
        }
    }

    private void SaveTargets()
    {
        string[] selected = Agents.Where(agent => agent.IsSelected).Select(agent => agent.Id).ToArray();
        TargetError = selected.Length == 0 ? CoreTools.Translate("Select at least one coding agent, or choose Automatic.") : "";
        if (selected.Length > 0)
            _service.TargetAgents = selected;
    }

    partial void OnTargetErrorChanged(string value) => OnPropertyChanged(nameof(HasTargetError));
    partial void OnNotificationsEnabledChanged(bool value)
    {
        if (!_loading) _service.NotificationsEnabled = value;
    }

    partial void OnSkillsShDiscoveryEnabledChanged(bool value)
    {
        if (_loading) return;
        try
        {
            _service.SkillsShDiscoveryEnabled = value;
            SkillsShDiscoveryError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Could not save the skills.sh discovery preference");
            Logger.Warn(ex);
            _skillsShDiscoveryEnabled = _service.SkillsShDiscoveryEnabled;
            OnPropertyChanged(nameof(SkillsShDiscoveryEnabled));
            SkillsShDiscoveryError = CoreTools.Translate("The skills.sh discovery preference could not be saved. Check your settings folder permissions and try again.");
        }
    }

    partial void OnVersionPolicyChanged(int value)
    {
        OnPropertyChanged(nameof(SpecificVersion));
        if (!_loading) ValidateVersion(save: true);
    }

    partial void OnVersionTextChanged(string value)
    {
        if (!_loading && SpecificVersion) ValidateVersion(save: true);
    }

    private void ValidateVersion(bool save)
    {
        string version = SpecificVersion ? VersionText.Trim() : "";
        bool valid = !SpecificVersion || (version.Length > 0 && NotionCliTool.IsValidVersion(version));
        if (valid && save)
            valid = _service.SetVersion(version);
        VersionError = valid ? "" : CoreTools.Translate("Enter a valid version number, or choose Latest available.");
        if (!valid) IsAdvancedExpanded = true;
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(CanInstall));
    }

    partial void OnVersionErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasVersionError));
        OnPropertyChanged(nameof(VersionSummary));
        OnPropertyChanged(nameof(CanInstall));
    }
    partial void OnNotionErrorChanged(string value) => OnPropertyChanged(nameof(HasNotionError));
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanAct));
    }
    partial void OnNotionStateChanged(NotionCliState value)
    {
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowSignIn));
        OnPropertyChanged(nameof(ShowSignOut));
    }
    partial void OnIsNotionExpandedChanged(bool value)
    {
        if (!_autoExpanded) _notionExpansionChosen = true;
    }

    public void Activate()
    {
        _active = true;
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanInstall));
    }

    public void Deactivate()
    {
        _active = false;
        _generation++;
        _refreshPending = false;
        IsBusy = false;
        OnPropertyChanged(nameof(CanAct));
        OnPropertyChanged(nameof(CanInstall));
    }

    private void ExpandIfNeeded()
    {
        if (_service.HasNotionSources && NotionState != NotionCliState.SignedIn && !_notionExpansionChosen)
        {
            _autoExpanded = true;
            IsNotionExpanded = true;
            _autoExpanded = false;
        }
    }

    public async Task RefreshAsync()
    {
        if (!_active) return;
        if (IsBusy)
        {
            _refreshPending = true;
            return;
        }
        int generation = _generation;
        IsBusy = true;
        NotionError = "";
        NotionSummary = CoreTools.Translate("Checking the Notion CLI…");
        try
        {
            await ReadStatusAsync(generation);
        }
        finally
        {
            await FinishOperationAsync(generation);
        }
    }

    private async Task FinishOperationAsync(int generation)
    {
        if (!_active || generation != _generation) return;
        IsBusy = false;
        if (_refreshPending)
        {
            _refreshPending = false;
            await RefreshAsync();
        }
    }

    private async Task ReadStatusAsync(int generation)
    {
        try
        {
            var status = await _service.GetNotionStatusAsync();
            if (!_active || generation != _generation) return;
            NotionState = status.State;
            NotionSummary = status.State switch
            {
                NotionCliState.NotInstalled => CoreTools.Translate("Not installed. Only Notion sources need this integration."),
                NotionCliState.SignedOut => CoreTools.Translate("Notion CLI {0} installed. Sign in to use Notion sources.", status.CliVersion ?? ""),
                NotionCliState.SignedIn => status.WorkspaceName is { Length: > 0 } workspace
                    ? CoreTools.Translate("Connected to {0}, CLI {1}", workspace, status.CliVersion ?? "")
                    : CoreTools.Translate("Connected to Notion, CLI {0}", status.CliVersion ?? ""),
                _ => CoreTools.Translate("Could not check the Notion connection."),
            };
            if (status.State == NotionCliState.Unknown)
                NotionError = string.IsNullOrWhiteSpace(status.Message)
                    ? CoreTools.Translate("Retry the connection check or view Agent Skills logs.")
                    : status.Message;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not check the Notion connection");
            Logger.Warn(ex);
            if (!_active || generation != _generation) return;
            NotionState = NotionCliState.Unknown;
            NotionSummary = CoreTools.Translate("Could not check the Notion connection.");
            NotionError = ex.Message;
        }
        ExpandIfNeeded();
    }

    public async Task RunNotionActionAsync(Func<Task> action, bool requiresValidVersion = false)
    {
        if (!CanAct) return;
        if (requiresValidVersion && HasVersionError)
        {
            IsAdvancedExpanded = true;
            return;
        }
        int generation = _generation;
        IsBusy = true;
        NotionError = "";
        try
        {
            await action();
            if (!_active || generation != _generation) return;
            _service.RefreshNotionSources();
            await ReadStatusAsync(generation);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not complete the Notion action");
            Logger.Warn(ex);
            if (!_active || generation != _generation) return;
            NotionError = CoreTools.Translate("Could not complete the Notion action: {0}", ex.Message);
            IsNotionExpanded = true;
        }
        finally
        {
            await FinishOperationAsync(generation);
        }
    }

    public Task SignOutAsync() => RunNotionActionAsync(_service.SignOutAsync);
}
