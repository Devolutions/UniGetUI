using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ReleaseNotesWindow : Window
{
    private readonly string _releaseNotesUrl =
        $"https://github.com/Devolutions/UniGetUI/releases/tag/{CoreData.VersionName}";
    private readonly string _releaseNotesApiUrl =
        $"https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/{Uri.EscapeDataString(CoreData.VersionName)}";

    private bool _hasLoadedReleaseNotes;

    private TextBlock TitleBlockControl => GetControl<TextBlock>("TitleBlock");
    private TextBlock VersionBlockControl => GetControl<TextBlock>("VersionBlock");
    private TextBlock StatusBlockControl => GetControl<TextBlock>("StatusBlock");
    private ProgressBar LoadingProgressBarControl => GetControl<ProgressBar>("LoadingProgressBar");
    private TextBlock ReleaseNotesLabelBlockControl => GetControl<TextBlock>("ReleaseNotesLabelBlock");
    private TextBlock ReleaseNotesTextBlockControl => GetControl<TextBlock>("ReleaseNotesTextBlock");
    private TextBlock UrlLabelBlockControl => GetControl<TextBlock>("UrlLabelBlock");
    private TextBox UrlTextBoxControl => GetControl<TextBox>("UrlTextBox");
    private Button RetryButtonControl => GetControl<Button>("RetryButton");
    private Button OpenButtonControl => GetControl<Button>("OpenButton");
    private Button CloseButtonControl => GetControl<Button>("CloseButton");

    public ReleaseNotesWindow()
    {
        InitializeComponent();
        Opened += ReleaseNotesWindow_OnOpened;

        Title = CoreTools.Translate("Release notes");
        TitleBlockControl.Text = CoreTools.Translate("Release notes");
        VersionBlockControl.Text = CoreTools.Translate("version {0}", CoreData.VersionName);
        ReleaseNotesLabelBlockControl.Text = CoreTools.Translate("Release notes:");
        UrlLabelBlockControl.Text = CoreTools.Translate("Release notes URL:");
        UrlTextBoxControl.Text = _releaseNotesUrl;
        StatusBlockControl.Text = CoreTools.Translate("Loading...");
        ReleaseNotesTextBlockControl.Text = string.Empty;
        RetryButtonControl.Content = CoreTools.Translate("Retry");
        OpenButtonControl.Content = CoreTools.Translate("View page on browser");
        CloseButtonControl.Content = CoreTools.Translate("Close");

        SetLoadingState(isLoading: true);
    }

    private async void ReleaseNotesWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_hasLoadedReleaseNotes)
        {
            return;
        }

        _hasLoadedReleaseNotes = true;
        await LoadReleaseNotesAsync();
    }

    private async Task LoadReleaseNotesAsync()
    {
        SetLoadingState(isLoading: true);

        try
        {
            using HttpClient client = new(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("UniGetUI.Avalonia", CoreData.VersionName)
            );
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await client.GetAsync(_releaseNotesApiUrl);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub API returned status code {(int)response.StatusCode} ({response.StatusCode})."
                );
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
            GitHubReleaseResponse? release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
                responseStream,
                SerializationHelpers.DefaultOptions
            );

            string releaseBody = string.IsNullOrWhiteSpace(release?.Body)
                ? CoreTools.Translate("Please try again later")
                : release.Body.Replace("\r\n", "\n").Trim();

            if (!string.IsNullOrWhiteSpace(release?.HtmlUrl))
            {
                UrlTextBoxControl.Text = release.HtmlUrl;
            }

            ReleaseNotesTextBlockControl.Text = releaseBody;
            StatusBlockControl.IsVisible = false;
            RetryButtonControl.IsVisible = false;
            LoadingProgressBarControl.IsVisible = false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load release notes from {_releaseNotesApiUrl}");
            Logger.Error(ex);

            ReleaseNotesTextBlockControl.Text = string.Empty;
            StatusBlockControl.Text = CoreTools.Translate("Please try again later");
            StatusBlockControl.IsVisible = true;
            RetryButtonControl.IsVisible = true;
            LoadingProgressBarControl.IsVisible = false;
        }
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingProgressBarControl.IsVisible = isLoading;
        StatusBlockControl.IsVisible = isLoading;
        StatusBlockControl.Text = isLoading ? CoreTools.Translate("Loading...") : string.Empty;
        RetryButtonControl.IsVisible = false;
        if (isLoading)
        {
            ReleaseNotesTextBlockControl.Text = string.Empty;
        }
    }

    private async void RetryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadReleaseNotesAsync();
    }

    private void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = string.IsNullOrWhiteSpace(UrlTextBoxControl.Text)
                        ? _releaseNotesUrl
                        : UrlTextBoxControl.Text,
                    UseShellExecute = true,
                }
            );
        }
        catch
        {
            // best effort
        }
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
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

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("body")]
        public string Body { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;
    }
}
