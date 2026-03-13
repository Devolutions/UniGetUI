using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class DocumentationBrowserWindow : Window
{
    private static readonly Uri _homeUri = new("https://marticliment.com/unigetui/help/");
    private static readonly Regex _titleRegex = new("<title>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _migrationNoticeRegex = new("<div class=['\"]unigetui-migrate['\"]>.*?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _h1Regex = new("<h1[^>]*>(?<value>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _articleLinkRegex = new("<div class=mainArticleEntryDiv onclick=\"location.href='(?<href>[^']+)'\".*?<h3>(?<text>.*?)</h3>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _anchorRegex = new("<a[^>]*href=['\"](?<href>[^'\"]+)['\"][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _tagRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _scriptRegex = new("<(script|style|noscript)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex _breakRegex = new("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _blockEndRegex = new("</(p|div|section|article|h1|h2|h3|h4|h5|h6|li|ul|ol|pre|blockquote)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex _lineCollapseRegex = new("\\n{3,}", RegexOptions.Compiled);
    private static readonly Regex _markdownLinkRegex = new("\\[(?<text>[^\\]]+)\\]\\((?<href>[^)]+)\\)", RegexOptions.Compiled);

    private readonly List<Uri> _history = [];

    private int _historyIndex = -1;
    private bool _isLoading;

    private TextBlock TitleBlockControl => GetControl<TextBlock>("TitleBlock");
    private Button BackButtonControl => GetControl<Button>("BackButton");
    private Button ForwardButtonControl => GetControl<Button>("ForwardButton");
    private Button HomeButtonControl => GetControl<Button>("HomeButton");
    private Button ReloadButtonControl => GetControl<Button>("ReloadButton");
    private Button BrowserButtonControl => GetControl<Button>("BrowserButton");
    private TextBox UrlTextBoxControl => GetControl<TextBox>("UrlTextBox");
    private TextBlock StatusBlockControl => GetControl<TextBlock>("StatusBlock");
    private ProgressBar LoadingProgressBarControl => GetControl<ProgressBar>("LoadingProgressBar");
    private TextBlock PageTitleBlockControl => GetControl<TextBlock>("PageTitleBlock");
    private SelectableTextBlock PageContentBlockControl => GetControl<SelectableTextBlock>("PageContentBlock");
    private Border LinksSectionBorderControl => GetControl<Border>("LinksSectionBorder");
    private TextBlock LinksTitleBlockControl => GetControl<TextBlock>("LinksTitleBlock");
    private StackPanel LinksPanelControl => GetControl<StackPanel>("LinksPanel");

    public DocumentationBrowserWindow()
        : this(_homeUri) { }

    public DocumentationBrowserWindow(Uri initialUri)
    {
        InitializeComponent();
        Opened += DocumentationBrowserWindow_OnOpened;

        Title = CoreTools.Translate("Help");
        TitleBlockControl.Text = CoreTools.Translate("Help");
        HomeButtonControl.Content = CoreTools.Translate("Help");
        ReloadButtonControl.Content = CoreTools.Translate("Reload");
        BrowserButtonControl.Content = CoreTools.Translate("View page on browser");
        LinksTitleBlockControl.Text = CoreTools.Translate("Help");

        if (initialUri != _homeUri)
        {
            _history.Add(initialUri);
            _historyIndex = 0;
        }
    }

    private async void DocumentationBrowserWindow_OnOpened(object? sender, EventArgs e)
    {
        if (_historyIndex >= 0)
        {
            await NavigateToAsync(_history[_historyIndex], addToHistory: false);
        }
        else
        {
            await NavigateToAsync(_homeUri);
        }
    }

    private async Task NavigateToAsync(Uri uri, bool addToHistory = true)
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        SetLoadingState(true, CoreTools.Translate("Loading..."));

        try
        {
            DocumentationPage page = await LoadPageAsync(uri);

            if (addToHistory)
            {
                if (_historyIndex < _history.Count - 1)
                {
                    _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                }

                _history.Add(page.Uri);
                _historyIndex = _history.Count - 1;
            }

            UrlTextBoxControl.Text = page.Uri.AbsoluteUri;
            PageTitleBlockControl.Text = page.Title;
            PageContentBlockControl.Text = page.Content;
            PopulateLinks(page.Links);
            SetLoadingState(false, string.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error($"Documentation browser failed to load {uri}");
            Logger.Error(ex);

            UrlTextBoxControl.Text = uri.AbsoluteUri;
            PageTitleBlockControl.Text = CoreTools.Translate("Help");
            PageContentBlockControl.Text = CoreTools.Translate("Please try again later") + "\n\n" + ex.Message;
            PopulateLinks([]);
            SetLoadingState(false, CoreTools.Translate("Please try again later"));
        }
        finally
        {
            _isLoading = false;
            UpdateNavigationButtons();
        }
    }

    private static async Task<DocumentationPage> LoadPageAsync(Uri uri)
    {
        if (TryConvertGitHubBlobToRaw(uri, out Uri? rawUri) && rawUri is not null)
        {
            return await LoadMarkdownPageAsync(uri, rawUri);
        }

        using HttpClient client = new(CoreTools.GenericHttpClientParameters);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UniGetUI.Avalonia", CoreData.VersionName));
        string html = await client.GetStringAsync(uri);
        return ParseHtmlPage(uri, html);
    }

    private static async Task<DocumentationPage> LoadMarkdownPageAsync(Uri originalUri, Uri rawUri)
    {
        using HttpClient client = new(CoreTools.GenericHttpClientParameters);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UniGetUI.Avalonia", CoreData.VersionName));
        string markdown = await client.GetStringAsync(rawUri);

        var links = new List<DocumentationLink>();
        foreach (Match match in _markdownLinkRegex.Matches(markdown))
        {
            string href = match.Groups["href"].Value.Trim();
            string text = HtmlDecodeAndNormalize(match.Groups["text"].Value);

            if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text))
                continue;

            if (Uri.TryCreate(originalUri, href, out Uri? linkUri))
            {
                links.Add(new DocumentationLink(text, linkUri));
            }
        }

        string title = Path.GetFileNameWithoutExtension(originalUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(title))
            title = CoreTools.Translate("Help");

        return new DocumentationPage(CoreTools.FormatAsName(title), markdown.Trim(), DistinctLinks(links), originalUri);
    }

    private static DocumentationPage ParseHtmlPage(Uri pageUri, string html)
    {
        string fragment = NormalizeDocumentationContent(ExtractMainContent(html));
        string title = ExtractTitle(html, fragment);
        string text = HtmlToReadableText(fragment);
        var links = ExtractLinks(pageUri, fragment);
        return new DocumentationPage(title, text, links, pageUri);
    }

    private static string ExtractTitle(string html, string fragment)
    {
        Match headingMatch = _h1Regex.Match(fragment);
        if (headingMatch.Success)
        {
            string heading = HtmlDecodeAndNormalize(_tagRegex.Replace(headingMatch.Groups["value"].Value, string.Empty));
            if (!string.IsNullOrWhiteSpace(heading))
                return heading;
        }

        Match titleMatch = _titleRegex.Match(html);
        if (!titleMatch.Success)
            return CoreTools.Translate("Help");

        string title = HtmlDecodeAndNormalize(titleMatch.Groups["value"].Value);
        return string.IsNullOrWhiteSpace(title) ? CoreTools.Translate("Help") : title;
    }

    private static string ExtractMainContent(string html)
    {
        string mainContent = ExtractSection(html, ["id='mainContent'", "id=\"mainContent\""], ["</body>"]);
        if (string.IsNullOrWhiteSpace(mainContent))
            return html;

        string contentIsland = ExtractSection(mainContent, ["class='contentIsland'", "class=\"contentIsland\""], ["<p class=\"footer\"", "<p class='footer'", "<script defer src="]);
        return string.IsNullOrWhiteSpace(contentIsland) ? mainContent : contentIsland;
    }

    private static string NormalizeDocumentationContent(string html)
    {
        string normalized = _migrationNoticeRegex.Replace(html, string.Empty);
        return normalized.Trim();
    }

    private static string HtmlToReadableText(string html)
    {
        string sanitized = _scriptRegex.Replace(html, string.Empty);
        sanitized = _breakRegex.Replace(sanitized, "\n");
        sanitized = _blockEndRegex.Replace(sanitized, "\n\n");
        sanitized = _tagRegex.Replace(sanitized, string.Empty);
        sanitized = WebUtility.HtmlDecode(sanitized).Replace("\r", string.Empty);

        var lines = sanitized.Split('\n')
            .Select(line => Regex.Replace(line, "\\s+", " ").Trim())
            .ToArray();

        string normalized = string.Join("\n", lines);
        normalized = _lineCollapseRegex.Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    private static IReadOnlyList<DocumentationLink> ExtractLinks(Uri currentUri, string html)
    {
        var links = new List<DocumentationLink>();

        foreach (Match match in _articleLinkRegex.Matches(html))
        {
            string href = match.Groups["href"].Value.Trim();
            string text = HtmlDecodeAndNormalize(match.Groups["text"].Value);

            if (Uri.TryCreate(currentUri, href, out Uri? linkUri) && ShouldKeepLink(currentUri, linkUri, text))
            {
                links.Add(new DocumentationLink(text, linkUri));
            }
        }

        if (links.Count > 0)
        {
            return DistinctLinks(links);
        }

        foreach (Match match in _anchorRegex.Matches(html))
        {
            string href = match.Groups["href"].Value.Trim();
            string text = HtmlDecodeAndNormalize(_tagRegex.Replace(match.Groups["text"].Value, string.Empty));

            if (Uri.TryCreate(currentUri, href, out Uri? linkUri) && ShouldKeepLink(currentUri, linkUri, text))
            {
                links.Add(new DocumentationLink(text, linkUri));
            }
        }

        return DistinctLinks(links);
    }

    private static IReadOnlyList<DocumentationLink> DistinctLinks(IEnumerable<DocumentationLink> links)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<DocumentationLink>();

        foreach (DocumentationLink link in links)
        {
            if (seen.Add(link.Uri.AbsoluteUri))
            {
                distinct.Add(link);
            }
        }

        return distinct;
    }

    private static bool ShouldKeepLink(Uri currentUri, Uri linkUri, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (linkUri.Scheme is not ("http" or "https"))
            return false;

        if (!IsDocumentationLink(linkUri))
            return false;

        if (string.Equals(currentUri.GetLeftPart(UriPartial.Path), linkUri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(linkUri.Fragment))
        {
            return false;
        }

        return true;
    }

    private static bool IsDocumentationLink(Uri uri)
    {
        if (uri.Host.EndsWith("marticliment.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith("/unigetui/help", StringComparison.OrdinalIgnoreCase);
        }

        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith("/devolutions/UniGetUI/blob/", StringComparison.OrdinalIgnoreCase);
        }

        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.StartsWith("/devolutions/UniGetUI/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ExtractSection(string html, IReadOnlyList<string> startMarkers, IReadOnlyList<string> endMarkers)
    {
        int markerIndex = FindFirstIndex(html, startMarkers);
        if (markerIndex < 0)
            return string.Empty;

        int start = html.IndexOf('>', markerIndex);
        if (start < 0 || start >= html.Length - 1)
            return string.Empty;

        start++;

        int end = FindFirstIndex(html, endMarkers, start);
        if (end < 0 || end <= start)
            end = html.Length;

        return html[start..end];
    }

    private static int FindFirstIndex(string value, IReadOnlyList<string> candidates, int startIndex = 0)
    {
        int matchIndex = -1;

        foreach (string candidate in candidates)
        {
            int index = value.IndexOf(candidate, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (matchIndex < 0 || index < matchIndex))
            {
                matchIndex = index;
            }
        }

        return matchIndex;
    }

    private static string HtmlDecodeAndNormalize(string value)
    {
        return Regex.Replace(WebUtility.HtmlDecode(value), "\\s+", " ").Trim();
    }

    private static bool TryConvertGitHubBlobToRaw(Uri uri, out Uri? rawUri)
    {
        rawUri = null;

        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 5 || !segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase))
            return false;

        string owner = segments[0];
        string repo = segments[1];
        string branch = segments[3];
        string path = string.Join('/', segments.Skip(4));

        rawUri = new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}");
        return true;
    }

    private void PopulateLinks(IReadOnlyList<DocumentationLink> links)
    {
        LinksPanelControl.Children.Clear();
        LinksSectionBorderControl.IsVisible = links.Count > 0;

        foreach (DocumentationLink link in links)
        {
            Uri targetUri = link.Uri;
            bool isInternal = CanLoadInApp(targetUri);
            var button = new Button
            {
                Content = isInternal ? link.Text : link.Text + " ↗",
                HorizontalAlignment = HorizontalAlignment.Left,
                Classes = { "toolbar-secondary" },
            };

            button.Click += async (_, _) =>
            {
                if (CanLoadInApp(targetUri))
                {
                    await NavigateToAsync(targetUri);
                }
                else
                {
                    OpenInBrowser(targetUri.AbsoluteUri);
                }
            };

            LinksPanelControl.Children.Add(button);
        }
    }

    private static bool CanLoadInApp(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
            return false;

        return IsDocumentationLink(uri);
    }

    private void SetLoadingState(bool isLoading, string statusText)
    {
        LoadingProgressBarControl.IsVisible = isLoading;
        StatusBlockControl.Text = statusText;
        StatusBlockControl.IsVisible = isLoading || !string.IsNullOrWhiteSpace(statusText);
        BackButtonControl.IsEnabled = !isLoading && _historyIndex > 0;
        ForwardButtonControl.IsEnabled = !isLoading && _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        HomeButtonControl.IsEnabled = !isLoading;
        ReloadButtonControl.IsEnabled = !isLoading && _historyIndex >= 0;
        BrowserButtonControl.IsEnabled = !isLoading && !string.IsNullOrWhiteSpace(UrlTextBoxControl.Text);
    }

    private void UpdateNavigationButtons()
    {
        BackButtonControl.IsEnabled = _historyIndex > 0;
        ForwardButtonControl.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        HomeButtonControl.IsEnabled = !_isLoading;
        ReloadButtonControl.IsEnabled = !_isLoading && _historyIndex >= 0;
        BrowserButtonControl.IsEnabled = !_isLoading && !string.IsNullOrWhiteSpace(UrlTextBoxControl.Text);
    }

    private async void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
            return;

        _historyIndex--;
        await NavigateToAsync(_history[_historyIndex], addToHistory: false);
    }

    private async void ForwardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0 || _historyIndex >= _history.Count - 1)
            return;

        _historyIndex++;
        await NavigateToAsync(_history[_historyIndex], addToHistory: false);
    }

    private async void HomeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await NavigateToAsync(_homeUri);
    }

    private async void ReloadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0)
            return;

        await NavigateToAsync(_history[_historyIndex], addToHistory: false);
    }

    private void BrowserButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlTextBoxControl.Text))
            return;

        OpenInBrowser(UrlTextBoxControl.Text);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
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

    private sealed record DocumentationLink(string Text, Uri Uri);

    private sealed record DocumentationPage(string Title, string Content, IReadOnlyList<DocumentationLink> Links, Uri Uri);
}
