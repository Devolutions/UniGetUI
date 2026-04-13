using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using Windows.Graphics;

namespace UniGetUI;

internal sealed partial class CrashReportWindow : Window
{
    private readonly string _crashReport;

    public CrashReportWindow(string crashReport)
    {
        _crashReport = crashReport;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        int w = 1200;
        int h = 1175;
        AppWindow.Resize(new SizeInt32(w, h));
        if (area is not null)
        {
            AppWindow.Move(new PointInt32(
                (area.WorkArea.Width - w) / 2,
                (area.WorkArea.Height - h) / 2
            ));
        }

        Title = CoreTools.Translate("UniGetUI – Crash Report");
        TitleText.Text = CoreTools.Translate("UniGetUI has crashed");
        DescriptionText.Text = CoreTools.Translate("Help us fix this by sending a crash report to Devolutions. All fields below are optional.");
        EmailBox.Header = CoreTools.Translate("Email (optional)");
        EmailBox.PlaceholderText = CoreTools.Translate("your@email.com");
        DetailsBox.Header = CoreTools.Translate("Additional details (optional)");
        DetailsBox.PlaceholderText = CoreTools.Translate("Describe what you were doing when the crash occurred…");
        CrashReportText.Header = CoreTools.Translate("Crash report");
        CrashReportText.Text = crashReport;
        DontSendButton.Content = CoreTools.Translate("Don't Send");
        SendButton.Content = CoreTools.Translate("Send Report");
    }

    private async void SendReport_Click(object sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        DontSendButton.IsEnabled = false;
        SendButton.Content = CoreTools.Translate("Sending…");

        string email = EmailBox.Text.Trim();
        string details = DetailsBox.Text.Trim();

        await Task.Run(() => SendReport(_crashReport, email, details));

        Close();
    }

    private void DontSend_Click(object sender, RoutedEventArgs e) => Close();

    private static void SendReport(
        string errorBody,
        string email,
        string message)
    {
        try
        {
            var node = new JsonObject
            {
                ["email"] = email,
                ["message"] = message,
                ["errorMessage"] = errorBody,
                ["productInfo"] = $"UniGetUI {CoreData.VersionName} (Build {CoreData.BuildNumber})"
            };

            using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.Timeout = TimeSpan.FromSeconds(10);
            using var content = new StringContent(
                node.ToJsonString(), Encoding.UTF8, "application/json");
            client.PostAsync(
                "https://cloud.devolutions.net/api/senderrormessage", content)
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Network failures must not prevent the window from closing.
        }
    }
}
