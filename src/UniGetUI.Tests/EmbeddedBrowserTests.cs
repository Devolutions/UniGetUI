using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Controls;

namespace UniGetUI.Tests;

public sealed class EmbeddedBrowserTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(EmbeddedBrowserTests).Assembly);

    private static (Window Window, EmbeddedBrowser Browser) ShowUnavailableBrowser()
    {
        NativeWebViewSupport.MarkUnavailable();
        var browser = new EmbeddedBrowser();
        var window = new Window { Width = 600, Height = 400, Content = browser };
        window.Show();
        return (window, browser);
    }

    [Fact]
    public Task WithoutABrowserEngine_NoWebViewReachesTheVisualTree() => _session.Dispatch(() =>
    {
        var (window, browser) = ShowUnavailableBrowser();

        Assert.Empty(browser.GetVisualDescendants().OfType<UniGetUiWebView>());
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task WithoutABrowserEngine_TheFallbackExplainsWhyAndOffersTheBrowser() => _session.Dispatch(() =>
    {
        var (window, browser) = ShowUnavailableBrowser();

        StackPanel fallback = browser.GetVisualDescendants().OfType<StackPanel>().Single(p => p.IsVisible);
        Assert.Contains(fallback.Children.OfType<TextBlock>(), t => t.Text == NativeWebViewSupport.UnavailableReason);
        Assert.Single(fallback.Children.OfType<Button>());
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task WithoutABrowserEngine_NavigationIsRememberedAndShutdownIsSafe() => _session.Dispatch(() =>
    {
        var (window, browser) = ShowUnavailableBrowser();
        var page = new Uri("https://devolutions.net/data/UniGetUIChangeHistoryUpdate.htm?theme=light");

        browser.Navigate(page);
        browser.SetBrowserVisible(true);
        browser.Shutdown();
        browser.Shutdown();

        Assert.Equal(page, browser.CurrentUri);
        Assert.Empty(browser.GetVisualDescendants().OfType<UniGetUiWebView>());
        window.Close();
    }, CancellationToken.None);
}
