using System.Collections.ObjectModel;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class AgentSkillsViewModel : ViewModelBase
{
    private readonly AgentSkillsService _service;
    private readonly Func<SkillSourceLocator, Task>? _prepareSource;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly List<SkillItem> _results = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _backgroundOperation;
    private bool _stopped;
    private bool _loaded;
    private string _notifiedUpdates = "";
    internal Action<int>? UpdatesAvailable { get; set; }
    internal AgentSkillsService Service => _service;
    public ObservableCollection<SkillItem> Skills { get; } = [];
    public ObservableCollection<string> Sources { get; } = [CoreTools.Translate("All sources")];
    public ObservableCollection<string> Agents { get; } = [CoreTools.Translate("All agents")];
    [ObservableProperty] private int _mode;
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
    public bool IsDiscover => Mode == 0;
    public bool IsInstalled => Mode == 1;
    public bool IsUpdates => Mode == 2;
    public bool HasSelection => SelectedSkill is not null;
    public bool CanOpenSource => SelectedSkill?.Source?.Url.Scheme == "https";
    public bool HasOutput => Output.Length > 0;
    public bool ShowEmpty => Skills.Count == 0 && !IsBusy;
    public bool CanInstall => !_stopped && !IsBusy && IsDiscover && HasSelection;
    public bool CanRemove => !_stopped && !IsBusy && IsInstalled && HasSelection;
    public bool CanUpdate => !_stopped && !IsBusy && IsUpdates && HasSelection;
    public string InstalledHeader => InstalledCount is { } count
        ? CoreTools.Translate("Installed ({0})", count) : CoreTools.Translate("Installed");
    public string UpdatesHeader => UpdateCount is { } count
        ? CoreTools.Translate("Updates ({0})", count) : CoreTools.Translate("Updates");
    public string SearchLabel => IsDiscover ? CoreTools.Translate("Search skills") : CoreTools.Translate("Filter skills");
    public string RefreshLabel => IsDiscover ? CoreTools.Translate("Search") : CoreTools.Translate("Refresh");
    public bool NeedsSetup => IsDiscover && AgentSkillsService.GetConfiguredSources().Count == 0
        && !_service.SkillsShDiscoveryEnabled;
    public string EmptyTitle => HasError ? CoreTools.Translate("Could not load skills")
        : NeedsSetup ? CoreTools.Translate("Choose where to find skills")
        : !_loaded && IsDiscover ? CoreTools.Translate("Find a skill for your agents")
        : _results.Count > 0 || (IsDiscover && Query.Length > 0) ? CoreTools.Translate("No matching skills")
        : IsInstalled ? CoreTools.Translate("No skills installed")
        : IsUpdates ? CoreTools.Translate("No skill updates found")
        : CoreTools.Translate("No skills found");
    public string EmptyDescription => HasError ? Status
        : NeedsSetup ? CoreTools.Translate("Add a repository or skills index, or turn on skills.sh discovery to search its public catalog.")
        : !_loaded && IsDiscover ? CoreTools.Translate("Search your sources by name or description. Public skills.sh discovery is optional.")
        : _results.Count > 0 ? CoreTools.Translate("Try a different search or clear the source and agent filters.")
        : IsInstalled ? CoreTools.Translate("Skills installed for your user account will appear here. Open Discover to install your first skill.")
        : IsUpdates ? CoreTools.Translate("No updates were found in your configured sources. Refresh to check again.")
        : CoreTools.Translate("Try another search or add a source. skills.sh searches need at least two characters.");
    public string InstallationSummary => AgentSkillsService.TargetAgents is { Count: > 0 } targets
        ? CoreTools.Translate("Install for your user account in: {0}", string.Join(", ", targets))
        : CoreTools.Translate("Install for your user account using automatic agent detection and the shared skills folder.");

    public AgentSkillsViewModel() : this(new AgentSkillsService(), async source =>
    {
        if (AgentSkillsDependencies.ForSource(source) is { } dependency)
            await AvaloniaBootstrapper.PromptForMissingDependencyAsync(dependency);
    })
    { }

    internal AgentSkillsViewModel(AgentSkillsService service, Func<SkillSourceLocator, Task>? prepareSource = null)
    {
        _service = service;
        _prepareSource = prepareSource;
        _sourceFilter = Sources[0];
        _agentFilter = Agents[0];
        Skills.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmpty));
    }

    partial void OnModeChanged(int value)
    {
        _operation?.Cancel();
        ShowSources = false;
        _loaded = false;
        SelectedSkill = null;
        _results.Clear();
        Query = "";
        ResetFilters();
        Skills.Clear();
        Status = "";
        HasError = false;
        foreach (string property in new[] { nameof(IsDiscover), nameof(IsInstalled), nameof(IsUpdates),
                     nameof(SearchLabel), nameof(RefreshLabel) })
            OnPropertyChanged(property);
        NotifyEmpty();
        NotifyActions();
    }

    partial void OnQueryChanged(string value) { if (!IsDiscover) ApplyFilters(); }
    partial void OnSourceFilterChanged(string? value) => ApplyFilters();
    partial void OnAgentFilterChanged(string? value) => ApplyFilters();
    partial void OnSelectedSkillChanged(SkillItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenSource));
        NotifyActions();
    }
    partial void OnIsBusyChanged(bool value) { NotifyActions(); NotifyEmpty(); }
    partial void OnHasErrorChanged(bool value) => NotifyEmpty();
    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnInstalledCountChanged(int? value) => OnPropertyChanged(nameof(InstalledHeader));
    partial void OnUpdateCountChanged(int? value) => OnPropertyChanged(nameof(UpdatesHeader));

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanUpdate));
    }

    private void NotifyEmpty()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }

    public void RefreshPreferences()
    {
        OnPropertyChanged(nameof(InstallationSummary));
        NotifyEmpty();
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
        _results.AddRange(skills);
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
        var selected = SelectedSkill;
        Skills.Clear();
        foreach (var skill in _results.Where(skill =>
                     (SourceFilter is null || SourceFilter == Sources[0] || skill.SourceName == SourceFilter)
                     && (AgentFilter is null || AgentFilter == Agents[0] || (skill.Agents?.Contains(AgentFilter) ?? false))
                     && (IsDiscover || skill.Id.Contains(Query, StringComparison.OrdinalIgnoreCase)
                         || skill.Description.Contains(Query, StringComparison.OrdinalIgnoreCase))))
            Skills.Add(skill);
        SelectedSkill = selected is not null && Skills.Contains(selected) ? selected : null;
        NotifyEmpty();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async (progress, token) =>
    {
        int mode = Mode;
        string query = Query;
        var skills = await Task.Run(() => mode switch
        {
            0 => _service.Search(query, progress, token),
            1 => _service.ListInstalled(token),
            _ => _service.CheckUpdates(progress, token),
        }, token);
        if (_stopped || token.IsCancellationRequested || mode != Mode) return;
        SetResults(skills);
        if (mode == 1) InstalledCount = skills.Count;
        if (mode == 2) UpdateCount = skills.Count;
        Status = CoreTools.Translate("{0} skills", Skills.Count);
    });

    public async Task LoadCurrentModeAsync()
    {
        RefreshPreferences();
        if (!IsDiscover && !_loaded && !IsBusy)
            await RefreshCommand.ExecuteAsync(null);
    }

    [RelayCommand] private Task InstallAsync() => ChangeAsync(SkillAction.Install);
    [RelayCommand] private Task UpdateAsync() => ChangeAsync(SkillAction.Update);
    [RelayCommand] private Task RemoveAsync() => ChangeAsync(SkillAction.Remove);
    [RelayCommand]
    private void OpenSource()
    {
        if (SelectedSkill?.Source?.Url is { Scheme: "https" } url)
            CoreTools.Launch(url.AbsoluteUri);
    }
    private async Task ChangeAsync(SkillAction action)
    {
        if (SelectedSkill is not { } skill
            || (action == SkillAction.Install && !CanInstall)
            || (action == SkillAction.Update && !CanUpdate)
            || (action == SkillAction.Remove && !CanRemove)) return;
        await RunAsync(async (progress, token) =>
        {
            if (action == SkillAction.Install && _prepareSource is not null && _service.GetInstallSource(skill) is { } source)
                await _prepareSource(source);
            token.ThrowIfCancellationRequested();
            await _service.PerformAsync(skill, action, progress, token);
            token.ThrowIfCancellationRequested();
            if (_stopped) return;
            Status = CoreTools.Translate("Completed: {0}", skill.Name);
            AccessibilityAnnouncementService.Announce(Status);
            InstalledCount = null;
            if (action != SkillAction.Update) UpdateCount = null;
            if (action is SkillAction.Update or SkillAction.Remove)
            {
                _results.Remove(skill);
                SelectedSkill = null;
                ApplyFilters();
                if (IsUpdates) UpdateCount = _results.Count;
                if (IsInstalled) InstalledCount = _results.Count;
            }
        });
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
        catch (OperationCanceledException) { if (!_stopped) Status = CoreTools.Translate("Cancelled"); }
        catch (Exception ex)
        {
            Logger.Error(ex);
            if (!_stopped)
            {
                Status = CoreTools.Translate("Could not complete the operation: {0}", ex.Message);
                HasError = true;
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
        _stopped = true;
        _operation?.Cancel();
        _backgroundOperation?.Cancel();
        _service.ClearRegistryResults();
        NotifyActions();
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
            UpdateCount = updates.Count;
            BackgroundStatus = CoreTools.Translate("{0} skill updates", updates.Count);
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
