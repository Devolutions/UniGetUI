using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ReleaseNotesPage : UserControl, IEnterLeaveListener, IDisposable
{
    private readonly ReleaseNotesPageViewModel _viewModel;
    private bool _loaded;
    private bool _adapterReady;
    private bool _webViewDisabled;
    private bool _disposed;

    public ReleaseNotesPage()
    {
        _viewModel = new ReleaseNotesPageViewModel();
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
            _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? _viewModel.ReleaseNotesUrl;
        };

        WebViewControl.AdapterCreated += (_, _) =>
        {
            if (_webViewDisabled) return;
            _adapterReady = true;
            if (!_loaded)
            {
                WebViewControl.Navigate(new Uri(_viewModel.ReleaseNotesUrl));
                _loaded = true;
            }
        };
    }

    public void OnEnter()
    {
        if (!_webViewDisabled && !_loaded && _adapterReady)
        {
            WebViewControl.Navigate(new Uri(_viewModel.ReleaseNotesUrl));
            _loaded = true;
        }
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
        WebViewBorder.IsVisible = false;

        // Last, because tearing down an already-broken native control can throw: by now the
        // page is usable regardless of what happens here.
        WebViewBorder.Child = null;
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
