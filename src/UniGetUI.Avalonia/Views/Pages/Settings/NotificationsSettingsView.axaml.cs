using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class NotificationsSettingsView : UserControl, ISettingsSectionView
{
    private bool _isLoading;

    private Border SystemTrayWarningCardControl => GetControl<Border>("SystemTrayWarningCard");

    private CheckBox EnableNotificationsCheckBoxControl => GetControl<CheckBox>("EnableNotificationsCheckBox");

    private CheckBox ShowUpdatesNotificationsCheckBoxControl => GetControl<CheckBox>("ShowUpdatesNotificationsCheckBox");

    private CheckBox ShowProgressNotificationsCheckBoxControl => GetControl<CheckBox>("ShowProgressNotificationsCheckBox");

    private CheckBox ShowErrorNotificationsCheckBoxControl => GetControl<CheckBox>("ShowErrorNotificationsCheckBox");

    private CheckBox ShowSuccessNotificationsCheckBoxControl => GetControl<CheckBox>("ShowSuccessNotificationsCheckBox");

    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");

    private TextBlock LeadDescriptionText => GetControl<TextBlock>("LeadDescriptionBlock");

    private TextBlock SystemTrayWarningTitleText => GetControl<TextBlock>("SystemTrayWarningTitleBlock");

    private TextBlock SystemTrayWarningDescriptionText => GetControl<TextBlock>("SystemTrayWarningDescriptionBlock");

    private TextBlock NotificationsTitleText => GetControl<TextBlock>("NotificationsTitleBlock");

    private TextBlock NotificationsDescriptionText => GetControl<TextBlock>("NotificationsDescriptionBlock");

    private TextBlock NotificationsHintText => GetControl<TextBlock>("NotificationsHintBlock");

    private TextBlock TypesTitleText => GetControl<TextBlock>("TypesTitleBlock");

    private TextBlock TypesDescriptionText => GetControl<TextBlock>("TypesDescriptionBlock");

    private TextBlock TypesHintText => GetControl<TextBlock>("TypesHintBlock");

    public NotificationsSettingsView()
    {
        InitializeComponent();
        EnableNotificationsCheckBoxControl.Click += EnableNotificationsCheckBox_OnClick;
        ShowUpdatesNotificationsCheckBoxControl.Click += ShowUpdatesNotificationsCheckBox_OnClick;
        ShowProgressNotificationsCheckBoxControl.Click += ShowProgressNotificationsCheckBox_OnClick;
        ShowErrorNotificationsCheckBoxControl.Click += ShowErrorNotificationsCheckBox_OnClick;
        ShowSuccessNotificationsCheckBoxControl.Click += ShowSuccessNotificationsCheckBox_OnClick;

        SectionTitle = CoreTools.Translate("Notification preferences");
        SectionSubtitle = CoreTools.Translate("Show notifications on different events");
        SectionStatus = CoreTools.Translate("Show the live output");

        ApplyLocalizedText();
        LoadStoredValues();
    }

    public string SectionTitle { get; }

    public string SectionSubtitle { get; }

    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Notification preferences");
        LeadDescriptionText.Text = CoreTools.Translate("Show notifications on different events");
        SystemTrayWarningTitleText.Text = CoreTools.Translate("Attention required");
        SystemTrayWarningDescriptionText.Text = CoreTools.Translate("The system tray icon must be enabled in order for notifications to work");
        NotificationsTitleText.Text = CoreTools.Translate("Notification preferences");
        NotificationsDescriptionText.Text = CoreTools.Translate("Show notifications on different events");
        EnableNotificationsCheckBoxControl.Content = CoreTools.Translate("Show notifications on different events");
        NotificationsHintText.Text = CoreTools.Translate("Show notifications on different events");
        TypesTitleText.Text = CoreTools.Translate("Notification types");
        TypesDescriptionText.Text = CoreTools.Translate("Show notifications on different events");
        ShowUpdatesNotificationsCheckBoxControl.Content = CoreTools.Translate("Show a notification when there are available updates");
        ShowProgressNotificationsCheckBoxControl.Content = CoreTools.Translate("Show a silent notification when an operation is running");
        ShowErrorNotificationsCheckBoxControl.Content = CoreTools.Translate("Show a notification when an operation fails");
        ShowSuccessNotificationsCheckBoxControl.Content = CoreTools.Translate("Show a notification when an operation finishes successfully");
        TypesHintText.Text = CoreTools.Translate("Show notifications on different events");
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        EnableNotificationsCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableNotifications);
        ShowUpdatesNotificationsCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableUpdatesNotifications);
        ShowProgressNotificationsCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableProgressNotifications);
        ShowErrorNotificationsCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableErrorNotifications);
        ShowSuccessNotificationsCheckBoxControl.IsChecked = !Settings.Get(Settings.K.DisableSuccessNotifications);

        ApplyNotificationControlState();

        _isLoading = false;
    }

    private void EnableNotificationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableNotifications, EnableNotificationsCheckBoxControl.IsChecked != true);
        ApplyNotificationControlState();
    }

    private void ShowUpdatesNotificationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableUpdatesNotifications, ShowUpdatesNotificationsCheckBoxControl.IsChecked != true);
    }

    private void ShowProgressNotificationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableProgressNotifications, ShowProgressNotificationsCheckBoxControl.IsChecked != true);
    }

    private void ShowErrorNotificationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableErrorNotifications, ShowErrorNotificationsCheckBoxControl.IsChecked != true);
    }

    private void ShowSuccessNotificationsCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        Settings.Set(Settings.K.DisableSuccessNotifications, ShowSuccessNotificationsCheckBoxControl.IsChecked != true);
    }

    private void ApplyNotificationControlState()
    {
        var isTrayAvailable = !Settings.Get(Settings.K.DisableSystemTray);
        var notificationsEnabled = EnableNotificationsCheckBoxControl.IsChecked == true;

        SystemTrayWarningCardControl.IsVisible = !isTrayAvailable;
        EnableNotificationsCheckBoxControl.IsEnabled = isTrayAvailable;

        var enableChildControls = isTrayAvailable && notificationsEnabled;
        ShowUpdatesNotificationsCheckBoxControl.IsEnabled = enableChildControls;
        ShowProgressNotificationsCheckBoxControl.IsEnabled = enableChildControls;
        ShowErrorNotificationsCheckBoxControl.IsEnabled = enableChildControls;
        ShowSuccessNotificationsCheckBoxControl.IsEnabled = enableChildControls;
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
