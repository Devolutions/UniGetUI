using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Avalonia-side helpers for bulk package update operations, consumed by
/// the BackgroundApi event handlers and the --updateapps CLI flag.
/// </summary>
internal static class AvaloniaPackageOperationHelper
{
    public static async Task UpdateAllAsync()
    {
        foreach (var pkg in UpgradablePackagesLoader.Instance.Packages.ToList())
        {
            if (pkg.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue) continue;
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            var op = new UpdatePackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    public static async Task UpdateAllForManagerAsync(string managerName)
    {
        foreach (var pkg in UpgradablePackagesLoader.Instance.Packages
            .Where(p => p.Manager.Name == managerName || p.Manager.DisplayName == managerName)
            .ToList())
        {
            if (pkg.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue) continue;
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            var op = new UpdatePackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    public static async Task UpdateForIdAsync(string packageId)
    {
        var pkg = UpgradablePackagesLoader.Instance.Packages.FirstOrDefault(p => p.Id == packageId);
        if (pkg is null)
        {
            Logger.Warn($"BackgroundApi: no upgradable package found with id={packageId}");
            return;
        }

        var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
        var op = new UpdatePackageOperation(pkg, opts);
        op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
        op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }

    /// <summary>
    /// Prompts the user with a save-file dialog and downloads the installer for
    /// a single package into the chosen location.
    /// </summary>
    public static async Task AskLocationAndDownloadAsync(IPackage? package, TEL_InstallReferral referral)
    {
        if (package is null) return;
        if (MainWindow.Instance is not { } win) return;

        await package.Details.Load();

        if (package.Details.InstallerUrl is null)
        {
            Logger.Warn($"No installer URL found for {package.Id}");
            return;
        }

        string? suggestedName = await package.GetInstallerFileName();
        if (string.IsNullOrWhiteSpace(suggestedName))
            suggestedName = CoreTools.MakeValidFileName(package.Id) + ".exe";

        string ext = suggestedName.Contains('.')
            ? CoreTools.MakeValidFileName(suggestedName.Split('.')[^1])
            : "exe";

        var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            FileTypeChoices =
            [
                new FilePickerFileType(CoreTools.Translate("Installer")) { Patterns = [$"*.{ext}"] },
                new FilePickerFileType(CoreTools.Translate("Executable")) { Patterns = ["*.exe"] },
                new FilePickerFileType(CoreTools.Translate("MSI")) { Patterns = ["*.msi"] },
                new FilePickerFileType(CoreTools.Translate("Compressed file")) { Patterns = ["*.zip"] },
                new FilePickerFileType(CoreTools.Translate("MSIX")) { Patterns = ["*.msix"] },
            ],
        });

        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var op = new DownloadOperation(package, path);
        op.OperationSucceeded += (_, _) => TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.SUCCESS, referral);
        op.OperationFailed += (_, _) => TelemetryHandler.DownloadPackage(package, TEL_OP_RESULT.FAILED, referral);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }

    /// <summary>
    /// Prompts the user with a folder-picker dialog and downloads the installers
    /// for all eligible packages into the chosen folder.
    /// </summary>
    public static async Task DownloadSelectedAsync(IEnumerable<IPackage> packages, TEL_InstallReferral referral)
    {
        if (MainWindow.Instance is not { } win) return;

        var eligible = packages
            .Where(p => !p.Source.IsVirtualManager && p.Manager.Capabilities.CanDownloadInstaller)
            .ToList();

        if (eligible.Count == 0) return;

        var folders = await win.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });

        var folder = folders.FirstOrDefault();
        var outputPath = folder?.TryGetLocalPath();
        if (outputPath is null) return;

        foreach (var pkg in eligible)
        {
            var op = new DownloadOperation(pkg, outputPath);
            op.OperationSucceeded += (_, _) => TelemetryHandler.DownloadPackage(pkg, TEL_OP_RESULT.SUCCESS, referral);
            op.OperationFailed += (_, _) => TelemetryHandler.DownloadPackage(pkg, TEL_OP_RESULT.FAILED, referral);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    /// <summary>
    /// Runs the WinGet self-repair sequence elevated and shows a result notification.
    /// Only meaningful on Windows; no-ops on other platforms.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static async Task HandleBrokenWinGetAsync()
    {
        var banner = (MainWindow.Instance?.DataContext as MainWindowViewModel)?.WinGetWarningBanner;
        banner?.IsOpen = false;

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CoreData.PowerShell5,
                    Arguments =
                        "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {"
                        + "cmd.exe /C \"rmdir /Q /S `\"%temp%\\WinGet`\"\"; "
                        + "cmd.exe /C \"`\"%localappdata%\\Microsoft\\WindowsApps\\winget.exe`\" source reset --force\"; "
                        + "taskkill /im winget.exe /f; "
                        + "taskkill /im WindowsPackageManagerServer.exe /f; "
                        + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force; "
                        + "Install-Module Microsoft.WinGet.Client -Force -AllowClobber; "
                        + "Import-Module Microsoft.WinGet.Client; "
                        + "Repair-WinGetPackageManager -Force -Latest; "
                        + "Get-AppxPackage -Name 'Microsoft.DesktopAppInstaller' | Reset-AppxPackage; "
                        + "}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                },
            };

            p.Start();
            await p.WaitForExitAsync();

            if (string.Equals(
                Settings.GetValue(Settings.K.WinGetCliToolPreference),
                "pinget",
                StringComparison.OrdinalIgnoreCase))
            {
                Settings.SetValue(Settings.K.WinGetCliToolPreference, "default");
            }

            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("WinGet was repaired successfully"),
                CoreTools.Translate("It is recommended to restart UniGetUI after WinGet has been repaired"),
                MainWindow.RuntimeNotificationLevel.Success);

            _ = UpgradablePackagesLoader.Instance.ReloadPackages();
            _ = InstalledPackagesLoader.Instance.ReloadPackages();
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while trying to repair WinGet");
            Logger.Error(ex);

            banner?.IsOpen = true;

            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("WinGet could not be repaired"),
                CoreTools.Translate("An unexpected issue occurred while attempting to repair WinGet. Please try again later")
                    + " — " + ex.Message,
                MainWindow.RuntimeNotificationLevel.Error);
        }
    }

    /// <summary>
    /// Returns true when the WinGet manager is ready but loaded zero installed packages,
    /// which is a strong signal that WinGet has malfunctioned.
    /// </summary>
    public static bool IsWinGetMalfunctioning()
    {
        var winget = PEInterface.Managers.FirstOrDefault(m => m.Name == "WinGet");
        return winget is not null
            && winget.IsReady()
            && !InstalledPackagesLoader.Instance.Packages.Any(p => p.Manager == winget);
    }
}
