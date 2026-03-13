using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Represents a single entry in the ignored-updates list.
/// </summary>
public sealed class IgnoredEntryModel
{
    public string IgnoredId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string ManagerName { get; init; } = string.Empty;
    public string DisplayVersion { get; init; } = string.Empty;
}

public partial class IgnoredUpdatesWindow : Window
{
    private readonly ObservableCollection<IgnoredEntryModel> _entries = [];

    public IgnoredUpdatesWindow()
    {
        InitializeComponent();
        ApplyTranslations();
        EntriesList.ItemsSource = _entries;
        LoadEntries();
    }

    private void ApplyTranslations()
    {
        Title = CoreTools.Translate("Manage ignored updates");
        HeaderTitleBlock.Text = CoreTools.Translate("Manage ignored updates");
        HeaderSubtitleBlock.Text = CoreTools.Translate("The packages listed here won't be taken in account when checking for updates. Double-click them or click the button on their right to stop ignoring their updates.");
        ColPackageBlock.Text = CoreTools.Translate("Package");
        ColManagerBlock.Text = CoreTools.Translate("Package manager");
        ColVersionBlock.Text = CoreTools.Translate("Ignored version");
        ConfirmResetTextBlock.Text = CoreTools.Translate("Reset list");
        ConfirmYesButton.Content = CoreTools.Translate("Yes");
        ConfirmCancelButton.Content = CoreTools.Translate("Cancel");
        ResetAllButtonControl.Content = CoreTools.Translate("Reset");
        CloseWindowButton.Content = CoreTools.Translate("Close");
    }

    private void LoadEntries()
    {
        _entries.Clear();

        var db = IgnoredUpdatesDatabase.GetDatabase();
        foreach (var (key, version) in db)
        {
            // key format: "managername\packageId"
            var separatorIdx = key.IndexOf('\\');
            string managerPrefix = separatorIdx >= 0 ? key[..separatorIdx] : string.Empty;
            string packageId = separatorIdx >= 0 ? key[(separatorIdx + 1)..] : key;

            // Try to match to a known manager by name prefix
            string managerName = managerPrefix;
            bool isWinGet = false;
            foreach (var mgr in PEInterface.Managers)
            {
                if (string.Equals(mgr.Name, managerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    managerName = mgr.Name;
                    isWinGet = string.Equals(mgr.Name, "WinGet", StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }

            // Derive display name using the same logic as WinUI
            string displayName;
            if (isWinGet && packageId.Contains('.'))
                displayName = string.Join(' ', packageId.Split('.')[1..]);
            else
                displayName = CoreTools.FormatAsName(packageId);

            // Display version: "*" → "All versions"
            string displayVersion = version == "*"
                ? CoreTools.Translate("All versions")
                : version;

            _entries.Add(new IgnoredEntryModel
            {
                IgnoredId = key,
                DisplayName = displayName,
                ManagerName = managerName,
                DisplayVersion = displayVersion,
            });
        }
    }

    private void RemoveButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IgnoredEntryModel entry })
            return;

        IgnoredUpdatesDatabase.Remove(entry.IgnoredId);
        _entries.Remove(entry);
    }

    private void ResetAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ConfirmResetPanel.IsVisible = true;
    }

    private void ConfirmResetYes_OnClick(object? sender, RoutedEventArgs e)
    {
        ConfirmResetPanel.IsVisible = false;

        var toRemove = new List<IgnoredEntryModel>(_entries);
        foreach (var entry in toRemove)
        {
            IgnoredUpdatesDatabase.Remove(entry.IgnoredId);
            _entries.Remove(entry);
        }
    }

    private void ConfirmResetCancel_OnClick(object? sender, RoutedEventArgs e)
    {
        ConfirmResetPanel.IsVisible = false;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

