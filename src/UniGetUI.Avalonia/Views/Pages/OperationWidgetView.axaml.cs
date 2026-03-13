using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Compact card that tracks a single <see cref="AbstractOperation"/> and shows live progress.
/// The DataContext is set to the AbstractOperation by the ItemsControl DataTemplate.
/// </summary>
public partial class OperationWidgetView : UserControl
{
    private AbstractOperation? _operation;

    private static readonly IBrush _runningBrush = Brushes.DodgerBlue;
    private static readonly IBrush _successBrush = Brushes.ForestGreen;
    private static readonly IBrush _failedBrush = Brushes.Crimson;
    private static readonly IBrush _cancelBrush = Brushes.DarkGoldenrod;
    private static readonly IBrush _neutralBrush = Brushes.DimGray;

    public OperationWidgetView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not AbstractOperation op) return;

        if (_operation is not null)
        {
            _operation.LogLineAdded -= OnLogLineAdded;
            _operation.StatusChanged -= OnStatusChanged;
        }

        _operation = op;

        TitleBlock.Text = op.Metadata.Title.Length > 0
            ? op.Metadata.Title
            : CoreTools.Translate("Operation in progress");

        var outputLines = op.GetOutput();
        LiveLineBlock.Text = outputLines.Count > 0
            ? outputLines[^1].Item1
            : CoreTools.Translate("Please wait...");

        ApplyStatus(op.Status);

        op.LogLineAdded += OnLogLineAdded;
        op.StatusChanged += OnStatusChanged;
    }

    private void OnLogLineAdded(object? sender, (string text, AbstractOperation.LineType type) line)
    {
        if (line.type == AbstractOperation.LineType.ProgressIndicator) return;
        Dispatcher.UIThread.Post(() => LiveLineBlock.Text = line.text);
    }

    private void OnStatusChanged(object? sender, OperationStatus status) =>
        Dispatcher.UIThread.Post(() => ApplyStatus(status));

    private void ApplyStatus(OperationStatus status)
    {
        switch (status)
        {
            case OperationStatus.InQueue:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 0;
                SetProgressForeground(_neutralBrush);
                ActionBtn.Content = CoreTools.Translate("Cancel");
                ViewOutputBtn.Content = CoreTools.Translate("Show the live output");
                break;

            case OperationStatus.Running:
                ProgressBar.IsIndeterminate = true;
                SetProgressForeground(_runningBrush);
                ActionBtn.Content = CoreTools.Translate("Cancel");
                ViewOutputBtn.Content = CoreTools.Translate("Show the live output");
                break;

            case OperationStatus.Succeeded:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                SetProgressForeground(_successBrush);
                LiveLineBlock.Text = _operation?.Metadata.SuccessMessage.Length > 0
                    ? _operation.Metadata.SuccessMessage
                    : CoreTools.Translate("Success!");
                ActionBtn.Content = CoreTools.Translate("Close");
                ViewOutputBtn.Content = CoreTools.Translate("Show the live output");
                if (!Settings.Get(Settings.K.MaintainSuccessfulInstalls))
                    ScheduleAutoDismiss();
                break;

            case OperationStatus.Failed:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                SetProgressForeground(_failedBrush);
                LiveLineBlock.Text = _operation?.Metadata.FailureMessage.Length > 0
                    ? _operation.Metadata.FailureMessage
                    : CoreTools.Translate("Failed");
                ActionBtn.Content = CoreTools.Translate("Close");
                ViewOutputBtn.Content = CoreTools.Translate("Retry failed operations");
                break;

            case OperationStatus.Canceled:
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
                SetProgressForeground(_cancelBrush);
                LiveLineBlock.Text = CoreTools.Translate("Operation cancelled");
                ActionBtn.Content = CoreTools.Translate("Close");
                ViewOutputBtn.Content = CoreTools.Translate("Retry failed operations");
                break;
        }
    }

    private void SetProgressForeground(IBrush brush) =>
        ProgressBar.Foreground = brush;

    private void ScheduleAutoDismiss()
    {
        var captured = _operation;
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                if (captured?.Status is OperationStatus.Succeeded)
                    AvaloniaOperationRegistry.Operations.Remove(captured);
            }),
            TaskScheduler.Default);
    }

    private void ActionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_operation is null) return;

        if (_operation.Status is OperationStatus.InQueue or OperationStatus.Running)
            _operation.Cancel();
        else
            AvaloniaOperationRegistry.Operations.Remove(_operation);
    }

    private async void ViewOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_operation is null) return;

        if (_operation.Status is OperationStatus.Failed or OperationStatus.Canceled)
        {
            // Build a retry flyout context menu
            var menu = new ContextMenu();

            var retryItem = new MenuItem { Header = CoreTools.Translate("Retry") };
            retryItem.Click += (_, _) => _operation.Retry(AbstractOperation.RetryMode.Retry);
            menu.Items.Add(retryItem);

            var adminItem = new MenuItem { Header = CoreTools.Translate("Retry as administrator") };
            adminItem.Click += (_, _) => _operation.Retry(AbstractOperation.RetryMode.Retry_AsAdmin);
            menu.Items.Add(adminItem);

            var interactiveItem = new MenuItem { Header = CoreTools.Translate("Interactive operation") };
            interactiveItem.Click += (_, _) => _operation.Retry(AbstractOperation.RetryMode.Retry_Interactive);
            menu.Items.Add(interactiveItem);

            var skipHashItem = new MenuItem { Header = CoreTools.Translate("Retry skipping integrity checks") };
            skipHashItem.Click += (_, _) => _operation.Retry(AbstractOperation.RetryMode.Retry_SkipIntegrity);
            menu.Items.Add(skipHashItem);

            menu.Items.Add(new Separator());

            var viewOutputItem = new MenuItem { Header = CoreTools.Translate("Show the live output") };
            viewOutputItem.Click += async (_, _) =>
            {
                var win = new OperationLogWindow(_operation);
                if (VisualRoot is Window p) await win.ShowDialog(p); else win.Show();
            };
            menu.Items.Add(viewOutputItem);

            if (sender is Control anchor)
            {
                menu.PlacementTarget = anchor;
                menu.Placement = PlacementMode.Bottom;
                menu.Open(anchor);
            }
            return;
        }

        Window window = new OperationLogWindow(_operation);

        if (VisualRoot is Window parent)
            await window.ShowDialog(parent);
        else
            window.Show();
    }

    private void Card_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_operation is null || _operation.Status != OperationStatus.InQueue) return;

        var menu = new ContextMenu();

        var runNowItem = new MenuItem { Header = CoreTools.Translate("Run now") };
        runNowItem.Click += (_, _) => _operation.SkipQueue();
        menu.Items.Add(runNowItem);

        var runNextItem = new MenuItem { Header = CoreTools.Translate("Run next") };
        runNextItem.Click += (_, _) => _operation.RunNext();
        menu.Items.Add(runNextItem);

        var runLastItem = new MenuItem { Header = CoreTools.Translate("Run last") };
        runLastItem.Click += (_, _) => _operation.BackOfTheQueue();
        menu.Items.Add(runLastItem);

        menu.Items.Add(new Separator());

        var cancelItem = new MenuItem { Header = CoreTools.Translate("Cancel") };
        cancelItem.Click += (_, _) => _operation.Cancel();
        menu.Items.Add(cancelItem);

        if (sender is Control ctrl)
        {
            menu.PlacementTarget = ctrl;
            menu.Placement = PlacementMode.Pointer;
            menu.Open(ctrl);
        }
        e.Handled = true;
    }
}
