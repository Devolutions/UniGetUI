using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class HelpPage : UserControl, IEnterLeaveListener, IDisposable
{
    private readonly HelpPageViewModel _viewModel;
    private string _pendingNavigation = HelpPageViewModel.HelpBaseUrl;
    private bool _adapterReady;
    private bool _webViewDisabled;
    private bool _disposed;

    public HelpPage()
    {
        _viewModel = new HelpPageViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        // #5285: embedding a web view where no browser engine can be hosted crashes the
        // process from an async continuation we cannot catch, so the control must never
        // reach the visual tree in that case.
        if (!NativeWebViewSupport.IsAvailable)
        {
            ShowFallback();
            return;
        }

        NativeWebViewSupport.BecameUnavailable += ShowFallback;

        // Every handler below bails out once the page has switched to the fallback: a
        // callback still in flight at that point must not repaint over it or revive the
        // web view state.
        WebViewControl.NavigationStarted += (_, _) =>
        {
            if (_webViewDisabled) return;
            NavProgressBar.IsVisible = true;
        };

        WebViewControl.NavigationCompleted += (_, e) =>
        {
            if (_webViewDisabled) return;
            NavProgressBar.IsVisible = false;
            _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? HelpPageViewModel.HelpBaseUrl;
            BackButton.IsEnabled = WebViewControl.CanGoBack;
            ForwardButton.IsEnabled = WebViewControl.CanGoForward;
        };

        // WebView2 on Windows initializes asynchronously after the control is attached
        // to the visual tree. Navigate() called before AdapterCreated is silently dropped.
        // This mirrors WinUI's EnsureCoreWebView2Async() pattern.
        WebViewControl.AdapterCreated += (_, _) =>
        {
            if (_webViewDisabled) return;
            _adapterReady = true;
            WebViewControl.Navigate(new Uri(_pendingNavigation));
        };
    }

    public void NavigateTo(string uriAttachment)
    {
        string url = _viewModel.GetInitialUrl(uriAttachment);
        _pendingNavigation = url;
        _viewModel.CurrentUrl = url;
        if (_adapterReady)
            WebViewControl.Navigate(new Uri(url));
    }

    public void OnEnter()
    {
        if (!_webViewDisabled && _adapterReady)
            WebViewControl.Navigate(new Uri(_pendingNavigation));
    }

    public void OnLeave() { }

    // Swaps the web view for a message plus the "Open in browser" button. Detaching the
    // control (rather than just hiding it) is what keeps it from initializing, since a
    // collapsed control is still attached to the visual tree.
    private void ShowFallback()
    {
        if (_webViewDisabled) return;
        _webViewDisabled = true;
        _adapterReady = false;

        WebViewFallbackMessage.Text = NativeWebViewSupport.UnavailableReason;
        WebViewFallbackPanel.IsVisible = true;
        NavProgressBar.IsVisible = false;
        NavigationButtons.IsVisible = false;
        WebViewBorder.IsVisible = false;

        // Last, because tearing down an already-broken native control can throw: by now the
        // page is usable regardless of what happens here.
        WebViewBorder.Child = null;
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_webViewDisabled && WebViewControl.CanGoBack)
            WebViewControl.GoBack();
    }

    private void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_webViewDisabled && WebViewControl.CanGoForward)
            WebViewControl.GoForward();
    }

    private void HomeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_webViewDisabled) return;
        WebViewControl.Navigate(new Uri(HelpPageViewModel.HelpBaseUrl));
    }

    private void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_webViewDisabled) return;
        WebViewControl.Refresh();
    }

    // Detach the WebView so its WebView2 host/controller is released; the page is
    // rebuilt fresh on next visit (MainWindowViewModel drops the cached instance).
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeWebViewSupport.BecameUnavailable -= ShowFallback;
        if (_webViewDisabled) return;
        WebViewControl.Stop();
        WebViewBorder.Child = null;
    }
}
