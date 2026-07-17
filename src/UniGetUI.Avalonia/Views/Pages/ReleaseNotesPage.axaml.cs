using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ReleaseNotesPage : UserControl, IEnterLeaveListener, IDisposable
{
    private readonly ReleaseNotesPageViewModel _viewModel;
    private bool _loaded;
    private bool _adapterReady;
    private bool _disposed;

    public ReleaseNotesPage()
    {
        _viewModel = new ReleaseNotesPageViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        if (OperatingSystem.IsLinux())
        {
            WebViewBorder.IsVisible = false;
            LinuxFallbackPanel.IsVisible = true;
            return;
        }

        WebViewControl.NavigationStarted += (_, _) =>
            NavProgressBar.IsVisible = true;

        WebViewControl.NavigationCompleted += (_, e) =>
        {
            NavProgressBar.IsVisible = false;
            _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? _viewModel.ReleaseNotesUrl;
        };

        WebViewControl.AdapterCreated += (_, _) =>
        {
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
        if (!_loaded && _adapterReady)
        {
            WebViewControl.Navigate(new Uri(_viewModel.ReleaseNotesUrl));
            _loaded = true;
        }
    }

    public void OnLeave() { }

    // Detach the WebView so its WebView2 host/controller is released; the page is
    // rebuilt fresh on next visit (MainWindowViewModel drops the cached instance).
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!OperatingSystem.IsLinux())
            WebViewControl.Stop();
        WebViewBorder.Child = null;
    }
}
