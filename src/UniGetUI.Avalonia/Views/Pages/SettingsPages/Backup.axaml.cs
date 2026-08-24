using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;
using CornerRadius = global::Avalonia.CornerRadius;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Backup : UserControl, ISettingsPage, IDisposable
{
    private readonly BackupViewModel _viewModel;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Backup and Restore");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public Backup()
    {
        _viewModel = new BackupViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.RestartRequired += OnRestartRequired;
        _viewModel.NavigationRequested += OnNavigationRequested;

        foreach (var (name, val) in _viewModel.MaxBackupCountItems)
            MaxBackupCountCard.AddItem(name, val, false);
        MaxBackupCountCard.ShowAddedItems();

        MaxBackupCountCard.ValueChanged += (_, _) => RefreshMaxBackupCountLayout();
        RefreshMaxBackupCountLayout();
    }

    private void RefreshMaxBackupCountLayout()
    {
        bool isCustom = CoreSettings.GetValue(CoreSettings.K.MaxLocalBackupCount) == "custom";
        _viewModel.IsCustomBackupCountSelected = isCustom;
        MaxBackupCountCard.CornerRadius = isCustom
            ? new CornerRadius(0)
            : new CornerRadius(0, 0, 8, 8);
    }

    private void OnRestartRequired(object? sender, EventArgs e) => RestartRequired?.Invoke(sender, e);

    private void OnNavigationRequested(object? sender, Type page) => NavigationRequested?.Invoke(sender, page);

    public void Dispose()
    {
        _viewModel.RestartRequired -= OnRestartRequired;
        _viewModel.NavigationRequested -= OnNavigationRequested;
        _viewModel.Dispose();
    }
}
