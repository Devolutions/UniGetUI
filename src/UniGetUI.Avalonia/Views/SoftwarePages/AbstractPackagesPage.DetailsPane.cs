using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Pages;

public abstract partial class AbstractPackagesPage : IDetailsPaneHost
{
    private static readonly string DetailsPaneShortcut = OperatingSystem.IsMacOS() ? "Cmd+Shift+I" : "Ctrl+Shift+D";

    private const double DefaultDetailsPaneWidth = 400;
    private const double MinDetailsPaneWidth = 320;
    private const double MinListWidthBesideDetails = 420;
    private const double DetailsSplitterWidth = 12;
    private static readonly TimeSpan DetailsSelectionDelay = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _detailsSelectionTimer = new() { Interval = DetailsSelectionDelay };
    private double _detailsPaneWidth = DefaultDetailsPaneWidth;
    private bool _isDetailsPaneOpen;
    private bool _isDetailsPaneShown;
    private bool _syncingDetailsToggle;

    protected virtual bool SupportsDetailsPane => false;

    protected virtual PackageDetailsWindow? CreateDetailsPaneView(IPackage package) => null;

    public bool CanToggleDetailsPane => SupportsDetailsPane;

    public void ToggleDetailsPane()
    {
        if (CanToggleDetailsPane)
            SetDetailsPaneOpen(!_isDetailsPaneOpen);
    }

    private static string DetailsToggleTooltip =>
        $"{CoreTools.Translate("Details panel")} ({DetailsPaneShortcut})";

    private void InitializeDetailsPane()
    {
        if (!SupportsDetailsPane) return;

        int savedWidth = Settings.GetDictionaryItem<string, int>(Settings.K.DetailsPaneWidths, ViewModel.PageName);
        if (savedWidth >= MinDetailsPaneWidth) _detailsPaneWidth = savedWidth;
        _isDetailsPaneOpen = Settings.GetDictionaryItem<string, bool>(Settings.K.DetailsPaneOpen, ViewModel.PageName);

        DetailsToggleContainer.IsVisible = true;
        SyncDetailsToggle();
        ToggleDetailsButton.IsCheckedChanged += (_, _) =>
        {
            if (_syncingDetailsToggle) return;
            SetDetailsPaneOpen(ToggleDetailsButton.IsChecked == true);
        };
        CloseDetailsPaneButton.Click += (_, _) => SetDetailsPaneOpen(false);
        ToolTip.SetTip(CloseDetailsPaneButton, $"{CoreTools.Translate("Close")} ({DetailsPaneShortcut})");

        if (PackageList.ContextMenu is { } contextMenu)
        {
            var toggleItem = new MenuItem();
            toggleItem.Click += (_, _) => ToggleDetailsPane();
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(toggleItem);
            contextMenu.Opening += (_, _) =>
                toggleItem.Header = ShortcutHeader(
                    _isDetailsPaneOpen
                        ? CoreTools.Translate("Hide details panel")
                        : CoreTools.Translate("Show details panel"),
                    DetailsPaneShortcut);
        }

        _detailsSelectionTimer.Tick += (_, _) =>
        {
            _detailsSelectionTimer.Stop();
            RefreshDetailsPaneContent();
        };
        PackageList.SelectionChanged += (_, _) => ScheduleDetailsPaneRefresh();
        ViewModel.FilteredPackages.SelectionStateChanged += (_, _) => ScheduleDetailsPaneRefresh();
        foreach (var cards in new Control[] { GridViewItems, IconsViewItems })
        {
            cards.AddHandler(PointerPressedEvent, OnCardInteraction, RoutingStrategies.Tunnel);
            cards.AddHandler(GotFocusEvent, OnCardInteraction);
        }

        DetailsSplitter.DragCompleted += (_, _) =>
        {
            double width = FilteringPanel.ColumnDefinitions[4].ActualWidth;
            if (width < MinDetailsPaneWidth) return;
            _detailsPaneWidth = width;
            Settings.SetDictionaryItem(Settings.K.DetailsPaneWidths, ViewModel.PageName, (int)width);
        };

        UpdateDetailsPaneLayout();
    }

    private void OnCardInteraction(object? sender, RoutedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: PackageWrapper wrapper } && !ReferenceEquals(PackageList.SelectedItem, wrapper))
            PackageList.SelectedItem = wrapper;
    }

    private void ScheduleDetailsPaneRefresh()
    {
        if (!_isDetailsPaneShown) return;
        _detailsSelectionTimer.Stop();
        _detailsSelectionTimer.Start();
    }

    private void SetDetailsPaneOpen(bool open)
    {
        if (_isDetailsPaneOpen != open)
        {
            _isDetailsPaneOpen = open;
            Settings.SetDictionaryItem(Settings.K.DetailsPaneOpen, ViewModel.PageName, open);
        }

        SyncDetailsToggle();
        UpdateDetailsPaneLayout();
    }

    private void SyncDetailsToggle()
    {
        _syncingDetailsToggle = true;
        ToggleDetailsButton.IsChecked = _isDetailsPaneOpen;
        _syncingDetailsToggle = false;
    }

    private double AvailableDetailsPaneWidth()
    {
        double total = FilteringPanel.Bounds.Width;
        if (total <= 0) return 0;

        double filterWidth = !_isOverlayMode && ViewModel.IsFilterPaneOpen ? _savedFilterPaneWidth + 12 : 0;
        return total - filterWidth - DetailsSplitterWidth - MinListWidthBesideDetails;
    }

    private void UpdateDetailsPaneLayout()
    {
        if (!SupportsDetailsPane || FilteringPanel.ColumnDefinitions.Count < 5) return;

        double available = AvailableDetailsPaneWidth();
        bool fits = available >= MinDetailsPaneWidth;
        bool show = _isDetailsPaneOpen && fits;

        ToolTip.SetTip(ToggleDetailsButton, _isDetailsPaneOpen && !fits
            ? CoreTools.Translate("Make the window wider to show package details here")
            : DetailsToggleTooltip);

        var listColumn = FilteringPanel.ColumnDefinitions[2];
        var splitterColumn = FilteringPanel.ColumnDefinitions[3];
        var paneColumn = FilteringPanel.ColumnDefinitions[4];
        if (show)
        {
            listColumn.MinWidth = MinListWidthBesideDetails;
            paneColumn.MinWidth = MinDetailsPaneWidth;
            paneColumn.Width = new GridLength(Math.Min(_detailsPaneWidth, available));
            splitterColumn.Width = new GridLength(DetailsSplitterWidth);
        }
        else
        {
            listColumn.MinWidth = 0;
            paneColumn.MinWidth = 0;
            paneColumn.Width = new GridLength(0);
            splitterColumn.Width = new GridLength(0);
        }

        DetailsSplitter.IsVisible = show;
        DetailsPane.IsVisible = show;

        if (show == _isDetailsPaneShown) return;
        _isDetailsPaneShown = show;
        if (show)
            RefreshDetailsPaneContent();
        else
            ClearDetailsPaneContent();
    }

    private void RefreshDetailsPaneContent()
    {
        if (!_isDetailsPaneShown) return;

        var package = SelectedItem;
        if (package is null)
        {
            ClearDetailsPaneContent();
            return;
        }

        if (DetailsPaneHost.Content is PackageDetailsWindow current && ReferenceEquals(current.Package, package))
            return;

        var view = CreateDetailsPaneView(package);
        if (view is null)
        {
            ClearDetailsPaneContent();
            return;
        }

        view.MainActionRequested += (_, _) => PerformMainPackageAction(view.Package);
        ReleaseDetailsPaneView();
        DetailsPaneHost.Content = view;
        DetailsPanePlaceholder.IsVisible = false;
    }

    private void ClearDetailsPaneContent()
    {
        _detailsSelectionTimer.Stop();
        ReleaseDetailsPaneView();
        DetailsPaneHost.Content = null;
        DetailsPanePlaceholder.IsVisible = true;
    }

    private void ReleaseDetailsPaneView()
    {
        if (DetailsPaneHost.Content is PackageDetailsWindow previous)
            previous.CancelLoading();
    }
}
