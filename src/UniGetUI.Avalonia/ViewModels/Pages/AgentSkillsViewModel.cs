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
    private CancellationTokenSource? _operation;
    private bool _stopped;
    private string _notifiedUpdates = "";
    internal Action<int>? UpdatesAvailable { get; set; }
    public ObservableCollection<SkillItem> Skills { get; } = [];
    public IReadOnlyList<string> Modes { get; } =
        [CoreTools.Translate("Discover"), CoreTools.Translate("Installed"), CoreTools.Translate("Updates")];
    [ObservableProperty] private int _mode;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SkillItem? _selectedSkill;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isBusy;
    public bool CanInstall => !_stopped && !IsBusy && Mode == 0 && SelectedSkill is not null;
    public bool CanRemove => !_stopped && !IsBusy && Mode == 1 && SelectedSkill is not null;
    public bool CanUpdate => !_stopped && !IsBusy && Mode == 2 && SelectedSkill is not null;

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
    }
    partial void OnModeChanged(int value)
    {
        SelectedSkill = null;
        Skills.Clear();
        NotifyActions();
    }
    partial void OnSelectedSkillChanged(SkillItem? value) => NotifyActions();
    partial void OnIsBusyChanged(bool value) => NotifyActions();
    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanUpdate));
    }

    [RelayCommand]
    private async Task RefreshAsync() => await RunAsync(async (progress, token) =>
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
        Skills.Clear();
        foreach (var skill in skills) Skills.Add(skill);
        Status = CoreTools.Translate("{0} skills", Skills.Count);
    });

    [RelayCommand] private Task InstallAsync() => ChangeAsync(SkillAction.Install);
    [RelayCommand] private Task UpdateAsync() => ChangeAsync(SkillAction.Update);
    [RelayCommand] private Task RemoveAsync() => ChangeAsync(SkillAction.Remove);
    private async Task ChangeAsync(SkillAction action)
    {
        if (SelectedSkill is not { } skill) return;
        await RunAsync(async (progress, token) =>
        {
            if (action == SkillAction.Install && _prepareSource is not null && _service.GetInstallSource(skill) is { } source)
                await _prepareSource(source);
            token.ThrowIfCancellationRequested();
            await _service.PerformAsync(skill, action, progress, token);
            Status = CoreTools.Translate("Completed: {0}", skill.Name);
            AccessibilityAnnouncementService.Announce(Status);
            if (action is SkillAction.Update or SkillAction.Remove) Skills.Remove(skill);
        });
    }
    private async Task RunAsync(Func<IProgress<string>, CancellationToken, Task> action)
    {
        if (_stopped || IsBusy) return;
        using var cancel = new CancellationTokenSource();
        _operation = cancel;
        IsBusy = true;
        Output = "";
        Status = CoreTools.Translate("Working…");
        try
        {
            await action(new Progress<string>(line =>
        {
            if (_stopped || !ReferenceEquals(_operation, cancel)) return;
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
                AccessibilityAnnouncementService.Announce(Status, AutomationLiveSetting.Assertive);
            }
        }
        finally { _operation = null; IsBusy = false; }
    }
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    public void Stop()
    {
        _stopped = true;
        _operation?.Cancel();
        _service.ClearRegistryResults();
        NotifyActions();
    }

    public async Task CheckForUpdatesInBackgroundAsync()
    {
        await RunAsync(async (progress, token) =>
        {
            var updates = await Task.Run(() => _service.CheckUpdates(progress, token), token);
            if (_stopped || token.IsCancellationRequested) return;
            string signature = string.Join('|', updates.Select(skill => $"{skill.SourceName}:{skill.Id}:{skill.NewVersion}").Order());
            if (Mode == 2)
            {
                Skills.Clear();
                foreach (var skill in updates) Skills.Add(skill);
            }
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
            Status = CoreTools.Translate("{0} skill updates", updates.Count);
        });
    }
}
