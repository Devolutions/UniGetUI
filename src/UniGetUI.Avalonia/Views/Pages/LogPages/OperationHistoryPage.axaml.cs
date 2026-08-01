using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

        // Ease wheel scrolling to a stop (WinUI-like), matching the package list.
        DataGridWheelAnimator.Attach(HistoryList);

        // Right-click a row → the same actions as the inline buttons.
        HistoryList.ContextRequested += OnRowContextRequested;
        // Double-click → open the log; Enter/Delete keyboard shortcuts on the list.
        // Handle on the tunnel (preview) phase so Enter opens the log instead of the DataGrid's
        // built-in "move to next row" navigation, which otherwise consumes the key first.
        HistoryList.DoubleTapped += (_, e) =>
        {
            // Inline actions own their pointer gestures; a double-click on one must not
            // bubble into the row's "open log" shortcut as a second, unrelated action.
            if (e.Source is Visual source
                && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            ViewLog(SelectedRow);
        };
        HistoryList.AddHandler(InputElement.KeyDownEvent, OnHistoryKeyDown, RoutingStrategies.Tunnel);
    }

    private OperationHistoryRowViewModel? SelectedRow => HistoryList.SelectedItem as OperationHistoryRowViewModel;

    private static void ViewLog(OperationHistoryRowViewModel? row)
    {
        if (row?.HasOutput is true) row.ViewLogCommand.Execute(null);
    }

    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        if (e.Key == Key.Enter)
        {
            ViewLog(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            row.RemoveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control source) return;
        if (source.FindAncestorOfType<DataGridRow>()?.DataContext is not OperationHistoryRowViewModel row) return;

        var menu = new ContextMenu();
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("Revert this operation"), "undo.svg", row.RevertCommand, row.CanRevert));
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("Run this operation again"), "reload.svg", row.ReRunCommand, row.CanReRun));
        menu.Items.Add(BuildMenuItem(
            CoreTools.Translate("View full log"), "clipboard_list.svg", row.ViewLogCommand, row.HasOutput));
        menu.Items.Add(BuildCopyDetailsItem(row));

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

    private void FilterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string facet } button) return;

        (IEnumerable<HistoryFilterOption> options, HistoryFilterOption? selected) = facet switch
        {
            "status" => (_viewModel.StatusOptions, _viewModel.SelectedStatus),
            "kind" => (_viewModel.KindOptions, _viewModel.SelectedKind),
            "manager" => (_viewModel.ManagerOptions, _viewModel.SelectedManager),
            _ => ([], null),
        };

        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (HistoryFilterOption option in options)
        {
            var item = new MenuItem
            {
                Header = option.Label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = ReferenceEquals(option, selected),
            };
            item.Click += (_, _) => SetHistoryFilter(facet, option);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void SetHistoryFilter(string facet, HistoryFilterOption option)
    {
        switch (facet)
        {
            case "status":
                _viewModel.SelectedStatus = option;
                break;
            case "kind":
                _viewModel.SelectedKind = option;
                break;
            case "manager":
                _viewModel.SelectedManager = option;
                break;
        }
    }

    // Copy details needs clipboard access (a top-level concern), so it uses a click handler rather than a command.
    private MenuItem BuildCopyDetailsItem(OperationHistoryRowViewModel row)
    {
        var item = new MenuItem
        {
            Header = CoreTools.Translate("Copy details"),
            Icon = new SvgIcon { Path = "avares://UniGetUI/Assets/Symbols/copy.svg", Width = 16, Height = 16 },
        };
        item.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(row.DetailsSummary);
        };
        return item;
    }

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
