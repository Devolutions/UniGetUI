using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class UpdatesSettingsView : UserControl, ISettingsSectionView
{
    private static readonly (string Label, string Value, int? Number)[] UpdateIntervalOptions =
    [
        ("{0} minutes", "600", 10),
        ("{0} minutes", "1800", 30),
        ("1 hour", "3600", null),
        ("{0} hours", "7200", 2),
        ("{0} hours", "14400", 4),
        ("{0} hours", "28800", 8),
        ("{0} hours", "43200", 12),
        ("1 day", "86400", null),
        ("{0} days", "172800", 2),
        ("{0} days", "259200", 3),
        ("1 week", "604800", null),
    ];

    private bool _isLoading;

    private CheckBox EnablePeriodicChecksCheckBoxControl => GetControl<CheckBox>("EnablePeriodicChecksCheckBox");

    private ComboBox UpdatesCheckIntervalSelectorControl => GetControl<ComboBox>("UpdatesCheckIntervalSelector");

    private CheckBox AutomaticallyUpdatePackagesCheckBoxControl => GetControl<CheckBox>("AutomaticallyUpdatePackagesCheckBox");

    private CheckBox DisableOnMeteredConnectionsCheckBoxControl => GetControl<CheckBox>("DisableOnMeteredConnectionsCheckBox");

    private CheckBox DisableOnBatteryCheckBoxControl => GetControl<CheckBox>("DisableOnBatteryCheckBox");

    private CheckBox DisableOnBatterySaverCheckBoxControl => GetControl<CheckBox>("DisableOnBatterySaverCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock CheckingTitleText => GetControl<TextBlock>("CheckingTitleBlock");

    private TextBlock CheckingDescriptionText => GetControl<TextBlock>("CheckingDescriptionBlock");

    private TextBlock CheckingHintText => GetControl<TextBlock>("CheckingHintBlock");

    private TextBlock AutomaticTitleText => GetControl<TextBlock>("AutomaticTitleBlock");

    private TextBlock AutomaticDescriptionText => GetControl<TextBlock>("AutomaticDescriptionBlock");

    private TextBlock AutomaticHintText => GetControl<TextBlock>("AutomaticHintBlock");

    public UpdatesSettingsView()
    {
        InitializeComponent();
        EnablePeriodicChecksCheckBoxControl.Click += EnablePeriodicChecksCheckBox_OnClick;
        AutomaticallyUpdatePackagesCheckBoxControl.Click += AutomaticallyUpdatePackagesCheckBox_OnClick;
        DisableOnMeteredConnectionsCheckBoxControl.Click += DisableOnMeteredConnectionsCheckBox_OnClick;
        DisableOnBatteryCheckBoxControl.Click += DisableOnBatteryCheckBox_OnClick;
        DisableOnBatterySaverCheckBoxControl.Click += DisableOnBatterySaverCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("Updates preferences");
        SectionSubtitle = CoreTools.Translate("Update check frequency, automatically install updates, etc.");
        SectionStatus = CoreTools.Translate("Show the live output");

        ApplyLocalizedText();
        PopulateUpdateIntervalSelector();
        LoadStoredValues();
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Updates preferences");
        LeadDescriptionText.Text = CoreTools.Translate("Update check frequency, automatically install updates, etc.");
        CheckingTitleText.Text = CoreTools.Translate("Update checking");
        CheckingDescriptionText.Text = CoreTools.Translate("Check for updates periodically");
        EnablePeriodicChecksCheckBoxControl.Content = CoreTools.Translate("Check for package updates periodically");
        CheckingHintText.Text = CoreTools.Translate("Check for updates periodically");
        AutomaticTitleText.Text = CoreTools.Translate("Automatic updates");
        AutomaticDescriptionText.Text = CoreTools.Translate("Install updates automatically");
        AutomaticallyUpdatePackagesCheckBoxControl.Content = CoreTools.Translate("Install available updates automatically");
        DisableOnMeteredConnectionsCheckBoxControl.Content = CoreTools.Translate("Do not automatically install updates when the network connection is metered");
        DisableOnBatteryCheckBoxControl.Content = CoreTools.Translate("Do not automatically install updates when the device runs on battery");
        DisableOnBatterySaverCheckBoxControl.Content = CoreTools.Translate("Do not automatically install updates when the battery saver is on");
        AutomaticHintText.Text = CoreTools.Translate("Update check frequency, automatically install updates, etc.");
    }

    private void PopulateUpdateIntervalSelector()
    {
        UpdatesCheckIntervalSelectorControl.Items.Clear();

        foreach (var option in BuildIntervalItems())
        {
            UpdatesCheckIntervalSelectorControl.Items.Add(CreateComboBoxItem(option.Label, option.Value));
        }
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        var isPeriodicCheckEnabled = !Settings.Get(Settings.K.DisableAutoCheckforUpdates);
        EnablePeriodicChecksCheckBoxControl.IsChecked = isPeriodicCheckEnabled;

        var intervalValue = Settings.GetValue(Settings.K.UpdatesCheckInterval);
        if (string.IsNullOrWhiteSpace(intervalValue))
        {
            intervalValue = "3600";
            Settings.SetValue(Settings.K.UpdatesCheckInterval, intervalValue);
        }

        SelectComboBoxValue(UpdatesCheckIntervalSelectorControl, intervalValue, fallbackValue: "3600");
        AutomaticallyUpdatePackagesCheckBoxControl.IsChecked = Settings.Get(Settings.K.AutomaticallyUpdatePackages);
        DisableOnMeteredConnectionsCheckBoxControl.IsChecked = Settings.Get(Settings.K.DisableAUPOnMeteredConnections);
        DisableOnBatteryCheckBoxControl.IsChecked = Settings.Get(Settings.K.DisableAUPOnBattery);
        DisableOnBatterySaverCheckBoxControl.IsChecked = Settings.Get(Settings.K.DisableAUPOnBatterySaver);
        ApplyAutomaticUpdateControlState();

        _isLoading = false;
    }

    private void EnablePeriodicChecksCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var isPeriodicCheckEnabled = EnablePeriodicChecksCheckBoxControl.IsChecked == true;
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, !isPeriodicCheckEnabled);
        ApplyAutomaticUpdateControlState();
        RefreshUpdatesLoader();
    }

    private void UpdatesCheckIntervalSelector_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.SetValue(Settings.K.UpdatesCheckInterval, GetSelectedValue(UpdatesCheckIntervalSelectorControl));
        RefreshUpdatesLoader();
    }

    private void AutomaticallyUpdatePackagesCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.AutomaticallyUpdatePackages, AutomaticallyUpdatePackagesCheckBoxControl.IsChecked == true);
    }

    private void DisableOnMeteredConnectionsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableAUPOnMeteredConnections, DisableOnMeteredConnectionsCheckBoxControl.IsChecked == true);
    }

    private void DisableOnBatteryCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableAUPOnBattery, DisableOnBatteryCheckBoxControl.IsChecked == true);
    }

    private void DisableOnBatterySaverCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableAUPOnBatterySaver, DisableOnBatterySaverCheckBoxControl.IsChecked == true);
    }

    private void ApplyAutomaticUpdateControlState()
    {
        var isPeriodicCheckEnabled = EnablePeriodicChecksCheckBoxControl.IsChecked == true;
        UpdatesCheckIntervalSelectorControl.IsEnabled = isPeriodicCheckEnabled;
        AutomaticallyUpdatePackagesCheckBoxControl.IsEnabled = isPeriodicCheckEnabled;
        DisableOnMeteredConnectionsCheckBoxControl.IsEnabled = isPeriodicCheckEnabled;
        DisableOnBatteryCheckBoxControl.IsEnabled = isPeriodicCheckEnabled;
        DisableOnBatterySaverCheckBoxControl.IsEnabled = isPeriodicCheckEnabled;
    }

    private static IEnumerable<(string Label, string Value)> BuildIntervalItems()
    {
        foreach (var option in UpdateIntervalOptions)
        {
            yield return option.Number is int number
                ? (CoreTools.Translate(option.Label, number), option.Value)
                : (CoreTools.Translate(option.Label), option.Value);
        }
    }

    private static void RefreshUpdatesLoader()
    {
        try
        {
            if (!UpgradablePackagesLoader.Instance.IsLoading)
            {
                _ = UpgradablePackagesLoader.Instance.ReloadPackages();
            }
        }
        catch
        {
        }
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
