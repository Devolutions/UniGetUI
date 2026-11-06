using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ReleaseNotesPage : UserControl, IEnterLeaveListener, IDisposable
{
    private readonly ReleaseNotesPageViewModel _viewModel;
    private bool _disposed;

    public ReleaseNotesPage()
    {
        _viewModel = new ReleaseNotesPageViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        Browser.NavigationCompleted += (_, _) =>
            _viewModel.CurrentUrl = Browser.CurrentUri?.ToString() ?? _viewModel.ReleaseNotesUrl;
        Browser.Navigate(ReleaseNotesLinks.PageUri(ThemeHelper.IsDark));
    }

    public void OnEnter() { }

    public void OnLeave() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Browser.Shutdown();
    }
}
