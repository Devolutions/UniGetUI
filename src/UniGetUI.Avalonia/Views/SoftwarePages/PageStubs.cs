using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages;

// ---------------------------------------------------------------------------
// Stub classes for pages not yet ported — concrete implementations TBD
// ---------------------------------------------------------------------------

public class UniGetUILogPage : UserControl { }

public class ManagerLogsPage : UserControl
{
    public void LoadForManager(IPackageManager manager) { }
}

public class OperationHistoryPage : UserControl { }

public class HelpPage : UserControl
{
    private const string HelpBaseUrl = "https://marticliment.com/unigetui/help/";
    private string _currentUrl = HelpBaseUrl;

    public HelpPage()
    {
        var icon = new SvgIcon
        {
            Path = "avares://UniGetUI.Avalonia/Assets/Symbols/help.svg",
            Width = 64,
            Height = 64,
            Foreground = Application.Current?.FindResource("SystemControlForegroundBaseHighBrush") as IBrush ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = CoreTools.Translate("Help & Documentation"),
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 8),
        };

        var subtitle = new TextBlock
        {
            Text = CoreTools.Translate("Embedded web view is not yet available on this platform."),
            FontSize = 14,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        var openBtn = new Button
        {
            Content = CoreTools.Translate("Open documentation in browser"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
            Padding = new Thickness(20, 10),
            CornerRadius = new CornerRadius(6),
        };
        openBtn.Click += (_, _) =>
            Process.Start(new ProcessStartInfo(_currentUrl) { UseShellExecute = true });

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 480,
        };
        panel.Children.Add(icon);
        panel.Children.Add(title);
        panel.Children.Add(subtitle);
        panel.Children.Add(openBtn);

        Content = panel;
    }

    public void NavigateTo(string uriAttachment)
    {
        _currentUrl = string.IsNullOrEmpty(uriAttachment)
            ? HelpBaseUrl
            : HelpBaseUrl + uriAttachment;
    }
}
