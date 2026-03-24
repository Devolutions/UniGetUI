using Avalonia.Controls;
using Avalonia.Input;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class TelemetryDialog : Window
{
    public bool? Result { get; private set; }

    public TelemetryDialog()
    {
        InitializeComponent();

        Title = CoreTools.Translate("Share anonymous usage data");

        TitleBlock.Text = CoreTools.Translate("Share anonymous usage data");
        Body1.Text = CoreTools.Translate("UniGetUI collects anonymous usage data with the sole purpose of understanding and improving the user experience.");
        Body2.Text = CoreTools.Translate("No personal information is collected nor sent, and the collected data is anonimized, so it can't be back-tracked to you.");
        DetailsLink.Text = CoreTools.Translate("More details about the shared data and how it will be processed");
        Body3.Text = CoreTools.Translate("Do you accept that UniGetUI collects and sends anonymous usage statistics, with the sole purpose of understanding and improving the user experience?");

        DeclineButton.Content = CoreTools.Translate("Decline");
        AcceptButton.Content = CoreTools.Translate("Accept");

        DetailsLink.Bind(TextBlock.ForegroundProperty,
            DetailsLink.GetResourceObservable("SystemControlHighlightAccentBrush"));
        DetailsLink.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
                CoreTools.Launch("https://devolutions.net/legal/");
        };

        Closing += (_, e) => { if (Result is null) e.Cancel = true; };
        DeclineButton.Click += (_, _) => { Result = false; Close(); };
        AcceptButton.Click += (_, _) => { Result = true; Close(); };
    }
}
