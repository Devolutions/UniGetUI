using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class BundleSecurityReportDialog : Window
{
    public BundleSecurityReportDialog(BundleReport report)
    {
        InitializeComponent();

        var sb = new System.Text.StringBuilder();
        foreach (var (pkgId, entries) in report.Contents)
        {
            sb.AppendLine($"• {pkgId}:");
            foreach (var entry in entries)
                sb.AppendLine($"    {(entry.Allowed ? "[allowed]" : "[stripped]")} {entry.Line}");
        }
        ReportText.Text = sb.ToString();

        OkButton.Click += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => OkButton.Focus(), DispatcherPriority.Background);
    }
}
