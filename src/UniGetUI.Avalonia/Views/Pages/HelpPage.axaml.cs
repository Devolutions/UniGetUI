using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class HelpPage : UserControl, IEnterLeaveListener
{
    private readonly HelpPageViewModel _viewModel;
    private string _pendingNavigation = HelpPageViewModel.HelpBaseUrl;
    private bool _adapterReady;

    public HelpPage()
    {
        _viewModel = new HelpPageViewModel();
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
            _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? HelpPageViewModel.HelpBaseUrl;
            BackButton.IsEnabled = WebViewControl.CanGoBack;
            ForwardButton.IsEnabled = WebViewControl.CanGoForward;
        };

        // WebView2 on Windows initializes asynchronously after the control is attached
        // to the visual tree. Navigate() called before AdapterCreated is silently dropped.
        // This mirrors WinUI's EnsureCoreWebView2Async() pattern.
        WebViewControl.AdapterCreated += (_, _) =>
        {
            _adapterReady = true;
            WebViewControl.Navigate(new Uri(_pendingNavigation));
        };
    }

    public void NavigateTo(string uriAttachment)
    {
        string url = _viewModel.GetInitialUrl(uriAttachment);
        _pendingNavigation = url;
        if (_adapterReady)
            WebViewControl.Navigate(new Uri(url));
    }

    public void OnEnter()
    {
        if (_adapterReady)
            WebViewControl.Navigate(new Uri(_pendingNavigation));
    }

    public void OnLeave() { }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WebViewControl.CanGoBack)
            WebViewControl.GoBack();
    }

    private void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WebViewControl.CanGoForward)
            WebViewControl.GoForward();
    }

    private void HomeButton_Click(object? sender, RoutedEventArgs e) =>
        WebViewControl.Navigate(new Uri(HelpPageViewModel.HelpBaseUrl));

    private void ReloadButton_Click(object? sender, RoutedEventArgs e) =>
        WebViewControl.Refresh();
}
