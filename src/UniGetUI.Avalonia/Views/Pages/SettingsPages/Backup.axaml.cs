using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Backup : UserControl, ISettingsPage, IDisposable
{
    private readonly BackupViewModel _viewModel;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Backup and Restore");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Backup()
    {
        _viewModel = new BackupViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.RestartRequired += OnRestartRequired;
    }

    private void OnRestartRequired(object? sender, EventArgs e) => RestartRequired?.Invoke(sender, e);

    public void Dispose()
    {
        _viewModel.RestartRequired -= OnRestartRequired;
        _viewModel.Dispose();
    }
}
