using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using UniGetUI.Avalonia;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class InterfaceSettingsView : UserControl, ISettingsSectionView
{
    private bool _isLoading;

    private ComboBox ThemeSelectorControl => GetControl<ComboBox>("ThemeSelector");

    private ComboBox StartupPageSelectorControl => GetControl<ComboBox>("StartupPageSelector");

    private CheckBox CollapseNavigationCheckBoxControl => GetControl<CheckBox>("CollapseNavigationCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock ThemeTitleText => GetControl<TextBlock>("ThemeTitleBlock");

    private TextBlock ThemeDescriptionText => GetControl<TextBlock>("ThemeDescriptionBlock");

    private TextBlock StartupTitleText => GetControl<TextBlock>("StartupTitleBlock");

    private TextBlock StartupDescriptionText => GetControl<TextBlock>("StartupDescriptionBlock");

    private TextBlock NavigationTitleText => GetControl<TextBlock>("NavigationTitleBlock");

    private TextBlock NavigationDescriptionText => GetControl<TextBlock>("NavigationDescriptionBlock");

    private TextBlock NavigationNoteText => GetControl<TextBlock>("NavigationNoteBlock");

    private TextBlock IconsTitleText => GetControl<TextBlock>("IconsTitleBlock");

    private TextBlock IconsDescriptionText => GetControl<TextBlock>("IconsDescriptionBlock");

    private CheckBox DisableIconsCheckBoxControl => GetControl<CheckBox>("DisableIconsCheckBox");

    private TextBlock IconCacheSizeText => GetControl<TextBlock>("IconCacheSizeBlock");

    private Button ResetIconCacheBtnCtrl => GetControl<Button>("ResetIconCacheBtn");

    private TextBlock UpdatesDefaultsTitleText => GetControl<TextBlock>("UpdatesDefaultsTitleBlock");

    private TextBlock UpdatesDefaultsDescriptionText => GetControl<TextBlock>("UpdatesDefaultsDescriptionBlock");

    private CheckBox DisableSelectingUpdatesByDefaultCheckBoxControl => GetControl<CheckBox>("DisableSelectingUpdatesByDefaultCheckBox");

    private TextBlock AutostartTitleText => GetControl<TextBlock>("AutostartTitleBlock");

    private TextBlock AutostartDescriptionText => GetControl<TextBlock>("AutostartDescriptionBlock");

    private TextBlock SystemTrayTitleText => GetControl<TextBlock>("SystemTrayTitleBlock");

    private TextBlock SystemTrayDescriptionText => GetControl<TextBlock>("SystemTrayDescriptionBlock");

    private CheckBox EnableSystemTrayCheckBoxControl => GetControl<CheckBox>("EnableSystemTrayCheckBox");

    private Button EditAutostartBtnCtrl => GetControl<Button>("EditAutostartBtn");

    public InterfaceSettingsView()
    {
        InitializeComponent();
        CollapseNavigationCheckBoxControl.Click += CollapseNavigationCheckBox_OnClick;
        DisableIconsCheckBoxControl.Click += DisableIconsCheckBox_OnClick;
        DisableSelectingUpdatesByDefaultCheckBoxControl.Click += DisableSelectingUpdatesByDefault_OnClick;
        EnableSystemTrayCheckBoxControl.Click += EnableSystemTrayCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("User interface preferences");
        SectionSubtitle = CoreTools.Translate("Application theme, startup page, package icons, clear successful installs automatically");
        SectionStatus = CoreTools.Translate("Show the live output");

        ApplyLocalizedText();
        PopulateThemeSelector();
        PopulateStartupPageSelector();
        LoadStoredValues();
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("User interface preferences");
        LeadDescriptionText.Text = CoreTools.Translate("Language, theme and other miscellaneous preferences");
        ThemeTitleText.Text = CoreTools.Translate("Application theme:");
        ThemeDescriptionText.Text = CoreTools.Translate("Application theme, startup page, package icons, clear successful installs automatically");
        StartupTitleText.Text = CoreTools.Translate("UniGetUI startup page:");
        StartupDescriptionText.Text = CoreTools.Translate("WingetUI autostart behaviour, application launch settings");
        NavigationTitleText.Text = CoreTools.Translate("User interface preferences");
        NavigationDescriptionText.Text = CoreTools.Translate("Language, theme and other miscellaneous preferences");
        CollapseNavigationCheckBoxControl.Content = CoreTools.Translate("Toggle search filters pane");
        NavigationNoteText.Text = CoreTools.Translate("Show the live output");
        IconsTitleText.Text = CoreTools.Translate("Show package icons on package lists");
        IconsDescriptionText.Text = CoreTools.Translate("Show package icons on package lists");
        DisableIconsCheckBoxControl.Content = CoreTools.Translate("Show package icons on package lists");
        ResetIconCacheBtnCtrl.Content = CoreTools.Translate("Clear the local icon cache");
        UpdatesDefaultsTitleText.Text = CoreTools.Translate("Change default options");
        UpdatesDefaultsDescriptionText.Text = CoreTools.Translate("Select upgradable packages by default");
        DisableSelectingUpdatesByDefaultCheckBoxControl.Content = CoreTools.Translate("Select upgradable packages by default");
        AutostartTitleText.Text = CoreTools.Translate("WingetUI autostart behaviour, application launch settings");
        AutostartDescriptionText.Text = CoreTools.Translate("Manage WingetUI autostart behaviour from the Settings app");
        EditAutostartBtnCtrl.Content = CoreTools.Translate("Manage WingetUI autostart behaviour from the Settings app");
        SystemTrayTitleText.Text = CoreTools.Translate("UniGetUI on the background and system tray");
        SystemTrayDescriptionText.Text = CoreTools.Translate("Close UniGetUI to the system tray");
        EnableSystemTrayCheckBoxControl.Content = CoreTools.Translate("Show UniGetUI on the system tray");
    }

    private void PopulateThemeSelector()
    {
        ThemeSelectorControl.Items.Clear();
        ThemeSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Light"), "light"));
        ThemeSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Dark"), "dark"));
        ThemeSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Follow system color scheme"), "auto"));
    }

    private void PopulateStartupPageSelector()
    {
        StartupPageSelectorControl.Items.Clear();
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Default"), "default"));
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Discover Packages"), "discover"));
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Software Updates"), "updates"));
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Installed Packages"), "installed"));
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Package Bundles"), "bundles"));
        StartupPageSelectorControl.Items.Add(CreateComboBoxItem(CoreTools.Translate("Settings"), "settings"));
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        var preferredTheme = Settings.GetValue(Settings.K.PreferredTheme);
        if (string.IsNullOrWhiteSpace(preferredTheme))
        {
            preferredTheme = "auto";
            Settings.SetValue(Settings.K.PreferredTheme, preferredTheme);
        }

        SelectComboBoxValue(ThemeSelectorControl, preferredTheme, fallbackValue: "auto");
        SelectComboBoxValue(StartupPageSelectorControl, Settings.GetValue(Settings.K.StartupPage), fallbackValue: "default");
        CollapseNavigationCheckBoxControl.IsChecked = Settings.Get(Settings.K.CollapseNavMenuOnWideScreen);
        DisableIconsCheckBoxControl.IsChecked = Settings.Get(Settings.K.DisableIconsOnPackageLists);
        DisableSelectingUpdatesByDefaultCheckBoxControl.IsChecked = Settings.Get(Settings.K.DisableSelectingUpdatesByDefault);
        EnableSystemTrayCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableSystemTray);
        _ = UpdateIconCacheSizeAsync();

        _isLoading = false;
    }

    private void ThemeSelector_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var selectedTheme = GetSelectedValue(ThemeSelectorControl);
        Settings.SetValue(Settings.K.PreferredTheme, selectedTheme);

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.RequestedThemeVariant = selectedTheme switch
            {
                "dark" => ThemeVariant.Dark,
                "light" => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };
        }
    }

    private void StartupPageSelector_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.SetValue(Settings.K.StartupPage, GetSelectedValue(StartupPageSelectorControl));
    }

    private void CollapseNavigationCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var collapseOnWideScreen = CollapseNavigationCheckBoxControl.IsChecked == true;
        Settings.Set(Settings.K.CollapseNavMenuOnWideScreen, collapseOnWideScreen);
        FindShell()?.SetNavigationCollapsedPreference(collapseOnWideScreen);
    }

    private void DisableIconsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.DisableIconsOnPackageLists, DisableIconsCheckBoxControl.IsChecked == true);
    }

    private void DisableSelectingUpdatesByDefault_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.DisableSelectingUpdatesByDefault, DisableSelectingUpdatesByDefaultCheckBoxControl.IsChecked == true);
    }

    private void ResetIconCacheBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.Delete(CoreData.UniGetUICacheDirectory_Icons, true);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while deleting icon cache");
            Logger.Error(ex);
        }
        _ = UpdateIconCacheSizeAsync();
    }

    private async Task UpdateIconCacheSizeAsync()
    {
        double mb = await Task.Run(() =>
        {
            try
            {
                long bytes = Directory
                    .GetFiles(CoreData.UniGetUICacheDirectory_Icons, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                return bytes / 1_048_576d;
            }
            catch
            {
                return 0d;
            }
        });

        double rounded = Math.Round(mb, 2);
        IconCacheSizeText.Text = CoreTools.Translate("The local icon cache currently takes {0} MB", rounded);
    }

    private void EditAutostartBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        CoreTools.Launch("ms-settings:startupapps");
    }

    private void EnableSystemTrayCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.DisableSystemTray, EnableSystemTrayCheckBoxControl.IsChecked != true);
        if (TopLevel.GetTopLevel(this) is MainWindow mainWindow)
            mainWindow.ApplyTrayIconVisibility();
    }

    private MainShellView? FindShell()
    {
        return this.GetSelfAndVisualAncestors().OfType<MainShellView>().FirstOrDefault();
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
