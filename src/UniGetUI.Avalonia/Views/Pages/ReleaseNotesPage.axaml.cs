using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ReleaseNotesPage : UserControl, IEnterLeaveListener
{
    private readonly ReleaseNotesPageViewModel _viewModel;
    private bool _loaded;

    public ReleaseNotesPage()
    {
        _viewModel = new ReleaseNotesPageViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        WebViewControl.NavigationStarted += (_, _) => NavProgressBar.IsVisible = true;
        WebViewControl.NavigationCompleted += (_, e) =>
        {
            NavProgressBar.IsVisible = false;
            _viewModel.CurrentUrl = WebViewControl.Source?.ToString() ?? _viewModel.ReleaseNotesUrl;
        };
    }

    public void OnEnter()
    {
        if (!_loaded)
        {
            WebViewControl.Navigate(new Uri(_viewModel.ReleaseNotesUrl));
            _loaded = true;
        }
    }

    public void OnLeave() { }
}
