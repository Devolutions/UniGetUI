using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Avalonia.Views;

public partial class InstallOptionsWindow : Window
{
    public bool ShouldProceedWithOperation =>
        ((InstallOptionsViewModel)DataContext!).ShouldProceedWithOperation;

    public InstallOptionsWindow(IPackage package, OperationType operation, InstallOptions options)
    {
        var vm = new InstallOptionsViewModel(package, operation, options);
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => ProfileSelectorComboBox.Focus(), DispatcherPriority.Background);
    }

    private async void SelectDir_Click(object? sender, RoutedEventArgs e)
    {
        var results = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (results is [{ } folder])
            ((InstallOptionsViewModel)DataContext!).LocationText =
                folder.TryGetLocalPath() ?? folder.Name;
    }

    private void KillProcessBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter or Key.OemComma)
            ((InstallOptionsViewModel)DataContext!).AddKillProcessCommand.Execute(null);
    }
}
