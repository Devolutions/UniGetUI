using Avalonia.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class TelemetryDialog : Window
{
    public bool? Result { get; private set; }

    public TelemetryDialog()
    {
        InitializeComponent();

        Body2.Text = CoreTools.Translate("No personal information is collected nor sent, and the collected data is anonimized, so it can't be back-tracked to you.");

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
