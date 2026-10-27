using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Opens a <see cref="PackageLink"/>: looks the package up in the recipient's own managers and
/// shows its details, from where the user decides whether to install it. Nothing is installed
/// without that click.
/// </summary>
internal static class PackageLinkHandler
{
    public static async Task OpenAsync(PackageLink link, MainWindow window)
    {
        Logger.ImportantInfo($"Opening the package link {link.ToUri()}");
        MainWindowViewModel? viewModel = window.DataContext as MainWindowViewModel;

        InfoBarViewModel searching = new()
        {
            Title = CoreTools.Translate("Looking for {0}...", link.DisplayName),
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            IsOpen = true,
        };
        searching.OnClosed = () => viewModel?.DismissToast(searching);
        viewModel?.ShowToast(searching);

        IPackage? package = null;
        string? problem = DescribeUnavailableManager(link);
        try
        {
            if (problem is null)
            {
                package = await Task.Run(() => IpcPackageApi.FindCatalogPackage(new IpcPackageActionRequest
                {
                    PackageId = link.Id,
                    ManagerName = link.Manager,
                    PackageSource = link.Source,
                }));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"The package link {link.ToUri()} did not resolve to a package");
            Logger.Warn(ex);
        }
        finally
        {
            viewModel?.DismissToast(searching);
        }

        if (package is null)
        {
            ShowProblem(viewModel, problem ?? DescribeNotFound(link));
            return;
        }

        var dialog = new PackageDetailsWindow(package, OperationType.Install, TEL_InstallReferral.FROM_LINK)
        {
            InstallVersion = link.Version,
        };
        await dialog.ShowDialog(window);

        if (dialog.ShouldProceedWithOperation)
            await PackagesPageViewModel.LaunchInstall(
                [package], version: link.Version, referral: TEL_InstallReferral.FROM_LINK);
    }

    private static string? DescribeUnavailableManager(PackageLink link)
    {
        // A link without a manager searches every ready manager.
        if (link.Manager is null)
            return null;

        IPackageManager? manager = PEInterface.Managers.FirstOrDefault(candidate =>
            candidate.Id.Equals(link.Manager, StringComparison.OrdinalIgnoreCase));

        if (manager is null)
            return CoreTools.Translate("This version of UniGetUI does not support the package manager {0}.", link.Manager);

        if (!manager.IsEnabled())
            return CoreTools.Translate("{0} is disabled. Enable it in the settings to open this package.", manager.DisplayName);

        if (!manager.IsReady())
            return CoreTools.Translate("{0} is not available on this computer.", manager.DisplayName);

        return null;
    }

    private static string DescribeNotFound(PackageLink link)
    {
        string? managerName = link.Manager is null
            ? null
            : PEInterface.Managers.FirstOrDefault(candidate =>
                candidate.Id.Equals(link.Manager, StringComparison.OrdinalIgnoreCase))?.DisplayName;

        return managerName is null
            ? CoreTools.Translate("{0} was not found.", link.DisplayName)
            : CoreTools.Translate("{0} was not found in {1}.", link.DisplayName, managerName);
    }

    private static void ShowProblem(MainWindowViewModel? viewModel, string message)
    {
        Logger.Warn(message);
        if (viewModel is null)
            return;

        InfoBarViewModel toast = new()
        {
            Title = CoreTools.Translate("The package link could not be opened"),
            Message = message,
            Severity = InfoBarSeverity.Error,
            IsClosable = true,
            IsOpen = true,
        };
        toast.OnClosed = () => viewModel.DismissToast(toast);
        viewModel.ShowToast(toast);
    }
}
