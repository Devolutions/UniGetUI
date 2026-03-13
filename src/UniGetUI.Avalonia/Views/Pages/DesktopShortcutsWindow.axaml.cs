using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Avalonia.Views.Pages;

// ── View model for a single shortcut row ─────────────────────────────────────

public sealed class ShortcutItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Name { get; }

    private bool _isDeletable;
    public bool IsDeletable
    {
        get => _isDeletable;
        set
        {
            if (_isDeletable == value) return;
            _isDeletable = value;
            OnPropertyChanged();
        }
    }

    public bool ExistsOnDisk => File.Exists(Path);

    public string NotOnDiskText { get; } = CoreTools.Translate("Not found");

    public ShortcutItem(string path, bool isDeletable)
    {
        Path = path;
        Name = string.Join('.', path.Split('\\')[^1].Split('.')[..^1]);
        _isDeletable = isDeletable;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── Window ────────────────────────────────────────────────────────────────────

/// <summary>
/// Lets users review desktop shortcuts detected by UniGetUI and choose
/// which ones to delete automatically on future upgrades.
/// </summary>
public partial class DesktopShortcutsWindow : Window
{
    private readonly ObservableCollection<ShortcutItem> _shortcuts = [];

    public DesktopShortcutsWindow()
        : this(null)
    {
    }

    /// <param name="newShortcuts">
    /// When non-null, only these shortcuts are displayed (new-shortcut flow).
    /// When null, all known shortcuts from the database are loaded.
    /// </param>
    public DesktopShortcutsWindow(IReadOnlyList<string>? newShortcuts)
    {
        Title = CoreTools.Translate("Automatic desktop shortcut remover");
        InitializeComponent();

        TitleBlock.Text = CoreTools.Translate("Automatic desktop shortcut remover");
        DescriptionBlock.Text = CoreTools.Translate(
            "Here you can change UniGetUI's behaviour regarding the following shortcuts. " +
            "Checking a shortcut will make UniGetUI delete it if it gets created on a future upgrade. " +
            "Unchecking it will keep the shortcut intact.");

        AutoDeleteAllCheckBox.Content = CoreTools.Translate("When new shortcuts are detected, delete them automatically instead of showing this dialog.");
        AutoDeleteAllCheckBox.IsChecked = Settings.Get(Settings.K.RemoveAllDesktopShortcuts);

        ShortcutsListControl.ItemsSource = _shortcuts;
        LoadShortcuts(newShortcuts ?? DesktopShortcutsDatabase.GetAllShortcuts());

        if (_shortcuts.Count == 0)
        {
            EmptyHintBlock.Text = CoreTools.Translate("No new shortcuts were found during the scan.");
            EmptyHintBlock.IsVisible = true;
        }

        CloseBtn.Content = CoreTools.Translate("Close");
        SaveCloseBtn.Content = CoreTools.Translate("Save and close");
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private void LoadShortcuts(IReadOnlyList<string> paths)
    {
        _shortcuts.Clear();
        var items = paths.Select(p =>
        {
            var status = DesktopShortcutsDatabase.GetStatus(p);
            return new ShortcutItem(p, status is DesktopShortcutsDatabase.Status.Delete);
        })
        .OrderBy(s => s.Name)
        .ToList();

        foreach (var item in items)
            _shortcuts.Add(item);
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void SaveChanges()
    {
        foreach (var shortcut in _shortcuts)
        {
            DesktopShortcutsDatabase.AddToDatabase(
                shortcut.Path,
                shortcut.IsDeletable
                    ? DesktopShortcutsDatabase.Status.Delete
                    : DesktopShortcutsDatabase.Status.Maintain);

            DesktopShortcutsDatabase.RemoveFromUnknownShortcuts(shortcut.Path);

            if (shortcut.IsDeletable && File.Exists(shortcut.Path))
                DesktopShortcutsDatabase.DeleteFromDisk(shortcut.Path);
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void AutoDeleteAllCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        bool isChecked = AutoDeleteAllCheckBox.IsChecked == true;
        Settings.Set(Settings.K.RemoveAllDesktopShortcuts, isChecked);
        if (isChecked)
        {
            SaveChanges();
            Close();
        }
    }

    private void CloseBtn_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void SaveCloseBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        SaveChanges();
        Close();
    }
}
