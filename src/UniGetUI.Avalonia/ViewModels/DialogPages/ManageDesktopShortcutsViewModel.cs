using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels;

public partial class ManageDesktopShortcutsViewModel : ObservableObject
{
    public event EventHandler? CloseRequested;

    public ObservableCollection<ShortcutEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private bool _autoDelete;

    partial void OnAutoDeleteChanged(bool value) =>
        CoreSettings.Set(CoreSettings.K.RemoveAllDesktopShortcuts, value);

    public ManageDesktopShortcutsViewModel(IReadOnlyList<string>? shortcuts = null)
    {
        _autoDelete = CoreSettings.Get(CoreSettings.K.RemoveAllDesktopShortcuts);
        LoadEntries(shortcuts ?? DesktopShortcutsDatabase.GetAllShortcuts());
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

    private void OnEntryRemoved(object? sender, EventArgs e)
    {
        if (sender is ShortcutEntryViewModel entry)
            Entries.Remove(entry);
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (var entry in Entries.ToList())
            entry.Reset();
    }

    public void SaveChanges()
    {
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
    }

    [RelayCommand]
    public void SaveAndClose()
    {
        SaveChanges();
        CloseRequested?.Invoke(this, EventArgs.Empty);
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
