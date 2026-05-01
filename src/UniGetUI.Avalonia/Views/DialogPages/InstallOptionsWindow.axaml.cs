using Avalonia.Controls;
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
        Dispatcher.UIThread.Post(OptionsControl.FocusProfileSelector, DispatcherPriority.Background);
    }
}
