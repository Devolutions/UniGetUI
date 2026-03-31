using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels;

/// <summary>Badge displayed next to the progress bar (Admin, Interactive, …).</summary>
public sealed record OperationBadgeVm(
    string Label,
    string IconPath,
    string Primary,
    string Secondary
);

public sealed partial class OperationViewModel : ViewModelBase
{
    public AbstractOperation Operation { get; }

    public ObservableCollection<OperationBadgeVm> Badges { get; } = [];

    public ICommand ButtonCommand { get; }
    public ICommand ShowDetailsCommand { get; }

    /// <summary>Flyout for the "…" button; rebuilt each time the operation status changes.</summary>
    public MenuFlyout OpMenu { get; } = new();
    private OperationStatus? _menuState;

    // ── Bindable properties ───────────────────────────────────────────────────
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _liveLine;
    [ObservableProperty] private string _buttonText;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private IBrush _progressBrush;
    [ObservableProperty] private IBrush _backgroundBrush;
    [ObservableProperty] private IImage? _packageIcon;

    private static readonly Uri _fallbackIconUri =
        new("avares://UniGetUI.Avalonia/Assets/package_color.png");

    public OperationViewModel(AbstractOperation operation)
    {
        Operation = operation;
        ButtonCommand = new SyncCommand(ButtonClick);
        ShowDetailsCommand = new SyncCommand(ShowDetails);

        _title = operation.Metadata.Title;
        _liveLine = operation.GetOutput().Any()
            ? operation.GetOutput()[^1].Item1
            : CoreTools.Translate("Please wait...");
        _buttonText = CoreTools.Translate("Cancel");
        _progressBrush = new SolidColorBrush(Color.Parse("#888888"));
        _backgroundBrush = Brushes.Transparent;

        _ = LoadIconAsync();

        // Route all background-thread events to the UI thread
        operation.LogLineAdded += (_, ev) =>
            Dispatcher.UIThread.Post(() => LiveLine = ev.Item1);

        operation.StatusChanged += (_, status) =>
            Dispatcher.UIThread.Post(() => ApplyStatus(status));

        operation.BadgesChanged += (_, badges) =>
            Dispatcher.UIThread.Post(() =>
            {
                Badges.Clear();
                if (badges.AsAdministrator)
                    Badges.Add(new(
                        CoreTools.Translate("Administrator privileges"),
                        "avares://UniGetUI.Avalonia/Assets/Symbols/uac.svg",
                        CoreTools.Translate("This operation is running with administrator privileges."),
                        ""
                    ));
                if (badges.Interactive)
                    Badges.Add(new(
                        CoreTools.Translate("Interactive operation"),
                        "avares://UniGetUI.Avalonia/Assets/Symbols/interactive.svg",
                        CoreTools.Translate("This operation is running interactively."),
                        CoreTools.Translate("You will likely need to interact with the installer.")
                    ));
                if (badges.SkipHashCheck)
                    Badges.Add(new(
                        CoreTools.Translate("Integrity checks skipped"),
                        "avares://UniGetUI.Avalonia/Assets/Symbols/checksum.svg",
                        CoreTools.Translate("Integrity checks will not be performed during this operation."),
                        CoreTools.Translate("Proceed at your own risk.")
                    ));
            });

        // Sync with current status in case the operation already started
        ApplyStatus(operation.Status);
    }

    // ── Icon loading ──────────────────────────────────────────────────────────
    private async Task LoadIconAsync()
    {
        try
        {
            var uri = await Operation.GetOperationIcon();
            Bitmap? bmp = null;
            if (uri.Scheme is "http" or "https")
            {
                using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
                var bytes = await http.GetByteArrayAsync(uri);
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            else if (uri.Scheme is "avares")
            {
                using var stream = AssetLoader.Open(uri);
                bmp = new Bitmap(stream);
            }

            if (bmp is not null)
            {
                Dispatcher.UIThread.Post(() => PackageIcon = bmp);
                return;
            }
        }
        catch { /* icon is optional; fall through to fallback */ }

        try
        {
            using var stream = AssetLoader.Open(_fallbackIconUri);
            var fallback = new Bitmap(stream);
            Dispatcher.UIThread.Post(() => PackageIcon = fallback);
        }
        catch { }
    }

    // ── Status → visual properties ────────────────────────────────────────────
    private void ApplyStatus(OperationStatus status)
    {
        switch (status)
        {
            case OperationStatus.InQueue:
                ProgressIndeterminate = false;
                ProgressValue = 0;
                ProgressBrush = new SolidColorBrush(Color.Parse("#888888"));
                BackgroundBrush = Brushes.Transparent;
                ButtonText = CoreTools.Translate("Cancel");
                break;

            case OperationStatus.Running:
                ProgressIndeterminate = true;
                ProgressBrush = new SolidColorBrush(Color.Parse("#F0A500"));
                BackgroundBrush = new SolidColorBrush(Color.FromArgb(30, 240, 165, 0));
                ButtonText = CoreTools.Translate("Cancel");
                break;

            case OperationStatus.Succeeded:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressBrush = new SolidColorBrush(Color.Parse("#0F7B0F"));
                BackgroundBrush = new SolidColorBrush(Color.FromArgb(30, 15, 123, 15));
                ButtonText = CoreTools.Translate("Close");
                break;

            case OperationStatus.Failed:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressBrush = new SolidColorBrush(Color.Parse("#BC0000"));
                BackgroundBrush = new SolidColorBrush(Color.FromArgb(40, 188, 0, 0));
                ButtonText = CoreTools.Translate("Close");
                break;

            case OperationStatus.Canceled:
                ProgressIndeterminate = false;
                ProgressValue = 100;
                ProgressBrush = new SolidColorBrush(Color.Parse("#9D5D00"));
                BackgroundBrush = Brushes.Transparent;
                ButtonText = CoreTools.Translate("Close");
                break;
        }

        RebuildMenu(status);
    }

    // ── "…" menu ─────────────────────────────────────────────────────────────
    private void RebuildMenu(OperationStatus status)
    {
        if (_menuState == status) return;
        _menuState = status;

        OpMenu.Items.Clear();

        // ── Operation-specific items (package details, install options, etc.) ──
        if (Operation is PackageOperation packageOp)
        {
            bool notVirtual = !packageOp.Package.Source.IsVirtualManager;

            OpMenu.Items.Add(Item("Package details", "info_round.svg",
                notVirtual, () => ShowPackageDetails(packageOp)));

            OpMenu.Items.Add(Item("Installation options", "options.svg",
                notVirtual, () => _ = ShowInstallOptionsAsync(packageOp)));

            string? location = packageOp.Package.Manager.DetailsHelper.GetInstallLocation(packageOp.Package);
            OpMenu.Items.Add(Item("Open install location", "open_folder.svg",
                location is not null && Directory.Exists(location),
                () => CoreTools.Launch(location)));

            OpMenu.Items.Add(new Separator());
        }
        else if (Operation is DownloadOperation downloadOp)
        {
            bool succeeded = status is OperationStatus.Succeeded;

            OpMenu.Items.Add(Item("Open", "launch.svg",
                succeeded, () => CoreTools.Launch(downloadOp.DownloadLocation)));

            OpMenu.Items.Add(Item("Show in explorer", "open_folder.svg",
                succeeded, () => _ = CoreTools.ShowFileOnExplorer(downloadOp.DownloadLocation)));

            OpMenu.Items.Add(new Separator());
        }

        // ── Queue management ──────────────────────────────────────────────────
        if (status is OperationStatus.InQueue)
        {
            OpMenu.Items.Add(Item("Run now", "forward.svg", true, Operation.SkipQueue));
            OpMenu.Items.Add(Item("Run next", "forward.svg", true, Operation.RunNext));
            OpMenu.Items.Add(Item("Run last", "backward.svg", true, Operation.BackOfTheQueue));
            OpMenu.Items.Add(new Separator());
        }

        // ── Cancel / Retry ────────────────────────────────────────────────────
        if (status is OperationStatus.InQueue or OperationStatus.Running)
        {
            OpMenu.Items.Add(Item("Cancel", "cross.svg", true, Operation.Cancel));
        }
        else
        {
            OpMenu.Items.Add(Item("Retry", "reload.svg", true,
                () => Operation.Retry(AbstractOperation.RetryMode.Retry)));

            if (Operation is PackageOperation pkgOp)
            {
                var caps = pkgOp.Package.Manager.Capabilities;

                if (OperatingSystem.IsWindows() && !pkgOp.Options.RunAsAdministrator && caps.CanRunAsAdmin)
                    OpMenu.Items.Add(Item("Retry as administrator", "uac.svg", true,
                        () => Operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin)));

                if (!pkgOp.Options.InteractiveInstallation && caps.CanRunInteractively)
                    OpMenu.Items.Add(Item("Retry interactively", "interactive.svg", true,
                        () => Operation.Retry(AbstractOperation.RetryMode.Retry_Interactive)));

                if (!pkgOp.Options.SkipHashCheck && caps.CanSkipIntegrityChecks)
                    OpMenu.Items.Add(Item("Retry skipping integrity checks", "checksum.svg", true,
                        () => Operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity)));
            }
            else if (OperatingSystem.IsWindows() && Operation is SourceOperation srcOp && !srcOp.ForceAsAdministrator)
            {
                OpMenu.Items.Add(Item("Retry as administrator", "uac.svg", true,
                    () => Operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin)));
            }
        }
    }

    /// <summary>Creates a MenuItem with an SVG icon, translated header, and a SyncCommand.</summary>
    private static MenuItem Item(string translationKey, string svgName, bool enabled, Action action) =>
        new()
        {
            Header = CoreTools.Translate(translationKey),
            IsEnabled = enabled,
            Command = new SyncCommand(action),
            Icon = new SvgIcon
            {
                Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{svgName}",
                Width = 16,
                Height = 16,
                Foreground = Brushes.White,
            },
        };

    // ── Package details / install options ────────────────────────────────────
    private static void ShowPackageDetails(PackageOperation packageOp)
    {
        if (GetMainWindow() is not { } mainWindow) return;
        var win = new PackageDetailsWindow(packageOp.Package, OperationType.None);
        _ = win.ShowDialog(mainWindow);
    }

    private static async Task ShowInstallOptionsAsync(PackageOperation packageOp)
    {
        if (GetMainWindow() is not { } mainWindow) return;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(packageOp.Package);
        var win = new InstallOptionsWindow(packageOp.Package, OperationType.None, opts);
        await win.ShowDialog(mainWindow);
        await InstallOptionsFactory.SaveForPackageAsync(opts, packageOp.Package);
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: Window mw } ? mw : null;

    // ── Button / details actions ──────────────────────────────────────────────
    private void ButtonClick()
    {
        if (Operation.Status is OperationStatus.Running or OperationStatus.InQueue)
            Operation.Cancel();
        else
            AvaloniaOperationRegistry.Remove(this);
    }

    private void ShowDetails()
    {
        if (GetMainWindow() is not { } mainWindow) return;
        var win = new OperationOutputWindow(Operation);
        _ = win.ShowDialog(mainWindow);
    }

    // ── Minimal ICommand implementation ───────────────────────────────────────
    private sealed class SyncCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
