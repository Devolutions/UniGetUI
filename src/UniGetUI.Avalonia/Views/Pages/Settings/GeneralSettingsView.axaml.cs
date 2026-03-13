using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using UniGetUI.Avalonia;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class GeneralSettingsView : UserControl, ISettingsSectionView
{
    private bool _isLoading;
    private string _initialPreferredLanguage = "default";

    private Border RestartNoticeCardControl => GetControl<Border>("RestartNoticeCard");

    private Button RestartAppBtnCtrl => GetControl<Button>("RestartAppButton");

    private ComboBox LanguageSelectorControl => GetControl<ComboBox>("LanguageSelector");

    private CheckBox ShowVersionOnTitleBarCheckBoxControl => GetControl<CheckBox>("ShowVersionOnTitleBarCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock RestartTitleText => GetControl<TextBlock>("RestartTitleBlock");

    private TextBlock RestartDescriptionText => GetControl<TextBlock>("RestartDescriptionBlock");

    private TextBlock LanguageTitleText => GetControl<TextBlock>("LanguageTitleBlock");

    private TextBlock LanguageDescriptionText => GetControl<TextBlock>("LanguageDescriptionBlock");

    private TextBlock LanguageHintText => GetControl<TextBlock>("LanguageHintBlock");

    private TextBlock TitleBarTitleText => GetControl<TextBlock>("TitleBarTitleBlock");

    private TextBlock TitleBarDescriptionText => GetControl<TextBlock>("TitleBarDescriptionBlock");

    private TextBlock TitleBarHintText => GetControl<TextBlock>("TitleBarHintBlock");

    private TextBlock TelemetryTitleText => GetControl<TextBlock>("TelemetryTitleBlock");

    private TextBlock TelemetryDescriptionText => GetControl<TextBlock>("TelemetryDescriptionBlock");

    private CheckBox DisableTelemetryCheckBoxControl => GetControl<CheckBox>("DisableTelemetryCheckBox");

    private Button ManageTelemetryBtnCtrl => GetControl<Button>("ManageTelemetryBtn");

    private TextBlock UpdaterTitleText => GetControl<TextBlock>("UpdaterTitleBlock");
    private TextBlock UpdaterDescriptionText => GetControl<TextBlock>("UpdaterDescriptionBlock");
    private CheckBox EnableAutoUpdateCheckBoxControl => GetControl<CheckBox>("EnableAutoUpdateCheckBox");
    private CheckBox EnableBetaCheckBoxControl => GetControl<CheckBox>("EnableBetaCheckBox");
    private TextBlock UpdaterHintText => GetControl<TextBlock>("UpdaterHintBlock");

    private TextBlock SettingsMgmtTitleText => GetControl<TextBlock>("SettingsMgmtTitleBlock");

    private TextBlock SettingsMgmtDescriptionText => GetControl<TextBlock>("SettingsMgmtDescriptionBlock");

    private Button CheckForUpdatesNowBtnCtrl => GetControl<Button>("CheckForUpdatesNowBtn");

    private Button ImportSettingsBtnCtrl => GetControl<Button>("ImportSettingsBtn");

    private Button ExportSettingsBtnCtrl => GetControl<Button>("ExportSettingsBtn");

    private TextBlock ResetTitleText => GetControl<TextBlock>("ResetTitleBlock");

    private TextBlock ResetDescriptionText => GetControl<TextBlock>("ResetDescriptionBlock");

    private Button ResetSettingsBtnCtrl => GetControl<Button>("ResetSettingsBtn");

    public GeneralSettingsView()
    {
        InitializeComponent();
        ShowVersionOnTitleBarCheckBoxControl.Click += ShowVersionOnTitleBarCheckBox_OnClick;
        DisableTelemetryCheckBoxControl.Click += DisableTelemetryCheckBox_OnClick;
        EnableAutoUpdateCheckBoxControl.Click += EnableAutoUpdateCheckBox_OnClick;
        EnableBetaCheckBoxControl.Click += EnableBetaCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("General preferences");
        SectionSubtitle = CoreTools.Translate("Language, theme and other miscellaneous preferences");
        SectionStatus = CoreTools.Translate("Manage UniGetUI settings");

        ApplyLocalizedText();
        PopulateLanguageSelector();
        LoadStoredValues();
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Language");
        LeadDescriptionText.Text = CoreTools.Translate("WingetUI display language:");
        RestartTitleText.Text = CoreTools.Translate("Restart required");
        RestartDescriptionText.Text = CoreTools.Translate("Restart WingetUI to fully apply changes");
        RestartAppBtnCtrl.Content = CoreTools.Translate("Restart UniGetUI");
        LanguageTitleText.Text = CoreTools.Translate("Language");
        LanguageDescriptionText.Text = CoreTools.Translate("WingetUI display language:");
        LanguageHintText.Text = CoreTools.Translate("Is your language missing or incomplete?");
        TitleBarTitleText.Text = CoreTools.Translate("General preferences");
        TitleBarDescriptionText.Text = CoreTools.Translate("Show UniGetUI's version and build number on the titlebar.");
        ShowVersionOnTitleBarCheckBoxControl.Content = CoreTools.Translate("Show UniGetUI's version and build number on the titlebar.");
        TitleBarHintText.Text = string.Empty;
        TelemetryTitleText.Text = CoreTools.Translate("Telemetry");
        TelemetryDescriptionText.Text = CoreTools.Translate("UniGetUI collects anonymous usage data in order to improve the user experience.");
        DisableTelemetryCheckBoxControl.Content = CoreTools.Translate("Share anonymous usage data");
        ManageTelemetryBtnCtrl.Content = CoreTools.Translate("Manage telemetry settings");
        UpdaterTitleText.Text = CoreTools.Translate("UniGetUI updater");
        UpdaterDescriptionText.Text = CoreTools.Translate("Check for updates regularly, and automatically install available ones.");
        EnableAutoUpdateCheckBoxControl.Content = CoreTools.Translate("Update WingetUI automatically");
        EnableBetaCheckBoxControl.Content = CoreTools.Translate("Install prerelease versions of UniGetUI");
        UpdaterHintText.Text = string.Empty;
        CheckForUpdatesNowBtnCtrl.Content = CoreTools.Translate("Check for updates");
        SettingsMgmtTitleText.Text = CoreTools.Translate("Manage UniGetUI settings");
        SettingsMgmtDescriptionText.Text = CoreTools.Translate("Import settings from a local file");
        ImportSettingsBtnCtrl.Content = CoreTools.Translate("Import");
        ExportSettingsBtnCtrl.Content = CoreTools.Translate("Export");
        ResetTitleText.Text = CoreTools.Translate("Reset UniGetUI");
        ResetDescriptionText.Text = CoreTools.Translate("Reset WingetUI and its preferences");
        ResetSettingsBtnCtrl.Content = CoreTools.Translate("Reset WingetUI");
    }

    private void PopulateLanguageSelector()
    {
        LanguageSelectorControl.Items.Clear();
        LanguageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Default"), "default"));

        foreach (var entry in BuildLanguageEntries())
        {
            LanguageSelectorControl.Items.Add(CreateComboBoxItem(entry.Label, entry.Value));
        }
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        _initialPreferredLanguage = NormalizePreferredLanguageValue(Settings.GetValue(Settings.K.PreferredLanguage));
        SelectComboBoxValue(LanguageSelectorControl, _initialPreferredLanguage, fallbackValue: "default");
        ShowVersionOnTitleBarCheckBoxControl.IsChecked = Settings.Get(Settings.K.ShowVersionNumberOnTitlebar);
        DisableTelemetryCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableTelemetry);
        EnableAutoUpdateCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableAutoUpdateWingetUI);
        EnableBetaCheckBoxControl.IsChecked = Settings.Get(Settings.K.EnableUniGetUIBeta);
        SetRestartNoticeVisible(false);

        _isLoading = false;
    }

    private void LanguageSelector_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var selectedLanguage = NormalizePreferredLanguageValue(GetSelectedValue(LanguageSelectorControl));
        Settings.SetValue(Settings.K.PreferredLanguage, selectedLanguage);
        SetRestartNoticeVisible(selectedLanguage != _initialPreferredLanguage);
    }

    private void ShowVersionOnTitleBarCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.ShowVersionNumberOnTitlebar, ShowVersionOnTitleBarCheckBoxControl.IsChecked == true);
        FindWindow()?.RefreshWindowTitle();
    }

    private void DisableTelemetryCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableTelemetry, DisableTelemetryCheckBoxControl.IsChecked != true);
    }

    private void ManageTelemetryBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new TelemetryConsentWindow();
        if (owner is not null)
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private void EnableAutoUpdateCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.DisableAutoUpdateWingetUI, EnableAutoUpdateCheckBoxControl.IsChecked != true);
    }

    private void EnableBetaCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.EnableUniGetUIBeta, EnableBetaCheckBoxControl.IsChecked == true);
    }

    private void CheckForUpdatesNowBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        _ = AvaloniaAutoUpdater.CheckAndInstallUpdatesAsync();
    }

    private async void ImportSettingsBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = CoreTools.Translate("Import settings from a local file"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON (*.json)") { Patterns = ["*.json"] }],
        });

        if (result.Count > 0)
        {
            string? path = result[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    await Task.Run(() => Settings.ImportFromFile_JSON(path));
                }
                catch (Exception ex)
                {
                    Logger.Error("Error importing settings");
                    Logger.Error(ex);
                }
                SetRestartNoticeVisible(true);
            }
        }
    }

    private async void ExportSettingsBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CoreTools.Translate("Export settings to a local file"),
            SuggestedFileName = "UniGetUI Settings.json",
            FileTypeChoices = [new FilePickerFileType("JSON (*.json)") { Patterns = ["*.json"] }],
        });

        if (result is not null)
        {
            string? path = result.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    await Task.Run(() => Settings.ExportToFile_JSON(path));
                    CoreTools.Launch(path);
                }
                catch (Exception ex)
                {
                    Logger.Error("Error exporting settings");
                    Logger.Error(ex);
                }
            }
        }
    }

    private void ResetSettingsBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Settings.ResetSettings();
        }
        catch (Exception ex)
        {
            Logger.Error("Error resetting settings");
            Logger.Error(ex);
        }
        SetRestartNoticeVisible(true);
    }

    private void SetRestartNoticeVisible(bool isVisible)
    {
        RestartNoticeCardControl.IsVisible = isVisible;
    }

    private void RestartAppButton_OnClick(object? sender, RoutedEventArgs e)
        => MainWindow.KillAndRestart();

    private static List<(string Label, string Value)> BuildLanguageEntries()
    {
        return LanguageData.LanguageReference
            .Select(entry =>
            {
                var label = entry.Value;
                if (
                    entry.Key != "en"
                    && LanguageData.TranslationPercentages.TryGetValue(entry.Key, out var percentage)
                )
                {
                    label = $"{label} ({percentage})";
                }

                return (Label: label, Value: entry.Key);
            })
            .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private MainWindow? FindWindow()
    {
        return TopLevel.GetTopLevel(this) as MainWindow;
    }

    private static ComboBoxItem CreateComboBoxItem(string label, string value)
    {
        return new ComboBoxItem
        {
            Content = label,
            Tag = value,
        };
    }

    private static string GetSelectedValue(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem { Tag: string value }
            ? value
            : string.Empty;
    }

    private static string NormalizePreferredLanguageValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "default" : value;
    }

    private static void SelectComboBoxValue(ComboBox comboBox, string selectedValue, string fallbackValue)
    {
        var desiredValue = string.IsNullOrWhiteSpace(selectedValue) ? fallbackValue : selectedValue;

        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem { Tag: string value } && value == desiredValue)
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
