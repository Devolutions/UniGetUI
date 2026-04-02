using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views;

public partial class PackageDetailsWindow : Window
{
    /// <summary>
    /// True when the user confirmed the main action (install/update/uninstall) without extras.
    /// Callers check this after ShowDialog() to trigger the default operation.
    /// </summary>
    public bool ShouldProceedWithOperation { get; private set; }

    private readonly PackageDetailsViewModel _vm;

    public PackageDetailsWindow(IPackage package, OperationType operation)
    {
        _vm = new PackageDetailsViewModel(package, operation);
        DataContext = _vm;
        InitializeComponent();

        _vm.CloseRequested += (_, _) => Close();

        MainActionButton.Click += (_, _) => OnMainAction();
        ActionVariantsButton.Flyout = BuildActionFlyout();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = _vm.LoadDetailsAsync();
        TelemetryHandler.PackageDetails(_vm.Package, _vm.OperationRole.ToString());
    }

    private MenuFlyout BuildActionFlyout()
    {
        var flyout = new MenuFlyout();

        var asAdmin = new MenuItem
        {
            Header = _vm.AsAdminLabel,
            IsEnabled = _vm.CanRunAsAdmin,
        };
        var interactive = new MenuItem
        {
            Header = _vm.InteractiveLabel,
            IsEnabled = _vm.CanRunInteractively,
        };
        var skipOrRemove = new MenuItem
        {
            Header = _vm.SkipHashOrRemoveDataLabel,
            IsEnabled = _vm.CanSkipHashOrRemoveData,
        };

        var role = _vm.OperationRole;
        if (role is OperationType.Uninstall)
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, remove_data: true);
        }
        else
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, no_integrity: true);
        }

        flyout.Items.Add(asAdmin);
        flyout.Items.Add(interactive);
        flyout.Items.Add(skipOrRemove);
        return flyout;
    }

    // ── Action handlers ────────────────────────────────────────────────────────

    private void OnMainAction()
    {
        ShouldProceedWithOperation = true;
        Close();
    }

    private async Task LaunchAndClose(
        OperationType role,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null,
        bool? remove_data = null)
    {
        Close();

        var pkg = _vm.Package;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(
            pkg,
            elevated: elevated,
            interactive: interactive,
            no_integrity: no_integrity,
            remove_data: remove_data);

        AbstractOperation op = role switch
        {
            OperationType.Install => new InstallPackageOperation(pkg, opts),
            OperationType.Update => new UpdatePackageOperation(pkg, opts),
            OperationType.Uninstall => new UninstallPackageOperation(pkg, opts),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        switch (role)
        {
            case OperationType.Install:
                op.OperationSucceeded += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.SUCCESS, TEL_InstallReferral.DIRECT_SEARCH);
                op.OperationFailed += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.FAILED, TEL_InstallReferral.DIRECT_SEARCH);
                break;
            case OperationType.Update:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
                break;
            case OperationType.Uninstall:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.FAILED);
                break;
        }

        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }
}
