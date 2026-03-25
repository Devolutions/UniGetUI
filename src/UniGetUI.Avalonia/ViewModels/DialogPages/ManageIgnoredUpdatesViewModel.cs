using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels;

public partial class ManageIgnoredUpdatesViewModel : ObservableObject
{
    public event EventHandler? CloseRequested;

    public string Title { get; } = CoreTools.Translate("Manage ignored updates");
    public string Description { get; } = CoreTools.Translate("The packages listed here won't be taken in account when checking for updates. Double-click them or click the button on their right to stop ignoring their updates.");
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

    private void LoadEntries()
    {
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
            string managerIconPath = ResolveManagerIcon(managerKey);
            string packageName = CoreTools.FormatAsName(packageId);

            string versionDisplay = version == "*"
                ? CoreTools.Translate("All versions")
                : version;

            // Compute the "new version" column like WinUI does
            string currentVersion =
                InstalledPackagesLoader.Instance.GetPackageForId(packageId)?.VersionString
                ?? CoreTools.Translate("Unknown");

            string newVersion;
            if (UpgradablePackagesLoader.Instance.IgnoredPackages
                    .TryGetValue(packageId, out var upgradable)
                && upgradable.NewVersionString != upgradable.VersionString)
            {
                newVersion = currentVersion + " \u27a4 " + upgradable.NewVersionString;
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
                managerIconPath, versionDisplay, newVersion);
            entry.Removed += OnEntryRemoved;
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

    [RelayCommand]
    private async Task ResetAll()
    {
        foreach (var entry in Entries.ToList())
            await entry.RemoveAsync();
    }

    private static string ResolveManagerIcon(string managerKey) =>
        (managerKey switch
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
            _ => "ms_store",
        }) is var name
            ? $"avares://UniGetUI.Avalonia/Assets/Symbols/{name}.svg"
            : $"avares://UniGetUI.Avalonia/Assets/Symbols/ms_store.svg";
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

    private readonly string _ignoredId;

    public IgnoredPackageEntryViewModel(
        string ignoredId, string id, string name,
        string manager, string managerIconPath,
        string versionDisplay, string newVersion)
    {
        _ignoredId = ignoredId;
        Id = id;
        Name = name;
        Manager = manager;
        ManagerIconPath = managerIconPath;
        VersionDisplay = versionDisplay;
        NewVersion = newVersion;
    }

    [RelayCommand]
    public async Task Remove() => await RemoveAsync();

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
