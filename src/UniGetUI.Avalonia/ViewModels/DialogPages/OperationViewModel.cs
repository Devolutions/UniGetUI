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
using UniGetUI.Interface.Telemetry;
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
    [ObservableProperty] private IImage? _packageIcon;

    private OperationStatus _visualStatus;
    public bool IsInQueue => _visualStatus is OperationStatus.InQueue;
    public bool IsRunning => _visualStatus is OperationStatus.Running;
    public bool IsSucceeded => _visualStatus is OperationStatus.Succeeded;
    public bool IsFailed => _visualStatus is OperationStatus.Failed;
    public bool IsCanceled => _visualStatus is OperationStatus.Canceled;

    private static readonly Uri _fallbackIconUri =
        new("avares://UniGetUI/Assets/package_color.png");

    // Progress display state machine; the only UI-thread-owned copy.
    // All event handlers below run on the UI thread via Dispatcher.UIThread.Post,
    // which is FIFO at the same priority. That ordering is load-bearing on the
    // failure path: Status=Failed clears determinate ownership before the failure
    // line arrives, so the failure message is never swallowed.
    private readonly OperationCardController _controller = new();

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
        _visualStatus = operation.Status;

        _ = LoadIconAsync();

        // Route all background-thread events to the UI thread
        operation.LogLineAdded += (_, ev) =>
            Dispatcher.UIThread.Post(() =>
            {
                // Structured determinate progress owns the status line: raw per-frame
                // progress text must not clobber it. (History already excludes
                // ProgressIndicator lines, so this changes display only.)
                if (_controller.TryApplyLogLine(ev.Item1, ev.Item2, out string liveLine))
                {
                    LiveLine = liveLine;
                }
            });

        operation.ProgressChanged += (_, progress) =>
            Dispatcher.UIThread.Post(() =>
            {
                var (isIndeterminate, value, liveLine) = _controller.ApplyProgress(
                    Operation.Status,
                    progress
                );
                ProgressIndeterminate = isIndeterminate;
                ProgressValue = value;
                LiveLine = liveLine;
            });

        operation.StatusChanged += (_, status) =>
            Dispatcher.UIThread.Post(() => ApplyStatus(status));

        operation.BadgesChanged += (_, badges) =>
            Dispatcher.UIThread.Post(() =>
            {
                Badges.Clear();
                if (badges.AsAdministrator)
                    Badges.Add(new(
                        CoreTools.Translate("Administrator privileges"),
                        "avares://UniGetUI/Assets/Symbols/uac.svg",
                        CoreTools.Translate("This operation is running with administrator privileges."),
                        ""
                    ));
                if (badges.Interactive)
                    Badges.Add(new(
                        CoreTools.Translate("Interactive operation"),
                        "avares://UniGetUI/Assets/Symbols/interactive.svg",
                        CoreTools.Translate("This operation is running interactively."),
                        CoreTools.Translate("You will likely need to interact with the installer.")
                    ));
                if (badges.SkipHashCheck)
                    Badges.Add(new(
                        CoreTools.Translate("Integrity checks skipped"),
                        "avares://UniGetUI/Assets/Symbols/checksum.svg",
                        CoreTools.Translate("Integrity checks will not be performed during this operation."),
                        CoreTools.Translate("Proceed at your own risk.")
                    ));
            });

        // Sync with current status in case the operation already started. The
        // controller derives card, last log line, and determinate ownership from the
        // same snapshot so a mid-download card starts in the formatted state.
        // Note: SyncInitial already applies the status to the controller, so only
        // the brush/menu visuals still need syncing here (a second ApplyStatus would
        // flip a determinate Running card back to indeterminate).
        _controller.SyncInitial(_liveLine, operation.Status, operation.CurrentProgress);
        ProgressIndeterminate = _controller.Card.IsIndeterminate;
        ProgressValue = _controller.Card.Value;
        LiveLine = _controller.Card.LiveLine;
        ApplyStatusVisuals(operation.Status);
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
        // Determinate ownership ends with the running phase; afterwards log lines
        // (e.g. the success/failure message) own the status line again.
        _controller.ApplyStatus(status);
        ProgressIndeterminate = _controller.Card.IsIndeterminate;
        ProgressValue = _controller.Card.Value;
        ApplyStatusVisuals(status);
    }

    private void ApplyStatusVisuals(OperationStatus status)
    {
        _visualStatus = status;
        OnPropertyChanged(nameof(IsInQueue));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCanceled));

        switch (status)
        {
            case OperationStatus.InQueue:
                ButtonText = CoreTools.Translate("Cancel");
                break;

            case OperationStatus.Running:
                ButtonText = CoreTools.Translate("Cancel");
                break;

            case OperationStatus.Succeeded:
                ButtonText = CoreTools.Translate("Close");
                break;

            case OperationStatus.Failed:
                ButtonText = CoreTools.Translate("Close");
                break;

            case OperationStatus.Canceled:
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

                if (PackageOperation.CanRetrySkippingIntegrityChecks(
                        pkgOp.Package.Manager, pkgOp.Options, pkgOp.Role, pkgOp.WillRunElevated))
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
                Path = $"avares://UniGetUI/Assets/Symbols/{svgName}",
                Width = 16,
                Height = 16,
            },
        };

    // ── Package details / install options ────────────────────────────────────
    private static void ShowPackageDetails(PackageOperation packageOp)
    {
        if (GetMainWindow() is not { } mainWindow) return;
        var referral = packageOp.Role is OperationType.Update or OperationType.Uninstall
            ? TEL_InstallReferral.ALREADY_INSTALLED
            : TEL_InstallReferral.DIRECT_SEARCH;
        var win = new PackageDetailsWindow(packageOp.Package, OperationType.None, referral);
        _ = win.ShowDialog(mainWindow);
    }

    private static async Task ShowInstallOptionsAsync(PackageOperation packageOp)
    {
        if (GetMainWindow() is not { } mainWindow) return;
        var opts = await InstallOptionsFactory.LoadForPackageAsync(packageOp.Package);
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
        if (Operation.Status is OperationStatus.Failed)
        {
            var win = new OperationFailedDialog(Operation);
            win.Show(mainWindow);
        }
        else
        {
            var win = new OperationOutputWindow(Operation);
            win.Show(mainWindow);
        }
    }

    // ── Minimal ICommand implementation ───────────────────────────────────────
    private sealed class SyncCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
