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

public partial class AgentSkillsViewModel : ViewModelBase
{
    private readonly AgentSkillsService _service;
    private readonly Func<SkillSourceLocator, Task>? _prepareSource;
    private readonly Func<IReadOnlyList<SkillItem>, IReadOnlyList<AgentInfo>, IReadOnlyList<string>?,
        Task<AgentSkillsInstallSelection?>> _selectInstallTargets;
    private readonly SemaphoreSlim _workGate = new(1, 1);
    private readonly List<SkillItem> _results = [];
    private readonly HashSet<string> _installedSkillKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _updateSkillKeys = new(StringComparer.OrdinalIgnoreCase);
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
    [ObservableProperty] private string _sourceInput = "";
    [ObservableProperty] private string _sourceInputMessage = "";
    [ObservableProperty] private SkillPreview? _skillPreview;
    [ObservableProperty] private string _previewError = "";
    [ObservableProperty] private bool _isLoadingPreview;
    public bool IsDiscover => Mode == 0;
    public bool IsInstalled => Mode == 1;
    public bool IsUpdates => Mode == 2;
    public bool HasSelection => SelectedSkill is not null;
    public bool CanOpenSource => SelectedSkill?.Source?.Url.Scheme == "https";
    public bool HasOutput => Output.Length > 0;
    public bool ShowEmpty => Skills.Count == 0 && !IsBusy;
    public bool CanInstall => !_stopped && !IsBusy && IsDiscover && SelectedSkill?.CanInstallFromDiscover == true;
    public bool CanRemove => !_stopped && !IsBusy && IsInstalled && HasSelection;
    public bool CanUpdate => !_stopped && !IsBusy && IsUpdates && HasSelection;
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
    public bool ShowInstallAll => IsDiscover && InstallableSkills.Length > 1 && SingleShownSource is not null;
    public bool CanInstallAll => !_stopped && !IsBusy && ShowInstallAll;
    public string InstallAllLabel => CoreTools.Translate("Install all {0}", InstallableSkills.Length);
    public string InstalledHeader => InstalledCount is { } count
        ? CoreTools.Translate("Installed ({0})", count) : CoreTools.Translate("Installed");
    public string UpdatesHeader => UpdateCount is { } count
        ? CoreTools.Translate("Updates ({0})", count) : CoreTools.Translate("Updates");
    public string SearchLabel => CoreTools.Translate("Filter skills");
    public string RefreshLabel => IsDiscover && _service.SkillsShDiscoveryEnabled
        ? CoreTools.Translate("Search skills.sh") : CoreTools.Translate("Refresh");
    private static bool HasSources => AgentSkillsService.GetConfiguredSources().Count > 0;
    public bool NeedsSetup => IsDiscover && !HasSources
        && !_service.SkillsShDiscoveryEnabled;
    public string EmptyTitle => HasError ? CoreTools.Translate("Could not load skills")
        : NeedsSetup ? CoreTools.Translate("Add skills from a repository")
        : !_loaded && IsDiscover ? (HasSources ? CoreTools.Translate("Browse your sources") : CoreTools.Translate("Search skills.sh"))
        : _results.Count > 0 || (IsDiscover && Query.Length > 0) ? CoreTools.Translate("No matching skills")
        : IsInstalled ? CoreTools.Translate("No skills installed")
        : IsUpdates ? CoreTools.Translate("No skill updates found")
        : CoreTools.Translate("No skills found");
    public string EmptyDescription => HasError ? Status
        : NeedsSetup ? CoreTools.Translate("Paste a GitHub repository such as owner/repo, a repository or skills index URL, or an \"npx skills add\" command above. Its skills will appear here, ready to install.")
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
            Task<AgentSkillsInstallSelection?>>? selectInstallTargets = null)
    {
        _service = service;
        _prepareSource = prepareSource;
        _selectInstallTargets = selectInstallTargets ?? AgentSkillsInstallDialog.ShowAsync;
        _sourceFilter = Sources[0];
        _agentFilter = Agents[0];
        Skills.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowEmpty));
            NotifyActions();
        };
    }

    private static string SkillKey(SkillItem skill) => $"{skill.Id}\0{skill.SourceName}";

    private void RefreshDiscoverStates()
    {
        for (int i = 0; i < _results.Count; i++)
        {
            var skill = _results[i];
            bool isInstalled = _installedSkillKeys.Contains(SkillKey(skill));
            bool hasUpdate = _updateSkillKeys.Contains(SkillKey(skill));
            if (skill.IsInstalled != isInstalled || skill.HasUpdate != hasUpdate)
                _results[i] = skill with { IsInstalled = isInstalled, HasUpdate = hasUpdate };
        }
        if (IsDiscover) ApplyFilters();
    }

    partial void OnModeChanged(int value)
    {
        _operation?.Cancel();
        _previewOperation?.Cancel();
        ShowSources = false;
        _loaded = false;
        SelectedSkill = null;
        _results.Clear();
        Query = "";
        ResetFilters();
        Skills.Clear();
        SkillPreview = null;
        PreviewError = "";
        Status = "";
        HasError = false;
        foreach (string property in new[] { nameof(IsDiscover), nameof(IsInstalled), nameof(IsUpdates),
                     nameof(SearchLabel), nameof(RefreshLabel) })
            OnPropertyChanged(property);
        NotifyEmpty();
        NotifyActions();
    }

    partial void OnQueryChanged(string value) => ApplyFilters();
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
    partial void OnIsBusyChanged(bool value) { NotifyActions(); NotifyEmpty(); }
    partial void OnHasErrorChanged(bool value) => NotifyEmpty();
    partial void OnOutputChanged(string value) => OnPropertyChanged(nameof(HasOutput));
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
        OnPropertyChanged(nameof(RefreshLabel));
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
                IsInstalled = _installedSkillKeys.Contains(SkillKey(skill)),
                HasUpdate = _updateSkillKeys.Contains(SkillKey(skill)),
            }
            : IsInstalled ? skill with { IsInstalled = true }
            : IsUpdates ? skill with { HasUpdate = true }
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
                     && (MatchesQuery(skill) || IsCatalogResult(skill))))
            Skills.Add(skill);
        SelectedSkill = selectedKey is null ? null : Skills.FirstOrDefault(skill => SkillKey(skill) == selectedKey);
        NotifyEmpty();
        NotifyActions();
    }

    private bool MatchesQuery(SkillItem skill) =>
        skill.Id.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase)
        || skill.Description.Contains(Query.Trim(), StringComparison.OrdinalIgnoreCase);

    // skills.sh matches on more than the name, and its results carry no description of their own
    private bool IsCatalogResult(SkillItem skill) =>
        IsDiscover && skill.Source is { } source && !AgentSkillsService.IsAllowedSource(source);

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async (progress, token) =>
    {
        int mode = Mode;
        string query = Query;
        // Once results are shown, Refresh means "fetch again", unless it is searching skills.sh
        bool refreshSources = _loaded && !(_service.SkillsShDiscoveryEnabled && query.Trim().Length >= 2);
        var skills = await Task.Run(() => mode switch
        {
            0 => _service.Search(query, progress, token, filterSources: false, refreshSources),
            1 => _service.ListInstalled(token),
            _ => _service.CheckUpdates(progress, token),
        }, token);
        IReadOnlyList<SkillItem>? installed = mode == 0
            ? await Task.Run(() => _service.ListInstalled(token), token)
            : null;
        if (_stopped || token.IsCancellationRequested || mode != Mode) return;
        if (installed is not null)
        {
            _installedSkillKeys.Clear();
            _installedSkillKeys.UnionWith(installed.Select(SkillKey));
        }
        if (mode == 2)
        {
            _updateSkillKeys.Clear();
            _updateSkillKeys.UnionWith(skills.Select(SkillKey));
        }
        SetResults(skills);
        if (mode == 1) InstalledCount = skills.Count;
        if (mode == 2) UpdateCount = skills.Count;
        Status = CoreTools.Translate("{0} skills", Skills.Count);
    });

    public async Task LoadCurrentModeAsync()
    {
        RefreshPreferences();
        if (!_loaded && !IsBusy && (!IsDiscover || HasSources))
            await RefreshCommand.ExecuteAsync(null);
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
            var skills = await Task.Run(() => _service.Search("", progress, token, filterSources: false), token);
            if (_stopped || token.IsCancellationRequested) return;
            SourceInput = "";
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
            foreach (var skill in skills)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(CoreTools.Translate("Installing {0}…", skill.Name));
                try
                {
                    await _service.PerformAsync(skill, SkillAction.Install, progress, token, targetAgents);
                    _installedSkillKeys.Add(SkillKey(skill));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error(ex);
                    failed.Add($"{skill.Name}: {ex.Message}");
                }
            }
            if (_stopped) return;
            RefreshDiscoverStates();
            InstalledCount = null;
            UpdateCount = null;
            if (failed.Count == 0)
            {
                Status = CoreTools.Translate("Installed {0} skills", skills.Length);
                AccessibilityAnnouncementService.Announce(Status);
                return;
            }
            throw new SkillsException(CoreTools.Translate("{0} of {1} skills could not be installed. {2}",
                failed.Count, skills.Length, string.Join(" ", failed)));
        });
    }
    [RelayCommand] private Task UpdateAsync() => ChangeAsync(SkillAction.Update);
    [RelayCommand] private Task RemoveAsync() => ChangeAsync(SkillAction.Remove);

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
            || _stopped || IsBusy || !IsDiscover) return;
        var skill = skills.Count == 1 ? skills[0] : null;
        var selection = await SelectInstallTargetsAsync(skills);
        if (selection is not null && skill is not null)
            await ChangeAsync(SkillAction.Install, selection, skill);
    }

    private async Task<IReadOnlyList<string>?> SelectInstallTargetsAsync(IReadOnlyList<SkillItem> skills)
    {
        var selection = await _selectInstallTargets(skills, _service.GetSupportedAgents(), AgentSkillsService.TargetAgents);
        if (selection is not { AgentIds.Count: > 0 }) return null;
        var targets = selection.AgentIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selection.RememberAsDefault)
            AgentSkillsService.TargetAgents = targets;
        return targets;
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
            await _service.PerformAsync(skill, action, progress, token, targetAgents);
            token.ThrowIfCancellationRequested();
            if (_stopped) return;
            Status = CoreTools.Translate("Completed: {0}", skill.Name);
            AccessibilityAnnouncementService.Announce(Status);
            if (action == SkillAction.Install) _installedSkillKeys.Add(SkillKey(skill));
            if (action == SkillAction.Remove) _installedSkillKeys.Remove(SkillKey(skill));
            RefreshDiscoverStates();
            InstalledCount = null;
            if (action != SkillAction.Update) UpdateCount = null;
            if (action is SkillAction.Update or SkillAction.Remove)
            {
                string key = SkillKey(skill);
                _results.RemoveAll(item => SkillKey(item).Equals(key, StringComparison.OrdinalIgnoreCase));
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
        _previewOperation?.Cancel();
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
            _updateSkillKeys.Clear();
            _updateSkillKeys.UnionWith(updates.Select(SkillKey));
            RefreshDiscoverStates();
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
