using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class HelpPage : UserControl, IEnterLeaveListener
{
    private readonly HelpPageViewModel _viewModel;
    private string _pendingNavigation = HelpPageViewModel.HelpBaseUrl;

    public HelpPage()
    {
        _viewModel = new HelpPageViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        WebViewControl.NavigationStarted += OnNavigationStarted;
        WebViewControl.NavigationCompleted += OnNavigationCompleted;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        NavProgressBar.IsVisible = true;
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        NavProgressBar.IsVisible = false;
        _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? HelpPageViewModel.HelpBaseUrl;

        BackButton.IsEnabled = WebViewControl.CanGoBack;
        ForwardButton.IsEnabled = WebViewControl.CanGoForward;
    }

    public void NavigateTo(string uriAttachment)
    {
        string url = _viewModel.GetInitialUrl(uriAttachment);
        if (WebViewControl.IsLoaded)
            WebViewControl.Navigate(new Uri(url));
        else
            _pendingNavigation = url;
    }

    public void OnEnter()
    {
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
