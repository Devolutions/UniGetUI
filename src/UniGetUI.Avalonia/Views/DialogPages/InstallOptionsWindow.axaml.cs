using Avalonia.Controls;
using Avalonia.Input;
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

        // Drop the OS title-bar strip but keep the system min/close buttons, extending the
        // client area into the title-bar region (matches the Manage-ignored-updates window).
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;

        vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(OptionsControl.FocusProfileSelector, DispatcherPriority.Background);
    }

    // The client area is extended over the title bar, so provide window dragging from the
    // transparent strip at the top.
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
