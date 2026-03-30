using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.LogPages;

public partial class BaseLogPage : UserControl, IEnterLeaveListener, IKeyboardShortcutListener
{
    protected readonly BaseLogPageViewModel ViewModel;

    protected BaseLogPage(BaseLogPageViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();

        ViewModel.CopyTextRequested += OnCopyTextRequested;
        ViewModel.ExportTextRequested += OnExportTextRequested;
        ViewModel.ScrollToBottomRequested += OnScrollToBottomRequested;
    }

    private async void OnCopyTextRequested(object? sender, string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private async void OnExportTextRequested(object? sender, string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CoreTools.Translate("Export log"),
            SuggestedFileName = CoreTools.Translate("UniGetUI Log"),
            FileTypeChoices =
            [
                new FilePickerFileType(CoreTools.Translate("Text")) { Patterns = ["*.txt"] },
            ],
        });

        if (file is not null)
            await File.WriteAllTextAsync(file.Path.LocalPath, text);
    }

    private void OnScrollToBottomRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            MainScroller.Offset = new Vector(MainScroller.Offset.X, double.MaxValue);
        }, DispatcherPriority.Background);
    }

    public void OnEnter() => ViewModel.LoadLog();

    public void OnLeave() => ViewModel.ClearLog();

    public void ReloadTriggered() => ViewModel.LoadLog(isReload: true);

    public void SelectAllTriggered() { }

    public void SearchTriggered() { }
}
