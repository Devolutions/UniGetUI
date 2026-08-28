using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels;

public enum ShortcutDialogScope
{
    Desktop,
    StartMenu,
    All,
}

public partial class ManageShortcutsViewModel : ObservableObject
{
    public const int DesktopTabIndex = 0;
    public const int StartMenuFoldersTabIndex = 1;
    public const int StartMenuShortcutsTabIndex = 2;

    public event EventHandler? CloseRequested;

    public ObservableCollection<ShortcutEntryViewModel> Entries { get; } = [];

    public ObservableCollection<StartMenuShortcutEntryViewModel> StartMenuEntries { get; } = [];

    public ObservableCollection<StartMenuFolderRuleViewModel> StartMenuRules { get; } = [];

    [ObservableProperty]
    private bool _autoDelete;

    [ObservableProperty]
    private bool _askAboutStartMenuShortcuts;

    [ObservableProperty]
    private int _selectedTabIndex;

    public bool CanResetList =>
        SelectedTabIndex is DesktopTabIndex or StartMenuShortcutsTabIndex;

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(CanResetList));

    [ObservableProperty]
    private bool _showAllStartMenuShortcuts;

    public bool ShowDesktopTab { get; }

    public bool ShowStartMenuTabs { get; }

    public bool HasStartMenuRules => StartMenuRules.Count > 0;

    public bool HasStartMenuEntries => StartMenuEntries.Count > 0;

    public int AllStartMenuShortcutCount { get; private set; }

    private HashSet<string> _shortcutsOwnedByRules = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, bool> _unsavedStartMenuVerdicts = new(
        StringComparer.OrdinalIgnoreCase
    );

    public string ShowAllStartMenuShortcutsLabel =>
        CoreTools.Translate(
            "Show every Start Menu shortcut ({0})",
            AllStartMenuShortcutCount
        );

    public string StartMenuEmptyStateText
    {
        get
        {
            if (ShowAllStartMenuShortcuts)
                return CoreTools.Translate("No shortcuts were found on the Start Menu.");

            if (HasStartMenuRules)
                return CoreTools.Translate(
                    "Nothing else to review. Every shortcut UniGetUI knows about is on the Start Menu folders tab."
                );

            return CoreTools.Translate(
                "No Start Menu shortcut has been handled by UniGetUI yet. Shortcuts show up here once an install creates them, or once you give a package a folder."
            );
        }
    }

    partial void OnShowAllStartMenuShortcutsChanged(bool value) => LoadStartMenuEntries();

    public ManageShortcutsViewModel(
        IReadOnlyList<string>? desktopShortcuts = null,
        ShortcutDialogScope scope = ShortcutDialogScope.All
    )
    {
        ShowDesktopTab = scope is ShortcutDialogScope.Desktop or ShortcutDialogScope.All;
        ShowStartMenuTabs = scope is ShortcutDialogScope.StartMenu or ShortcutDialogScope.All;

        _autoDelete = CoreSettings.Get(CoreSettings.K.RemoveAllDesktopShortcuts);
        _askAboutStartMenuShortcuts = CoreSettings.Get(
            CoreSettings.K.AskAboutNewStartMenuShortcuts
        );
        _selectedTabIndex = ShowDesktopTab ? DesktopTabIndex : StartMenuFoldersTabIndex;

        StartMenuLocation.Reset();

        if (ShowDesktopTab)
            LoadEntries(desktopShortcuts ?? DesktopShortcutsDatabase.GetAllShortcuts());

        if (ShowStartMenuTabs)
        {
            LoadStartMenuRules();
            LoadStartMenuEntries();
        }
    }

    private void LoadEntries(IReadOnlyList<string> shortcuts)
    {
        Entries.Clear();
        foreach (var path in shortcuts.OrderBy(Path.GetFileName))
        {
            var entry = new ShortcutEntryViewModel(path);
            entry.Removed += OnEntryRemoved;
            Entries.Add(entry);
        }
    }

    private void CaptureUnsavedStartMenuVerdicts()
    {
        foreach (var entry in StartMenuEntries)
        {
            if (entry.HasChanged)
                _unsavedStartMenuVerdicts[entry.Path] = entry.IsDeletable;
            else
                _unsavedStartMenuVerdicts.Remove(entry.Path);
        }
    }

    private void LoadStartMenuEntries()
    {
        CaptureUnsavedStartMenuVerdicts();

        var allShortcuts = StartMenuShortcutsDatabase.GetAllShortcuts();
        AllStartMenuShortcutCount = allShortcuts.Count;

        var shortcuts = (
            ShowAllStartMenuShortcuts
                ? allShortcuts
                : StartMenuShortcutsDatabase.GetTrackedShortcuts()
        )
            .Where(path => !_shortcutsOwnedByRules.Contains(path))
            .ToList();

        StartMenuEntries.Clear();
        foreach (
            var path in shortcuts
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(Path.GetDirectoryName, StringComparer.CurrentCultureIgnoreCase)
        )
        {
            var entry = new StartMenuShortcutEntryViewModel(path);

            if (_unsavedStartMenuVerdicts.TryGetValue(path, out bool unsaved))
                entry.IsDeletable = unsaved;

            entry.Removed += OnStartMenuEntryRemoved;
            StartMenuEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasStartMenuEntries));
        OnPropertyChanged(nameof(AllStartMenuShortcutCount));
        OnPropertyChanged(nameof(ShowAllStartMenuShortcutsLabel));
        OnPropertyChanged(nameof(StartMenuEmptyStateText));
    }

    private void LoadStartMenuRules()
    {
        StartMenuRules.Clear();

        var pendingByPackage = StartMenuShortcutsDatabase
            .GetPendingShortcuts()
            .GroupBy(pending => pending.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(pending => pending.ShortcutPath).ToList()
            );

        var rules = StartMenuShortcutsDatabase.GetRules();
        var shortcutsOnDisk = StartMenuShortcutsDatabase.GetShortcutsOnDisk();
        var availableFolders = StartMenuShortcutsDatabase.GetUserProgramFolders();

        var orderedRules = rules
            .Keys.Union(pendingByPackage.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(packageId =>
            {
                var pending = pendingByPackage.TryGetValue(packageId, out var found)
                    ? found
                    : (IReadOnlyList<string>)[];

                var existing = StartMenuShortcutsDatabase
                    .FindRelocatableShortcuts(packageId, shortcutsOnDisk)
                    .Where(shortcut => !pending.Contains(shortcut, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                return new StartMenuFolderRuleViewModel(
                    packageId,
                    StartMenuShortcutsDatabase.GetRule(packageId) ?? "",
                    pending,
                    existing,
                    availableFolders
                );
            })
            .OrderBy(rule => rule.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var rule in orderedRules)
        {
            rule.Removed += OnStartMenuRuleRemoved;
            StartMenuRules.Add(rule);
        }

        _shortcutsOwnedByRules = new HashSet<string>(
            StartMenuRules.SelectMany(rule => rule.Candidates.Select(candidate => candidate.Path)),
            StringComparer.OrdinalIgnoreCase
        );

        OnPropertyChanged(nameof(HasStartMenuRules));
    }

    private void OnEntryRemoved(object? sender, EventArgs e)
    {
        if (sender is ShortcutEntryViewModel entry)
            Entries.Remove(entry);
    }

    private void OnStartMenuEntryRemoved(object? sender, EventArgs e)
    {
        if (sender is StartMenuShortcutEntryViewModel entry)
        {
            _unsavedStartMenuVerdicts.Remove(entry.Path);
            StartMenuEntries.Remove(entry);
        }

        OnPropertyChanged(nameof(HasStartMenuEntries));
    }

    private void OnStartMenuRuleRemoved(object? sender, EventArgs e)
    {
        if (sender is not StartMenuFolderRuleViewModel rule)
            return;

        StartMenuShortcutsDatabase.RemoveRule(rule.PackageId);
        foreach (var shortcut in rule.PendingShortcuts)
            StartMenuShortcutsDatabase.RemoveFromPending(rule.PackageId, shortcut);

        StartMenuRules.Remove(rule);
        OnPropertyChanged(nameof(HasStartMenuRules));
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (SelectedTabIndex is StartMenuShortcutsTabIndex)
        {
            foreach (var entry in StartMenuEntries.ToList())
                entry.Reset();
            return;
        }

        if (SelectedTabIndex is not DesktopTabIndex)
            return;

        foreach (var entry in Entries.ToList())
            entry.Reset();
    }

    public void SaveChanges()
    {
        if (ShowDesktopTab)
            CoreSettings.Set(CoreSettings.K.RemoveAllDesktopShortcuts, AutoDelete);

        if (ShowStartMenuTabs)
            CoreSettings.Set(
                CoreSettings.K.AskAboutNewStartMenuShortcuts,
                AskAboutStartMenuShortcuts
            );

        foreach (var entry in Entries)
        {
            DesktopShortcutsDatabase.AddToDatabase(
                entry.Path,
                entry.IsDeletable
                    ? DesktopShortcutsDatabase.Status.Delete
                    : DesktopShortcutsDatabase.Status.Maintain);
            DesktopShortcutsDatabase.RemoveFromUnknownShortcuts(entry.Path);

            if (entry.IsDeletable && File.Exists(entry.Path))
                DesktopShortcutsDatabase.DeleteFromDisk(entry.Path);
        }

        CaptureUnsavedStartMenuVerdicts();

        foreach ((string path, bool isDeletable) in _unsavedStartMenuVerdicts)
        {
            StartMenuShortcutsDatabase.SetStatus(
                path,
                isDeletable
                    ? StartMenuShortcutsDatabase.Status.Delete
                    : StartMenuShortcutsDatabase.Status.Maintain);
            StartMenuShortcutsDatabase.RemovePendingShortcuts(path);

            if (!isDeletable)
                continue;

            if (File.Exists(path))
                StartMenuShortcutsDatabase.DeleteFromDisk(path);
        }

        _unsavedStartMenuVerdicts.Clear();

        foreach (var rule in StartMenuRules)
        {
            if (rule.FolderIsInvalid)
                continue;

            foreach (var shortcut in rule.ShortcutsToDelete)
            {
                StartMenuShortcutsDatabase.SetStatus(
                    shortcut,
                    StartMenuShortcutsDatabase.Status.Delete
                );

                if (File.Exists(shortcut))
                    StartMenuShortcutsDatabase.DeleteFromDisk(shortcut);
            }

            StartMenuShortcutsDatabase.SetRule(rule.PackageId, rule.Folder);
            StartMenuShortcutsDatabase.RebaseRelocations(rule.PackageId);
            StartMenuShortcutsDatabase.ApplyRule(
                rule.PackageId,
                rule.ShortcutsToMove,
                out var movedShortcuts
            );

            foreach (var candidate in rule.Candidates)
            {
                if (
                    candidate.IsMoveSelected
                    && !movedShortcuts.Contains(candidate.Path, StringComparer.OrdinalIgnoreCase)
                )
                    continue;

                StartMenuShortcutsDatabase.RemoveFromPending(rule.PackageId, candidate.Path);
            }
        }
    }

    [RelayCommand]
    public void SaveAndClose()
    {
        if (StartMenuRules.Any(rule => rule.FolderIsInvalid))
        {
            SelectedTabIndex = StartMenuFoldersTabIndex;
            return;
        }

        SaveChanges();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

public static class StartMenuLocation
{
    private static IReadOnlyList<string>? _roots;

    public static void Reset() => _roots = null;

    public static string Describe(string shortcutPath)
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (string.IsNullOrEmpty(directory))
            return "";

        _roots ??= StartMenuShortcutsDatabase.GetShortcutRoots();

        for (int index = 0; index < _roots.Count; index++)
        {
            string root = Path.TrimEndingDirectorySeparator(_roots[index]);

            bool isRoot = directory.Equals(root, StringComparison.OrdinalIgnoreCase);
            bool isUnderRoot = directory.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );

            if (!isRoot && !isUnderRoot)
                continue;

            string relative = isRoot
                ? CoreTools.Translate("Start Menu root")
                : directory[root.Length..].Trim(Path.DirectorySeparatorChar);

            return index is 0
                ? relative
                : $"{CoreTools.Translate("All users")} \u00b7 {relative}";
        }

        return directory;
    }
}

public partial class ShortcutEntryViewModel : ObservableObject
{
    public event EventHandler? Removed;

    public string Path { get; }
    public string Name { get; }
    public bool ExistsOnDisk => File.Exists(Path);

    [ObservableProperty]
    private bool _isDeletable;

    public ShortcutEntryViewModel(string path)
    {
        Path = path;
        var filename = System.IO.Path.GetFileName(path);
        Name = string.Join('.', filename.Split('.')[..^1]);
        IsDeletable = DesktopShortcutsDatabase.GetStatus(path) is DesktopShortcutsDatabase.Status.Delete;
    }

    [RelayCommand]
    public void Open() => _ = CoreTools.ShowFileOnExplorer(Path);

    public void Reset()
    {
        DesktopShortcutsDatabase.AddToDatabase(Path, DesktopShortcutsDatabase.Status.Unknown);
        Removed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void Remove() => Reset();
}

public partial class StartMenuShortcutEntryViewModel : ObservableObject
{
    public event EventHandler? Removed;

    private readonly bool _initiallyDeletable;

    public string Path { get; }
    public string Name { get; }
    public string Location { get; }
    public bool ExistsOnDisk => File.Exists(Path);
    public bool HasChanged => IsDeletable != _initiallyDeletable;

    /// A row listed only because of a verdict can be untracked; one listed because
    /// UniGetUI relocated it cannot, since the relocation record keeps bringing it back.
    public bool CanStopTracking { get; }

    [ObservableProperty]
    private bool _isDeletable;

    public StartMenuShortcutEntryViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Location = StartMenuLocation.Describe(path);
        var status = StartMenuShortcutsDatabase.GetStatus(path);
        CanStopTracking = status is not StartMenuShortcutsDatabase.Status.Unknown;

        _initiallyDeletable = status is StartMenuShortcutsDatabase.Status.Delete;
        IsDeletable = _initiallyDeletable;
    }

    [RelayCommand]
    public void Open() => _ = CoreTools.ShowFileOnExplorer(Path);

    public void Reset()
    {
        StartMenuShortcutsDatabase.SetStatus(Path, StartMenuShortcutsDatabase.Status.Unknown);
        Removed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void Remove() => Reset();
}

public partial class StartMenuFolderRuleViewModel : ObservableObject
{
    public event EventHandler? Removed;

    public string PackageId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> PendingShortcuts { get; }
    public ObservableCollection<ShortcutCandidateViewModel> Candidates { get; } = [];

    public bool HasCandidates => Candidates.Count > 0;

    public IReadOnlyList<(string Path, string? NewName)> ShortcutsToMove =>
        Candidates
            .Where(candidate => candidate.IsMoveSelected)
            .Select(candidate => (candidate.Path, (string?)candidate.NewName))
            .ToList();

    public IReadOnlyList<string> ShortcutsToDelete =>
        Candidates
            .Where(candidate => candidate.IsDeleteSelected)
            .Select(candidate => candidate.Path)
            .ToList();

    public bool NeedsFolder =>
        string.IsNullOrWhiteSpace(Folder)
        && Candidates.Any(candidate => candidate.IsMoveSelected);

    public bool FolderIsInvalid =>
        !string.IsNullOrWhiteSpace(Folder)
        && StartMenuShortcutsDatabase.ResolveTargetDirectory(Folder) is null;

    public ObservableCollection<string> FolderOptions { get; } = [];

    public string NoFolderOption { get; }

    public string NewFolderOption { get; }

    [ObservableProperty]
    private string _folder;

    [ObservableProperty]
    private string _selectedFolderOption;

    [ObservableProperty]
    private string _newFolderName = "";

    [ObservableProperty]
    private bool _isCreatingFolder;

    partial void OnFolderChanged(string value)
    {
        OnPropertyChanged(nameof(NeedsFolder));
        OnPropertyChanged(nameof(FolderIsInvalid));
    }

    partial void OnSelectedFolderOptionChanged(string value)
    {
        if (value == NewFolderOption)
        {
            IsCreatingFolder = true;
            Folder = NewFolderName.Trim();
            return;
        }

        IsCreatingFolder = false;
        Folder = value == NoFolderOption ? "" : value;
    }

    partial void OnNewFolderNameChanged(string value)
    {
        if (IsCreatingFolder)
            Folder = value.Trim();
    }

    public StartMenuFolderRuleViewModel(
        string packageId,
        string folder,
        IReadOnlyList<string> pendingShortcuts,
        IReadOnlyList<string> existingShortcuts,
        IReadOnlyList<string> availableFolders
    )
    {
        PackageId = packageId;
        PendingShortcuts = pendingShortcuts;
        _folder = folder;

        NoFolderOption = CoreTools.Translate("Leave them where they are");
        NewFolderOption = CoreTools.Translate("New folder...");

        FolderOptions.Add(NoFolderOption);

        if (folder.Length > 0 && !availableFolders.Contains(folder))
            FolderOptions.Add(folder);

        foreach (var available in availableFolders)
            FolderOptions.Add(available);

        FolderOptions.Add(NewFolderOption);

        _selectedFolderOption = folder.Length > 0 ? folder : NoFolderOption;

        var parts = packageId.Split('\\');
        DisplayName = parts.Length is 2 ? $"{parts[1]} ({parts[0]})" : packageId;

        foreach (var shortcut in pendingShortcuts)
            Candidates.Add(new ShortcutCandidateViewModel(shortcut, true));

        foreach (var shortcut in existingShortcuts)
            Candidates.Add(new ShortcutCandidateViewModel(shortcut, false));

        foreach (var candidate in Candidates)
            candidate.SelectionChanged = () => OnPropertyChanged(nameof(NeedsFolder));
    }

    [RelayCommand]
    public void Remove() => Removed?.Invoke(this, EventArgs.Empty);
}

public partial class ShortcutCandidateViewModel : ObservableObject
{
    public Action? SelectionChanged { get; set; }

    public string Path { get; }
    public string Name { get; }
    public string Location { get; }
    public bool IsNew { get; }

    public string MoveAutomationName => CoreTools.Translate("Move {0} into the folder", Name);

    public string DeleteAutomationName =>
        CoreTools.Translate("Delete {0}, now and whenever an upgrade creates it again", Name);

    [ObservableProperty]
    private bool _isMoveSelected;

    [ObservableProperty]
    private bool _isDeleteSelected;

    [ObservableProperty]
    private string _newName = "";

    public ShortcutCandidateViewModel(string path, bool isNew)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Location = StartMenuLocation.Describe(path);
        IsNew = isNew;
        IsMoveSelected = isNew;
    }

    partial void OnIsMoveSelectedChanged(bool value)
    {
        if (value && IsDeleteSelected)
            IsDeleteSelected = false;

        SelectionChanged?.Invoke();
    }

    partial void OnIsDeleteSelectedChanged(bool value)
    {
        if (value && IsMoveSelected)
            IsMoveSelected = false;

        SelectionChanged?.Invoke();
    }

    [RelayCommand]
    public void Open() => _ = CoreTools.ShowFileOnExplorer(Path);
}
