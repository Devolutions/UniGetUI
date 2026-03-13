using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class TelemetryConsentWindow : Window
{
    public TelemetryConsentWindow()
    {
        InitializeComponent();

        Title = CoreTools.Translate("Share anonymous usage data");
        TitleBlock.Text = CoreTools.Translate("Share anonymous usage data");
        CollectionBlock.Text = CoreTools.Translate(
            "UniGetUI collects anonymous usage data with the sole purpose of understanding and improving the user experience."
        );
        AnonymousBlock.Text = CoreTools.Translate(
            "No personal information is collected nor sent, and the collected data is anonymized, so it can't be back-tracked to you."
        );
        PrivacyLinkBlock.Text = CoreTools.Translate("More details about the shared data and how it will be processed")
            + " https://www.marticliment.com/unigetui/privacy/";
        QuestionBlock.Text = CoreTools.Translate(
            "Do you accept that UniGetUI collects and sends anonymous usage statistics, with the sole purpose of understanding and improving the user experience?"
        );
        AcceptBtn.Content = CoreTools.Translate("Accept");
        DeclineBtn.Content = CoreTools.Translate("Decline");
    }

    private void AcceptBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.Set(Settings.K.DisableTelemetry, false);
        Settings.Set(Settings.K.ShownTelemetryBanner, true);
        Close();
    }

    private void DeclineBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.Set(Settings.K.DisableTelemetry, true);
        Settings.Set(Settings.K.ShownTelemetryBanner, true);
        Close();
    }
}
