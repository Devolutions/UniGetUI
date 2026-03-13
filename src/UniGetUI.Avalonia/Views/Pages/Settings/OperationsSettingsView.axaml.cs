using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class OperationsSettingsView : UserControl, ISettingsSectionView
{
    private static readonly int[] ParallelOperationOptions = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 30, 50, 75, 100];

    private bool _isLoading;

    private ComboBox ParallelOperationsSelectorControl => GetControl<ComboBox>("ParallelOperationsSelector");

    private CheckBox KeepSuccessfulOperationsCheckBoxControl => GetControl<CheckBox>("KeepSuccessfulOperationsCheckBox");

    private CheckBox ForceKillCheckBoxControl => GetControl<CheckBox>("ForceKillCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock ParallelTitleText => GetControl<TextBlock>("ParallelTitleBlock");

    private TextBlock ParallelDescriptionText => GetControl<TextBlock>("ParallelDescriptionBlock");

    private TextBlock ParallelHintText => GetControl<TextBlock>("ParallelHintBlock");

    private TextBlock RetentionTitleText => GetControl<TextBlock>("RetentionTitleBlock");

    private TextBlock RetentionDescriptionText => GetControl<TextBlock>("RetentionDescriptionBlock");

    private TextBlock RetentionHintText => GetControl<TextBlock>("RetentionHintBlock");

    private TextBlock ForceKillTitleText => GetControl<TextBlock>("ForceKillTitleBlock");

    private TextBlock ForceKillDescriptionText => GetControl<TextBlock>("ForceKillDescriptionBlock");

    private TextBlock ForceKillHintText => GetControl<TextBlock>("ForceKillHintBlock");

    private TextBlock ShortcutsTitleText => GetControl<TextBlock>("ShortcutsTitleBlock");
    private TextBlock ShortcutsDescriptionText => GetControl<TextBlock>("ShortcutsDescriptionBlock");
    private CheckBox AskShortcutsCheckBoxCtrl => GetControl<CheckBox>("AskShortcutsCheckBox");
    private Button ManageShortcutsBtnCtrl => GetControl<Button>("ManageShortcutsBtn");

    public OperationsSettingsView()
    {
        InitializeComponent();
        KeepSuccessfulOperationsCheckBoxControl.Click += KeepSuccessfulOperationsCheckBox_OnClick;
        ForceKillCheckBoxControl.Click += ForceKillCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("Package operation preferences");
        SectionSubtitle = CoreTools.Translate("Change how UniGetUI handles install, update and uninstall operations.");
        SectionStatus = CoreTools.Translate("Show the live output");

        ApplyLocalizedText();
        PopulateParallelOperationsSelector();
        LoadStoredValues();
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Change how UniGetUI handles install, update and uninstall operations.");
        LeadDescriptionText.Text = CoreTools.Translate("Clear successful operations from the operation list after a 5 second delay");
        ParallelTitleText.Text = CoreTools.Translate("Concurrency and execution");
        ParallelDescriptionText.Text = CoreTools.Translate("Choose how many operations shouls be performed in parallel");
        ParallelHintText.Text = CoreTools.Translate("Allow package operations to be performed in parallel");
        RetentionTitleText.Text = CoreTools.Translate("Operation history");
        RetentionDescriptionText.Text = CoreTools.Translate("Clear successful operations from the operation list after a 5 second delay");
        KeepSuccessfulOperationsCheckBoxControl.Content = CoreTools.Translate("Do not remove successful operations from the list automatically");
        RetentionHintText.Text = CoreTools.Translate("Download operations are not affected by this setting");
        ForceKillTitleText.Text = CoreTools.Translate("Restrictions on package operations");
        ForceKillDescriptionText.Text = CoreTools.Translate("Try to kill the processes that refuse to close when requested to");
        ForceKillCheckBoxControl.Content = CoreTools.Translate("Try to kill the processes that refuse to close when requested to");
        ForceKillHintText.Text = CoreTools.Translate("There are ongoing operations. Quitting WingetUI may cause them to fail. Do you want to continue?");
        ShortcutsTitleText.Text = CoreTools.Translate("Automatic desktop shortcut remover");
        ShortcutsDescriptionText.Text = CoreTools.Translate("Here you can change UniGetUI's behaviour regarding the following shortcuts. Checking a shortcut will make UniGetUI delete it if if gets created on a future upgrade. Unchecking it will keep the shortcut intact");
        AskShortcutsCheckBoxCtrl.Content = CoreTools.Translate("Ask to delete desktop shortcuts created during an install or upgrade.");
        ManageShortcutsBtnCtrl.Content = CoreTools.Translate("Manage shortcuts");
    }

    private void PopulateParallelOperationsSelector()
    {
        ParallelOperationsSelectorControl.Items.Clear();

        foreach (var option in ParallelOperationOptions)
        {
            var text = option == 1
                ? CoreTools.Translate("(Number {0} in the queue)", option)
                : CoreTools.Translate("(Number {0} in the queue)", option);
            ParallelOperationsSelectorControl.Items.Add(CreateComboBoxItem(text, option.ToString()));
        }
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        var selectedValue = Settings.GetValue(Settings.K.ParallelOperationCount);
        if (!int.TryParse(selectedValue, out var maxOperations))
        {
            maxOperations = 1;
            Settings.SetValue(Settings.K.ParallelOperationCount, maxOperations.ToString());
        }

        SelectComboBoxValue(ParallelOperationsSelectorControl, maxOperations.ToString(), fallbackValue: "1");
        AbstractOperation.MAX_OPERATIONS = maxOperations;
        KeepSuccessfulOperationsCheckBoxControl.IsChecked = Settings.Get(Settings.K.MaintainSuccessfulInstalls);
        ForceKillCheckBoxControl.IsChecked = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);
        AskShortcutsCheckBoxCtrl.IsChecked = Settings.Get(Settings.K.AskToDeleteNewDesktopShortcuts);

        _isLoading = false;
    }

    private void ParallelOperationsSelector_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        var selectedValue = GetSelectedValue(ParallelOperationsSelectorControl);
        Settings.SetValue(Settings.K.ParallelOperationCount, selectedValue);

        if (int.TryParse(selectedValue, out var maxOperations))
        {
            AbstractOperation.MAX_OPERATIONS = maxOperations;
        }
    }

    private void KeepSuccessfulOperationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.MaintainSuccessfulInstalls, KeepSuccessfulOperationsCheckBoxControl.IsChecked == true);
    }

    private void ForceKillCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.KillProcessesThatRefuseToDie, ForceKillCheckBoxControl.IsChecked == true);
    }

    private void AskShortcutsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.AskToDeleteNewDesktopShortcuts, AskShortcutsCheckBoxCtrl.IsChecked == true);
    }

    private async void ManageShortcutsBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var window = new DesktopShortcutsWindow();
        if (VisualRoot is Window owner)
            await window.ShowDialog(owner);
        else
            window.Show();
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
