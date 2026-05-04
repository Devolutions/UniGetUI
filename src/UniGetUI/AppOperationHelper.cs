using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using UniGetUI.Controls.OperationWidgets;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Pages.DialogPages;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace UniGetUI;

public partial class MainApp
{
    public static class Operations
    {
        public static bool AreThereRunningOperations()
        {
            return _operationList.Any()
                && _operationList.Any(x =>
                    x.Operation.Status is OperationStatus.Running or OperationStatus.InQueue
                );
        }

        public static ObservableCollection<OperationControl> _operationList = new();

        public static OperationControl Add(AbstractOperation op)
        {
            IpcOperationApi.Track(op);
            var control = new OperationControl(op);
            _operationList.Add(control);
            return control;
        }

        public static void Remove(OperationControl control) => _operationList.Remove(control);

        public static void Remove(AbstractOperation op)
        {
            foreach (var control in _operationList.Where(x => x.Operation == op).ToArray())
            {
                _operationList.Remove(control);
            }
        }

        /*
         *
         * OPERATION CREATION HELPERS
         *
         */
        public static async Task<AbstractOperation?> AskLocationAndDownload(
            IPackage? package,
            TEL_InstallReferral referral
        )
        {
            if (package is null)
                return null;
            int loadingId = DialogHelper.ShowLoadingDialog(CoreTools.Translate("Please wait..."));
            try
            {
                var details = package.Details;
                await details.Load();

                if (details.InstallerUrl is null)
                {
                    DialogHelper.HideLoadingDialog(loadingId);
                    var dialog = new ContentDialog
                    {
                        Title = CoreTools.Translate("Download failed"),
                        Content = CoreTools.Translate(
                            "No applicable installer was found for the package {0}",
                            package.Name
                        ),
                        PrimaryButtonText = CoreTools.Translate("Ok"),
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = Instance.MainWindow.Content.XamlRoot,
                    };
                    await DialogHelper.ShowDialogAsync(dialog);
                    return null;
                }

                FileSavePicker savePicker = new();
                MainWindow window = Instance.MainWindow;
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
                savePicker.SuggestedStartLocation = PickerLocationId.Downloads;

                string name = await package.GetInstallerFileName() ?? "";
                string extension;
                if (!name.Where(x => x == '.').Any())
                { // As a last resort, we need an extension for the file picker to work
                    extension = "unknown";
                    name = name + "." + extension;
                }
                else
                {
                    extension = CoreTools.MakeValidFileName(name.Split('.')[^1]);
                }

                savePicker.SuggestedFileName = name;

                if (package.Manager is BaseNuGet)
                {
                    extension = "nupkg";
                    savePicker.FileTypeChoices.Add("NuGet package", [".nupkg"]);
                }

                savePicker.FileTypeChoices.Add("Automatic", [$".{extension}"]);
                savePicker.FileTypeChoices.Add("Executable", [".exe"]);
                savePicker.FileTypeChoices.Add("MSI", [".msi"]);
                savePicker.FileTypeChoices.Add("Compressed file", [".zip"]);
                savePicker.FileTypeChoices.Add("MSIX", [".msix"]);
                savePicker.FileTypeChoices.Add("APPX", [".appx"]);
                savePicker.FileTypeChoices.Add("Tarball", [".tar"]);
                savePicker.FileTypeChoices.Add("Compressed Tarball", [".tgz"]);

                StorageFile file = await savePicker.PickSaveFileAsync();

                DialogHelper.HideLoadingDialog(loadingId);
                if (file is not null)
                {
                    var op = new DownloadOperation(package, file.Path);
                    op.OperationSucceeded += (_, _) =>
                        TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.SUCCESS, referral);
                    op.OperationFailed += (_, _) =>
                        TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.FAILED, referral);
                    Add(op);
                    Instance.MainWindow.UpdateSystemTrayStatus();
                    return op;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"An error occurred while downloading the installer for the package {package.Id}"
                );
                Logger.Error(ex);
                DialogHelper.HideLoadingDialog(loadingId);
                return null;
            }
        }

        public static async Task Download(
            IEnumerable<IPackage> packages,
            TEL_InstallReferral referral
        )
        {
            try
            {
                if (!packages.Any())
                    return;

                var hWnd = MainApp.Instance.MainWindow.GetWindowHandle();
                var a = new ExternalLibraries.Pickers.FolderPicker(hWnd);
                var outputPath = await Task.Run(a.Show);
                if (outputPath == "")
                    return;

                foreach (var package in packages)
                {
                    if (
                        package.Source.IsVirtualManager
                        || !package.Manager.Capabilities.CanDownloadInstaller
                    )
                    {
                        Logger.Warn($"Package {package.Id} cannot have its installer downloaded.");
                        continue;
                    }

                    var op = new DownloadOperation(package, outputPath);
                    op.OperationSucceeded += (_, _) =>
                        TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.SUCCESS, referral);
                    op.OperationFailed += (_, _) =>
                        TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.FAILED, referral);
                    Add(op);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while attempting to bulk-download packages:");
                Logger.Error(ex);
            }
        }

        /*
         *
         *
         * PACKAGE INSTALLATION
         *
         *
         */
        public static async Task<AbstractOperation?> Install(
            IPackage? package,
            TEL_InstallReferral referral,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null,
            bool ignoreParallel = false,
            AbstractOperation? req = null
        )
        {
            if (package is null)
                return null;

            var options = await InstallOptionsFactory.LoadApplicableAsync(
                package,
                elevated,
                interactive,
                no_integrity
            );
            var operation = new InstallPackageOperation(package, options, ignoreParallel, req);
            operation.OperationSucceeded += (_, _) =>
                TelemetryHandler.InstallPackage(package, TEL_OP_RESULT.SUCCESS, referral);
            operation.OperationFailed += (_, _) =>
                TelemetryHandler.InstallPackage(package, TEL_OP_RESULT.FAILED, referral);
            Add(operation);
            Instance.MainWindow.UpdateSystemTrayStatus();
            return operation;
        }

        public static void Install(
            IReadOnlyList<IPackage> packages,
            TEL_InstallReferral referral,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null
        )
        {
            foreach (var package in packages)
            {
                _ = Install(package, referral, elevated, interactive, no_integrity);
            }
        }

        public static async Task<AbstractOperation?> UninstallThenReinstall(
            IPackage? package,
            TEL_InstallReferral referral
        )
        {
            if (package is null)
                return null;

            var options = await InstallOptionsFactory.LoadApplicableAsync(package);

            var uninstallOp = new UninstallPackageOperation(package, options);
            uninstallOp.OperationSucceeded += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.SUCCESS);
            uninstallOp.OperationFailed += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.FAILED);

            var installOp = new InstallPackageOperation(package, options, req: uninstallOp);
            installOp.OperationSucceeded += (_, _) =>
                TelemetryHandler.InstallPackage(package, TEL_OP_RESULT.SUCCESS, referral);
            installOp.OperationFailed += (_, _) =>
                TelemetryHandler.InstallPackage(package, TEL_OP_RESULT.FAILED, referral);

            Add(installOp);
            Instance.MainWindow.UpdateSystemTrayStatus();
            return installOp;
        }

        /*
         *
         *
         * PACKAGE UPDATE
         *
         *
         */
        public static async Task<AbstractOperation?> Update(
            IPackage? package,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null,
            bool ignoreParallel = false,
            AbstractOperation? req = null
        )
        {
            if (package is null)
                return null;
            if (package.NewerVersionIsInstalled())
            {
                Logger.Warn(
                    $"A newer version of {package.Id} has been detected, the update will not be performed!"
                );
                UpgradablePackagesLoader.Instance.Remove(package);
                foreach (var eq in InstalledPackagesLoader.Instance.GetEquivalentPackages(package))
                { // Remove upgradable tag from all installed packages
                    eq.Tag = PackageTag.Default;
                }

                return null;
            }

            var options = await InstallOptionsFactory.LoadApplicableAsync(
                package,
                elevated,
                interactive,
                no_integrity
            );
            var operation = new UpdatePackageOperation(package, options, ignoreParallel, req);
            operation.OperationSucceeded += (_, _) =>
                TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.SUCCESS);
            operation.OperationFailed += (_, _) =>
                TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.FAILED);
            Add(operation);
            Instance.MainWindow.UpdateSystemTrayStatus();
            return operation;
        }

        public static void Update(
            IReadOnlyList<IPackage> packages,
            bool? elevated = null,
            bool? interactive = null,
            bool? no_integrity = null
        )
        {
            foreach (var package in packages)
            {
                _ = Update(package, elevated, interactive, no_integrity);
            }
        }

        private static bool AlreadyBeingUpdated(IPackage package)
        {
            // Check for duplicate operations already in the queue (prevents duplicates during unattended updates)
            return _operationList.Any(x =>
                x.Operation is UpdatePackageOperation updateOp
                && (x.Operation.Status is OperationStatus.InQueue or OperationStatus.Running)
                && updateOp.Package.GetHash() == package.GetHash()
            );
        }

        public static async Task UpdateAll()
        {
            foreach (IPackage package in UpgradablePackagesLoader.Instance.Packages)
            { // First check will only work if the package has not been reloaded, deeper check is needed, hence AlreadyBeingUpdated
                if (
                    package.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue
                    || AlreadyBeingUpdated(package)
                )
                {
                    Logger.Warn(
                        $"Update operation for package {package.Id} is already queued or running. Skipping duplicate in UpdateAll."
                    );
                    continue;
                }

                await Update(package);
            }
        }

        public static async Task UpdateAllForManager(string managerName)
        {
            foreach (IPackage package in UpgradablePackagesLoader.Instance.Packages)
            {
                if (
                    package.Manager.Id != managerName
                )
                    continue; // Package not from the desired package manager

                // First check will only work if the package has not been reloaded, deeper check is needed, hence AlreadyBeingUpdated
                if (
                    package.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue
                    || AlreadyBeingUpdated(package)
                )
                {
                    Logger.Warn(
                        $"Update operation for package {package.Id} is already queued or running. Skipping duplicate in UpdateAll."
                    );
                    continue;
                }

                await Update(package);
            }
        }

        public static async Task UpdateForId(string packageId)
        {
            foreach (IPackage package in UpgradablePackagesLoader.Instance.Packages)
            {
                if (package.Id == packageId)
                {
                    Logger.Info($"[WIDGETS] Updating package with id {packageId}");
                    await Update(package);
                    return;
                }
            }

            Logger.Warn($"[WIDGETS] No package with id={packageId} was found");
        }

        public static async Task<AbstractOperation?> UninstallThenUpdate(IPackage? package)
        {
            if (package is null)
                return null;

            var options = await InstallOptionsFactory.LoadApplicableAsync(package);

            var uninstallOp = new UninstallPackageOperation(package, options);
            uninstallOp.OperationSucceeded += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.SUCCESS);
            uninstallOp.OperationFailed += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.FAILED);

            options.Version = package.NewVersionString;
            options.OverridesNextLevelOpts = true;
            var installOp = new InstallPackageOperation(package, options, req: uninstallOp);
            installOp.OperationSucceeded += (_, _) =>
                TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.SUCCESS);
            installOp.OperationFailed += (_, _) =>
                TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.FAILED);

            Add(installOp);
            Instance.MainWindow.UpdateSystemTrayStatus();
            return installOp;
        }

        /*
         *
         *
         * PACKAGE UNINSTALL
         *
         *
         */

        public static async Task ConfirmAndUninstall(
            IReadOnlyList<IPackage> packages,
            bool? elevated = null,
            bool? interactive = null,
            bool? remove_data = null
        )
        {
            if (!await DialogHelper.ConfirmUninstallation(packages))
                return;

            await Uninstall(packages, elevated, interactive, remove_data);
        }

        public static async Task ConfirmAndUninstall(
            IPackage? package,
            bool? elevated = null,
            bool? interactive = null,
            bool? remove_data = null
        )
        {
            if (package is null)
                return;
            if (!await DialogHelper.ConfirmUninstallation(package))
                return;

            await Uninstall(package, elevated, interactive, remove_data);
        }

        public static async Task<AbstractOperation?> Uninstall(
            IPackage? package,
            bool? elevated = null,
            bool? interactive = null,
            bool? remove_data = null,
            bool ignoreParallel = false,
            AbstractOperation? req = null
        )
        {
            if (package is null)
                return null;

            var options = await InstallOptionsFactory.LoadApplicableAsync(
                package,
                elevated,
                interactive,
                remove_data: remove_data
            );
            var operation = new UninstallPackageOperation(package, options, ignoreParallel, req);
            operation.OperationSucceeded += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.SUCCESS);
            operation.OperationFailed += (_, _) =>
                TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.FAILED);
            Add(operation);
            Instance.MainWindow.UpdateSystemTrayStatus();
            return operation;
        }

        public static async Task Uninstall(
            IReadOnlyList<IPackage> packages,
            bool? elevated = null,
            bool? interactive = null,
            bool? remove_data = null
        )
        {
            foreach (var package in packages)
            {
                await Uninstall(package, elevated, interactive, remove_data);
            }
        }
    }
}
