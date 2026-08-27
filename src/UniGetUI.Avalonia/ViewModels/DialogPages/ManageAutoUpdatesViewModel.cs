using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels;

public partial class ManageAutoUpdatesViewModel : ObservableObject
{
    public string Title { get; } = CoreTools.Translate("Manage automatic updates");
    public string Description { get; } = CoreTools.Translate("Tick the packages that should be updated on their own when the scheduled \"Install available updates\" task runs.");
    public string SearchPlaceholder { get; } = CoreTools.Translate("Search for a package");
    public string OnlyMarkedLabel { get; } = CoreTools.Translate("Only marked");
    public string SelectAllLabel { get; } = CoreTools.Translate("Select all");
    public string LoadingLabel { get; } = CoreTools.Translate("Loading packages");

    public string ScopeWarning { get; }
    public bool HasScopeWarning => ScopeWarning.Length > 0;

    private readonly List<AutoUpdateCandidateViewModel> _candidates = [];

    public ObservableCollection<AutoUpdateCandidateViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLabel))]
    private bool _hasEntries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLabel))]
    private bool _isLoading = true;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _onlyMarked;
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private bool? _allMarked = false;
    [ObservableProperty] private string _emptyLabel = "";

    private bool _suppressSelectionRecompute;

    public bool ShowEmptyLabel => !HasEntries && !IsLoading;

    public ManageAutoUpdatesViewModel()
    {
        ScopeWarning = BuildScopeWarning();
        _ = InitializeAsync();
    }

    private static string BuildScopeWarning()
    {
        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);

        if (!schedule.Enabled)
            return CoreTools.Translate("Turn on \"Install available updates\" in the scheduled maintenance settings for this list to take effect.");

        if (schedule.InstallTargets is ScheduleInstallTargets.AllPackages)
            return CoreTools.Translate("Every upgradable package is currently installed automatically, so this list is not being used.");

        return "";
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (InstalledPackagesLoader.Instance is { } loader && !loader.IsLoaded)
            {
                if (loader.IsLoading)
                    await loader.WaitForCurrentLoadAsync();
                else
                    await loader.ReloadPackages();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the installed packages for the automatic updates editor");
            Logger.Error(ex);
        }

        IsLoading = false;
        BuildCandidates();
        ApplyFilter();
    }

    private void BuildCandidates()
    {
        _candidates.Clear();

        var marked = AutoUpdatesDatabase.GetDatabase().Keys.ToHashSet(StringComparer.Ordinal);
        var ignored = IgnoredUpdatesDatabase.GetDatabase().Keys.ToHashSet(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var package in InstalledPackagesLoader.Instance?.Packages ?? [])
        {
            string autoUpdateId = AutoUpdatesDatabase.GetIdForPackage(package);
            if (!covered.Add(autoUpdateId))
                continue;

            _candidates.Add(new AutoUpdateCandidateViewModel(
                autoUpdateId,
                package.Id,
                package.Name,
                package.VersionString,
                package.Manager.DisplayName,
                ManagerIconResolver.Resolve(package.Manager.Properties.Name.ToLower()),
                isInstalled: true,
                isIgnored: ignored.Contains(autoUpdateId)
                    && IgnoredUpdatesDatabase.HasUpdatesIgnored(autoUpdateId),
                isMarked: marked.Contains(autoUpdateId),
                OnMarkChanged,
                package));
        }

        var managerMap = PEInterface.Managers.ToDictionary(m => m.Properties.Name.ToLower(), m => m);

        foreach (string autoUpdateId in marked.Where(id => !covered.Contains(id)))
        {
            var parts = autoUpdateId.Split('\\');
            string managerKey = parts[0];
            string packageId = parts.Length > 1 ? parts[^1] : autoUpdateId;

            _candidates.Add(new AutoUpdateCandidateViewModel(
                autoUpdateId,
                packageId,
                CoreTools.FormatAsName(packageId),
                CoreTools.Translate("Not installed"),
                managerMap.TryGetValue(managerKey, out var mgr) ? mgr.DisplayName : managerKey,
                ManagerIconResolver.Resolve(managerKey),
                isInstalled: false,
                isIgnored: false,
                isMarked: true,
                OnMarkChanged));
        }

        _candidates.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ApplyFilter()
    {
        string query = SearchQuery.Trim();

        Entries.Clear();
        foreach (var candidate in _candidates)
        {
            if (OnlyMarked && !candidate.IsMarked)
                continue;

            if (query.Length > 0
                && !candidate.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !candidate.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            Entries.Add(candidate);
        }

        HasEntries = Entries.Count > 0;
        EmptyLabel = OnlyMarked && _candidates.All(c => !c.IsMarked)
            ? CoreTools.Translate("No packages are updated automatically")
            : CoreTools.Translate("No packages match the search");
        RefreshSummary();
        RefreshAllMarkedState();
    }

    partial void OnAllMarkedChanged(bool? value)
    {
        if (_suppressSelectionRecompute || value is null) return;
        SetAllFiltered(value.Value);
    }

    private void SetAllFiltered(bool marked)
    {
        var affected = Entries.Where(e => e.CanBeMarked && e.IsMarked != marked).ToList();
        if (affected.Count is 0)
        {
            RefreshAllMarkedState();
            return;
        }

        var ids = affected.Select(e => e.AutoUpdateId).ToList();
        if (marked)
            AutoUpdatesDatabase.AddRange(ids);
        else
            AutoUpdatesDatabase.RemoveRange(ids);

        foreach (var candidate in affected)
            candidate.SetMarkedWithoutPersisting(marked);

        if (OnlyMarked && !marked)
        {
            ApplyFilter();
            return;
        }

        RefreshSummary();
        RefreshAllMarkedState();
    }

    private void RefreshAllMarkedState()
    {
        var selectable = Entries.Where(e => e.CanBeMarked).ToList();
        bool? state = selectable.Count is 0 || selectable.All(e => !e.IsMarked)
            ? false
            : selectable.All(e => e.IsMarked) ? true : null;

        _suppressSelectionRecompute = true;
        try { AllMarked = state; }
        finally { _suppressSelectionRecompute = false; }
    }

    private void RefreshSummary()
    {
        int marked = _candidates.Count(c => c.IsMarked);
        SelectionSummary = marked is 0
            ? CoreTools.Translate("No packages are updated automatically")
            : CoreTools.Translate("{0} of {1} packages are updated automatically", marked, _candidates.Count);
    }

    private void OnMarkChanged(AutoUpdateCandidateViewModel candidate)
    {
        if (OnlyMarked && !candidate.IsMarked)
        {
            ApplyFilter();
            return;
        }

        RefreshSummary();
        RefreshAllMarkedState();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    partial void OnOnlyMarkedChanged(bool value) => ApplyFilter();
}

public partial class AutoUpdateCandidateViewModel : ObservableObject, IPackageIconHost
{
    public string AutoUpdateId { get; }

    private readonly IPackage? _package;
    private int _iconLoadStarted;
    private readonly Action<AutoUpdateCandidateViewModel> _onMarkChanged;
    private bool _suppressPersist;

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Manager { get; }
    public string ManagerIconPath { get; }
    public bool IsInstalled { get; }
    public bool IsIgnored { get; }
    public bool CanBeMarked => !IsIgnored || IsMarked;
    public double RowOpacity => IsInstalled ? 1.0 : 0.6;
    public string StatusTip { get; }
    public string AutomationName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeMarked))]
    private bool _isMarked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    private Bitmap? _iconBitmap;

    public bool HasCustomIcon => IconBitmap is not null;

    public AutoUpdateCandidateViewModel(
        string autoUpdateId, string id, string name, string version,
        string manager, string managerIconPath,
        bool isInstalled, bool isIgnored, bool isMarked,
        Action<AutoUpdateCandidateViewModel> onMarkChanged,
        IPackage? package = null)
    {
        AutoUpdateId = autoUpdateId;
        _onMarkChanged = onMarkChanged;
        _package = package;
        Id = id;
        Name = name;
        Version = version;
        Manager = manager;
        ManagerIconPath = managerIconPath;
        IsInstalled = isInstalled;
        IsIgnored = isIgnored;

        StatusTip = isIgnored
            ? CoreTools.Translate("This package has its updates ignored, so it will never be updated automatically")
            : isInstalled
                ? CoreTools.Translate("Package {name} from {manager}")
                    .Replace("{name}", name)
                    .Replace("{manager}", manager)
                : CoreTools.Translate("This package is no longer installed");

        AutomationName = CoreTools.Translate("Package {name} from {manager}")
            .Replace("{name}", name)
            .Replace("{manager}", manager);

        _suppressPersist = true;
        IsMarked = isMarked;
        _suppressPersist = false;
    }

    public void EnsureIconLoaded()
    {
        if (_package is null) return;
        if (Interlocked.Exchange(ref _iconLoadStarted, 1) != 0) return;
        _ = LoadIconAsync();
    }

    private async Task LoadIconAsync()
    {
        try
        {
            var bitmap = await PackageWrapper.LoadSharedIconAsync(_package!).ConfigureAwait(false);
            if (bitmap is null) return;

            await Dispatcher.UIThread.InvokeAsync(() => IconBitmap = bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load the icon for {AutoUpdateId}: {ex.Message}");
            Interlocked.Exchange(ref _iconLoadStarted, 0);
        }
    }

    public void SetMarkedWithoutPersisting(bool value)
    {
        _suppressPersist = true;
        IsMarked = value;
        _suppressPersist = false;
    }

    partial void OnIsMarkedChanged(bool value)
    {
        if (_suppressPersist) return;

        try
        {
            if (value)
                AutoUpdatesDatabase.Add(AutoUpdateId);
            else if (AutoUpdatesDatabase.IsAutoUpdated(AutoUpdateId))
                AutoUpdatesDatabase.Remove(AutoUpdateId);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not change the automatic update state of {AutoUpdateId}");
            Logger.Error(ex);
        }

        _onMarkChanged(this);
    }
}
