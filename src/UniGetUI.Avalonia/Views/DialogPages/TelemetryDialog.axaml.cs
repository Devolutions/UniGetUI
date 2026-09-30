using Avalonia.Controls;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public sealed class TelemetryDialog : ImmersiveConfirmationDialog
{
    public TelemetryDialog()
    {
        var detailsLink = new HyperlinkText
        {
            Text = CoreTools.Translate("More details about the shared data and how it will be processed"),
            Opacity = 0.9,
        };
        detailsLink.Bind(TextBlock.ForegroundProperty,
            detailsLink.GetResourceObservable("AccentTextFillColorPrimaryBrush"));
        detailsLink.Activated += (_, _) => CoreTools.Launch("https://devolutions.net/legal/");

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(BodyText(CoreTools.Translate(
            "UniGetUI collects anonymous usage data with the sole purpose of understanding and improving the user experience.")));
        body.Children.Add(BodyText(CoreTools.Translate(
            "No personal information is collected nor sent, and the collected data is anonimized, so it can't be back-tracked to you.")));
        body.Children.Add(detailsLink);
        body.Children.Add(BodyText(CoreTools.Translate(
            "Do you accept that UniGetUI collects and sends anonymous usage statistics, with the sole purpose of understanding and improving the user experience?")));

        Configure(
            CoreTools.Translate("Share anonymous usage data"),
            body,
            CoreTools.Translate("Accept"),
            CoreTools.Translate("Decline"));
        MaxWidth = 548;
        MinWidth = 420;
        MinHeight = 250;
        IsCloseButtonVisible = false;
        RequireChoice = true;
    }

    private static TextBlock BodyText(string text) => new()
    {
        Text = text,
        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        Opacity = 0.85,
    };
}
