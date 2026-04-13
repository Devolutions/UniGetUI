using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

internal sealed partial class CrashReportWindow : Window
{
    private readonly string _crashReport;

    public CrashReportWindow(string crashReport)
    {
        _crashReport = crashReport;
        InitializeComponent();
        CrashReportText.Text = crashReport;
        DontSendButton.Content = CoreTools.Translate("Don't Send");
        SendButton.Content = CoreTools.Translate("Send Report");
    }

    private async void SendReport_Click(object? sender, RoutedEventArgs e)
    {
        SendButton.IsEnabled = false;
        DontSendButton.IsEnabled = false;
        SendButton.Content = CoreTools.Translate("Sending…");

        string email = EmailBox.Text?.Trim() ?? string.Empty;
        string details = DetailsBox.Text?.Trim() ?? string.Empty;

        await Task.Run(() => SendReport(_crashReport, email, details));

        Close();
    }

    private void DontSend_Click(object? sender, RoutedEventArgs e) => Close();

    private static void SendReport(string errorBody, string email, string message)
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
