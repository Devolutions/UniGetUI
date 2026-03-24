using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    private async void SelectDir_Click(object? sender, RoutedEventArgs e)
    {
        var results = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (results is [{ } folder])
            ((InstallOptionsViewModel)DataContext!).LocationText =
                folder.TryGetLocalPath() ?? folder.Name;
    }
}
