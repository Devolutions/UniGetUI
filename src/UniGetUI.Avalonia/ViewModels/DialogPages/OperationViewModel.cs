using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels;

public sealed class OperationViewModel : INotifyPropertyChanged
{
    public AbstractOperation Operation { get; }

    /// <summary>Short badge labels shown next to the progress bar (Admin, Interactive, …).</summary>
    public ObservableCollection<string> Badges { get; } = [];

    public ICommand ButtonCommand { get; }
    public ICommand ShowDetailsCommand { get; }

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

        // Route all background-thread events to the UI thread
        operation.LogLineAdded += (_, ev) =>
            Dispatcher.UIThread.Post(() => LiveLine = ev.Item1);

        operation.StatusChanged += (_, status) =>
            Dispatcher.UIThread.Post(() => ApplyStatus(status));

        operation.BadgesChanged += (_, badges) =>
            Dispatcher.UIThread.Post(() =>
            {
                Badges.Clear();
                if (badges.AsAdministrator) Badges.Add(CoreTools.Translate("Administrator"));
                if (badges.Interactive) Badges.Add(CoreTools.Translate("Interactive"));
                if (badges.SkipHashCheck) Badges.Add(CoreTools.Translate("Skip hash check"));
            });

        // Sync with current status in case the operation already started
        ApplyStatus(operation.Status);
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
    }

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
        if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime { MainWindow: Window mainWindow })
        {
            var win = new OperationOutputWindow(Operation);
            _ = win.ShowDialog(mainWindow);
        }
    }

    // ── Bindable properties ───────────────────────────────────────────────────
    private string _title;
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private string _liveLine;
    public string LiveLine
    {
        get => _liveLine;
        set { _liveLine = value; OnPropertyChanged(); }
    }

    private string _buttonText;
    public string ButtonText
    {
        get => _buttonText;
        set { _buttonText = value; OnPropertyChanged(); }
    }

    private bool _progressIndeterminate;
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        set { _progressIndeterminate = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private IBrush _progressBrush;
    public IBrush ProgressBrush
    {
        get => _progressBrush;
        set { _progressBrush = value; OnPropertyChanged(); }
    }

    private IBrush _backgroundBrush;
    public IBrush BackgroundBrush
    {
        get => _backgroundBrush;
        set { _backgroundBrush = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Minimal ICommand implementation ───────────────────────────────────────
    private sealed class SyncCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
