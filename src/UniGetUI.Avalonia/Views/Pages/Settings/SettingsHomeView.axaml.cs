using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class SettingsHomeView : UserControl, ISettingsSectionView
{
    private StackPanel SectionButtonsHost => GetControl<StackPanel>("SectionButtonsPanel");

    private TextBlock IntroTitleText => GetControl<TextBlock>("IntroTitleBlock");

    private TextBlock IntroDescriptionText => GetControl<TextBlock>("IntroDescriptionBlock");

    private TextBlock SectionsLabelText => GetControl<TextBlock>("SectionsLabelBlock");

    public SettingsHomeView()
    {
        InitializeComponent();
        IntroTitleText.Text = CoreTools.Translate("General preferences");
        IntroDescriptionText.Text = CoreTools.Translate("Default preferences - suitable for regular users");
        SectionsLabelText.Text = CoreTools.Translate("Settings");

        AddSectionButton(
            SettingsSectionRoute.Interface,
            CoreTools.Translate("User interface preferences"),
            CoreTools.Translate("Application theme, startup page, package icons, clear successful installs automatically")
        );
        AddSectionButton(
            SettingsSectionRoute.Managers,
            CoreTools.Translate("Package manager preferences"),
            CoreTools.Translate("Enable and disable package managers, change default install options, etc.")
        );
        AddSectionButton(
            SettingsSectionRoute.General,
            CoreTools.Translate("General preferences"),
            CoreTools.Translate("Language, theme and other miscellaneous preferences")
        );
        AddSectionButton(
            SettingsSectionRoute.Updates,
            CoreTools.Translate("Updates preferences"),
            CoreTools.Translate("Update check frequency, automatically install updates, etc.")
        );
        AddSectionButton(
            SettingsSectionRoute.Notifications,
            CoreTools.Translate("Notification preferences"),
            CoreTools.Translate("Show notifications on different events")
        );
        AddSectionButton(
            SettingsSectionRoute.Operations,
            CoreTools.Translate("Package operation preferences"),
            CoreTools.Translate("Install and update preferences")
        );
        AddSectionButton(
            SettingsSectionRoute.Internet,
            CoreTools.Translate("Internet connection settings"),
            CoreTools.Translate("Proxy settings, etc.")
        );
        AddSectionButton(
            SettingsSectionRoute.Administrator,
            CoreTools.Translate("Administrator privileges preferences"),
            CoreTools.Translate("Change how operations request administrator rights")
        );
        AddSectionButton(
            SettingsSectionRoute.Backup,
            CoreTools.Translate("Backup and Restore"),
            CoreTools.Translate("Backup installed packages")
        );
        AddSectionButton(
            SettingsSectionRoute.Experimental,
            CoreTools.Translate("Experimental settings and developer options"),
            CoreTools.Translate("Experimental settings and developer options")
        );
    }

    internal event EventHandler<SettingsSectionRoute>? NavigationRequested;

    public string SectionTitle => CoreTools.Translate("Settings");

    public string SectionSubtitle => CoreTools.Translate("General preferences");

    public string SectionStatus => CoreTools.Translate("Settings");

    private void AddSectionButton(SettingsSectionRoute route, string title, string description)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = route,
        };
        button.Classes.Add("settings-tile");
        button.Click += SectionButton_OnClick;

        button.Content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Text = title,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Opacity = 0.74,
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        SectionButtonsHost.Children.Add(button);
    }

    private void SectionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SettingsSectionRoute route })
        {
            NavigationRequested?.Invoke(this, route);
        }
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
