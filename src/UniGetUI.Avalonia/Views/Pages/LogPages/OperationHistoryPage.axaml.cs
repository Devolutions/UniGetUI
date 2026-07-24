using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.LogPages;

public partial class OperationHistoryPage : UserControl, IEnterLeaveListener, IKeyboardShortcutListener, ISearchBoxPage
{
    private readonly OperationHistoryListViewModel _viewModel;

    public OperationHistoryPage()
    {
        _viewModel = new OperationHistoryListViewModel();
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.ClearRequested += OnClearRequested;

        // Header-click sorting. Like the package list, a strongly-typed CustomSortComparer keeps
        // Avalonia's built-in sort NativeAOT-safe (the default SortMemberPath path is reflection-based).
        foreach (var col in HistoryList.Columns)
        {
            if (col.Tag is not string key) continue;
            col.CanUserSort = true;
            col.CustomSortComparer = new OperationHistoryRowComparer(key);
        }

        // Right-click a row → the same actions as the inline buttons.
        HistoryList.ContextRequested += OnRowContextRequested;
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        if (source.FindAncestorOfType<DataGridRow>()?.DataContext is not OperationHistoryRowViewModel row) return;

        var menu = new ContextMenu();
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("Revert this operation"), "undelete.svg", row.RevertCommand, row.CanRevert));
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("Run this operation again"), "reload.svg", row.ReRunCommand, row.CanReRun));
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("View full log"), "clipboard_list.svg", row.ViewLogCommand, true));

        // Retry variants — only for failed operations where the mode is applicable.
        if (row.CanRetryAsAdmin || row.CanRetryInteractive || row.CanRetrySkipHash)
        {
            menu.Items.Add(new Separator());
            if (row.CanRetryAsAdmin)
                menu.Items.Add(BuildMenuItem(
                    CoreTools.Translate("Retry as administrator"), "reload.svg", row.RetryAsAdminCommand, true));
            if (row.CanRetryInteractive)
                menu.Items.Add(BuildMenuItem(
                    CoreTools.Translate("Retry interactively"), "reload.svg", row.RetryInteractiveCommand, true));
            if (row.CanRetrySkipHash)
                menu.Items.Add(BuildMenuItem(
                    CoreTools.Translate("Retry skipping integrity checks"), "reload.svg", row.RetrySkipHashCommand, true));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("Remove from history"), "cross.svg", row.RemoveCommand, true));

        menu.Placement = PlacementMode.Pointer;
        menu.Open(source);
        e.Handled = true;
    }

    private static MenuItem BuildMenuItem(string header, string svg, ICommand command, bool enabled) => new()
    {
        Header = header,
        Command = command,
        IsEnabled = enabled,
        Icon = new SvgIcon
        {
            Path = "avares://UniGetUI/Assets/Symbols/" + svg,
            Width = 16,
            Height = 16,
        },
    };

    // ─── ISearchBoxPage: the shell's global search box filters the history live ───
    public string QueryBackup { get; set; } = "";

    public string SearchBoxPlaceholder => CoreTools.Translate("Search operation history");

    public void SearchBox_QuerySubmitted(object? sender, EventArgs? e) { /* filtering is live */ }

    /// <summary>Called by the shell as the query text changes.</summary>
    public void ApplyQuery(string query) => _viewModel.SetFilter(query);

    private async void OnClearRequested(object? sender, EventArgs e)
    {
        if (await ConfirmationDialog.ShowAsync(
                CoreTools.Translate("This will permanently remove all operation history. Continue?")))
        {
            _viewModel.ClearConfirmed();
        }
    }

    public void OnEnter()
    {
        _viewModel.Load();
        LogView.OnEnter();
    }

    public void OnLeave() => LogView.OnLeave();

    public void ReloadTriggered()
    {
        _viewModel.Load();
        LogView.ReloadTriggered();
    }

    public void SelectAllTriggered() => LogView.SelectAllTriggered();

    public void SearchTriggered() { }

    public void DetailsTriggered() { }
}
