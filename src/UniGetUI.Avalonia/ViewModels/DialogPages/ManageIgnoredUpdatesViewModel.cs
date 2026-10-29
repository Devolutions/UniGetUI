using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels;

public partial class ManageIgnoredUpdatesViewModel : ObservableObject
{
    public string Title { get; } = CoreTools.Translate("Manage ignored updates");
    public string Description { get; } = CoreTools.Translate("The packages listed here won't be taken in account when checking for updates. Click the update button on their right to update them once, or the delete button to stop ignoring their updates.");
    public string ResetLabel { get; } = CoreTools.Translate("Reset list");
    public string ResetConfirm { get; } = CoreTools.Translate("Do you really want to reset the ignored updates list? This action cannot be reverted");
    public string ResetYes { get; } = CoreTools.Translate("Yes");
    public string ResetNo { get; } = CoreTools.Translate("No");
    public string EmptyLabel { get; } = CoreTools.Translate("No ignored updates");
    public string ColName { get; } = CoreTools.Translate("Package Name");
    public string ColId { get; } = CoreTools.Translate("Package ID");
    public string ColVersion { get; } = CoreTools.Translate("Ignored version");
    public string ColNewVersion { get; } = CoreTools.Translate("New version");
    public string ColManager { get; } = CoreTools.Translate("Source");

    public ObservableCollection<IgnoredPackageEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLabel))]
    private bool _hasEntries;

    public bool ShowEmptyLabel => !HasEntries;

    public ManageIgnoredUpdatesViewModel()
    {
        LoadEntries();
    }

    public void Detach()
    {
        foreach (var entry in Entries)
            entry.Detach();
    }

    private void LoadEntries()
    {
        Detach();
        Entries.Clear();

        var db = IgnoredUpdatesDatabase.GetDatabase();
        var managerMap = PEInterface.Managers
            .ToDictionary(m => m.Properties.Name.ToLower(), m => m);

        foreach (var (ignoredId, version) in db.OrderBy(x => x.Key))
        {
            var parts = ignoredId.Split('\\');
            var managerKey = parts[0];
            var packageId = parts.Length > 1 ? parts[^1] : ignoredId;

            string managerDisplay = managerMap.TryGetValue(managerKey, out var mgr)
                ? mgr.DisplayName
                : managerKey;
            string managerIconPath = ManagerIconResolver.Resolve(managerKey);
            string packageName = CoreTools.FormatAsName(packageId);

            string versionDisplay = version == "*"
                ? CoreTools.Translate("All versions")
                : version;

            // Compute the "new version" column like WinUI does
            string currentVersion =
                InstalledPackagesLoader.Instance.GetPackageForId(packageId)?.VersionString
                ?? CoreTools.Translate("Unknown");

            string newVersion;
            IPackage? update = IgnoredUpdatesDatabase.GetAvailableUpdate(ignoredId);
            if (update is not null)
            {
                newVersion = currentVersion + " \u27a4 " + update.NewVersionString;
            }
            else if (currentVersion != CoreTools.Translate("Unknown"))
            {
                newVersion = CoreTools.Translate("Up to date") + $" ({currentVersion})";
            }
            else
            {
                newVersion = CoreTools.Translate("Unknown");
            }

            var entry = new IgnoredPackageEntryViewModel(
                ignoredId, packageId, packageName, managerDisplay,
                managerIconPath, versionDisplay, newVersion, update);
            entry.Removed += OnEntryRemoved;
            entry.UpdateFinished += OnEntryUpdateFinished;
            Entries.Add(entry);
        }

        HasEntries = Entries.Count > 0;
    }

    private void OnEntryRemoved(object? sender, EventArgs e)
    {
        if (sender is IgnoredPackageEntryViewModel entry)
            Entries.Remove(entry);
        HasEntries = Entries.Count > 0;
    }

    private void OnEntryUpdateFinished(object? sender, EventArgs e) => LoadEntries();

    [RelayCommand]
    private async Task ResetAll()
    {
        foreach (var entry in Entries.ToList())
            await entry.RemoveAsync();
    }

}

internal static class ManagerIconResolver
{
    public static string Resolve(string managerKey)
    {
        string name = managerKey switch
        {
            "winget" => "winget",
            "scoop" => "scoop",
            "chocolatey" => "choco",
            "dotnet" => "dotnet",
            "npm" => "node",
            "pip" => "python",
            "powershell" => "powershell",
            "cargo" => "rust",
            "vcpkg" => "vcpkg",
            "steam" => "steam",
            "gog" => "gog",
            "uplay" => "uplay",
            "apt" => "apt",
            "dnf" => "dnf",
            "pacman" => "pacman",
            _ => "ms_store",
        };
        return $"avares://UniGetUI/Assets/Symbols/{name}.svg";
    }
}

public partial class IgnoredPackageEntryViewModel : ObservableObject
{
    public event EventHandler? Removed;

    public string Id { get; }
    public string Name { get; }
    public string Manager { get; }
    public string ManagerIconPath { get; }
    public string VersionDisplay { get; }
    public string NewVersion { get; }
    public string AutomationName { get; }
    public string RemoveAutomationName { get; }
    public string UpdateAutomationName { get; }
    public bool CanUpdate => _update is not null && !IsUpdating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _isUpdating;

    public event EventHandler? UpdateFinished;

    private readonly string _ignoredId;
    private readonly IPackage? _update;
    private UpdatePackageOperation? _operation;
    private int _operationFinished;

    public IgnoredPackageEntryViewModel(
        string ignoredId, string id, string name,
        string manager, string managerIconPath,
        string versionDisplay, string newVersion,
        IPackage? update = null)
    {
        _ignoredId = ignoredId;
        _update = update;
        if (FindRunningUpdate(ignoredId) is { } running)
            Track(running);
        Id = id;
        Name = name;
        Manager = manager;
        ManagerIconPath = managerIconPath;
        VersionDisplay = versionDisplay;
        NewVersion = newVersion;
        AutomationName = CoreTools.Translate("Package {name} from {manager}")
            .Replace("{name}", Name)
            .Replace("{manager}", Manager);
        RemoveAutomationName = CoreTools.Translate("Remove {0} from ignored updates", Name);
        UpdateAutomationName = CoreTools.Translate("Update {0} once, keeping its updates ignored", Name);
    }

    [RelayCommand]
    public async Task Remove() => await RemoveAsync();

    [RelayCommand]
    public async Task Update()
    {
        if (_update is null || IsUpdating)
            return;

        if (IgnoredUpdatesDatabase.GetAvailableUpdate(_ignoredId) is not { } update)
        {
            UpdateFinished?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsUpdating = true;
        try
        {
            var operations = await SoftwareUpdatesPage.LaunchUpdate(
                [update], keepUpdatesPaused: true, beforeStart: Track);
            if (operations.Count == 0)
                IsUpdating = false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not update {Id} from the ignored updates list");
            Logger.Error(ex);
            IsUpdating = false;
        }
    }

    public void Detach()
    {
        if (_operation is { } operation)
            operation.OperationFinished -= OnOperationFinished;
        _operation = null;
    }

    private static UpdatePackageOperation? FindRunningUpdate(string ignoredId) =>
        AvaloniaOperationRegistry.Operations
            .OfType<UpdatePackageOperation>()
            .LastOrDefault(operation =>
                IgnoredUpdatesDatabase.GetIgnoredIdForPackage(operation.Package) == ignoredId
                && (operation.Status is OperationStatus.InQueue or OperationStatus.Running
                    || (operation.Status is OperationStatus.Succeeded && !operation.SuccessHandled.IsCompleted)));

    private void Track(UpdatePackageOperation operation)
    {
        _operation = operation;
        _operationFinished = 0;
        IsUpdating = true;
        operation.OperationFinished += OnOperationFinished;
        if (operation.Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
            OnOperationFinished(operation, EventArgs.Empty);
    }

    private async void OnOperationFinished(object? sender, EventArgs e)
    {
        if (sender is not UpdatePackageOperation operation
            || Interlocked.Exchange(ref _operationFinished, 1) == 1)
            return;

        operation.OperationFinished -= OnOperationFinished;
        try
        {
            await operation.SuccessHandled;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_operation, operation))
                return;

            _operation = null;
            IsUpdating = false;
            UpdateFinished?.Invoke(this, EventArgs.Empty);
        });
    }

    public async Task RemoveAsync()
    {
        await Task.Run(() => IgnoredUpdatesDatabase.Remove(_ignoredId));
        await RestoreToUpdatesAsync();
        Removed?.Invoke(this, EventArgs.Empty);
    }

    private async Task RestoreToUpdatesAsync()
    {
        var parts = _ignoredId.Split('\\');
        var packageId = parts.Length > 1 ? parts[^1] : _ignoredId;

        if (UpgradablePackagesLoader.Instance.IgnoredPackages.TryRemove(packageId, out var pkg)
            && pkg.NewVersionString != pkg.VersionString)
        {
            await UpgradablePackagesLoader.Instance.AddForeign(pkg);
        }

        foreach (var installed in InstalledPackagesLoader.Instance.Packages)
        {
            if (installed.Id == packageId)
            {
                installed.SetTag(PackageTag.Default);
                break;
            }
        }
    }
}
