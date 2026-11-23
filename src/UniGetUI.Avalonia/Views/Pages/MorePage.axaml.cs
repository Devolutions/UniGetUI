using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class MorePage : UserControl, INavigationDropTarget
{
    private const double DragThreshold = 6d;

    private static readonly PageType[] ToolTilePages = [.. SidebarViewModel.MoreToolPages, PageType.About];

    private readonly Dictionary<PageType, Button> _tiles = [];
    private readonly Dictionary<PageType, (Border Badge, TextBlock Text)> _badges = [];
    private SidebarViewModel? _viewModel;

    private Button? _dragCandidate;
    private Point _dragStart;
    private bool _isDragging;
    private DragGhost? _dragGhost;
    private Point _dragGrabOffset;
    private Point _lastDragPoint;
    private readonly EdgeAutoScroller _autoScroller;
    private PageType[] _orderBeforeDrag = [];
    private INavigationDropTarget? _dropTarget;
    private int? _dropTargetIndex;
    private TopLevel? _dragTopLevel;
    private IPointer? _dragPointer;

    public MorePage()
    {
        InitializeComponent();
        _autoScroller = new EdgeAutoScroller(RefreshDropTarget);
        PageTiles.AddHandler(PointerPressedEvent, PageTiles_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        PageTiles.AddHandler(PointerMovedEvent, PageTiles_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        PageTiles.AddHandler(PointerReleasedEvent, PageTiles_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        PageTiles.PointerCaptureLost += (_, _) => EndDrag(commit: false);

        foreach (PageType page in ToolTilePages)
            ToolTiles.Children.Add(CreateTile(page));
    }

    internal PageType[] PageTileOrder
        => PageTiles.Children.OfType<Button>().Select(tile => (PageType)tile.Tag!).ToArray();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = DataContext as SidebarViewModel;
        _viewModel?.PropertyChanged += ViewModel_PropertyChanged;
        RebuildPageTiles();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SidebarViewModel.MorePages))
            RebuildPageTiles();
        else if (e.PropertyName is { } changed && SidebarViewModel.ExtensionEnabledProperties.Contains(changed))
            UpdateEmptyState();
        else if (e.PropertyName is { } name && SidebarViewModel.BadgeProperties.Contains(name))
            SyncBadges();
    }

    private void RebuildPageTiles()
    {
        EndDrag(commit: false);
        PageTiles.Children.Clear();
        foreach (PageType page in _viewModel?.MorePages ?? [])
            PageTiles.Children.Add(TileFor(page));
        UpdateEmptyState();
        SyncBadges();
    }

    private void UpdateEmptyState()
    {
        bool empty = PageTiles.Children.Count == 0;
        PagesHeader.IsVisible = !empty;
        EmptyPagesCard.IsVisible = empty;
        if (!empty)
            return;

        string[] offExtensions = [.. AppExtensions.All
            .Where(extension => extension.IsAvailable()
                && !(_viewModel?.IsExtensionEnabled(extension.Page) ?? extension.IsEnabled))
            .Select(extension => CoreTools.Translate(extension.Name))];
        BrowseExtensionsButton.IsVisible = offExtensions.Length > 0;
        if (offExtensions.Length > 0)
        {
            EmptyPagesTitle.Text = CoreTools.Translate("Add more pages");
            EmptyPagesText.Text = CoreTools.Translate(
                "Turn on extensions such as {0} to add their pages here. You can also drag a page from the navigation menu onto More, or right-click it, to keep it here.",
                string.Join(", ", offExtensions));
        }
        else
        {
            EmptyPagesTitle.Text = CoreTools.Translate("Keep the navigation menu short");
            EmptyPagesText.Text = CoreTools.Translate(
                "Drag a page from the navigation menu onto More, or right-click it, to keep it here instead.");
        }
    }

    private Button TileFor(PageType page)
    {
        if (!_tiles.TryGetValue(page, out Button? tile))
        {
            tile = _tiles[page] = CreateTile(page);
            var pin = new MenuItem { Header = CoreTools.Translate("Pin to the navigation menu") };
            pin.Click += (_, _) => PinToSidebar(page, null);
            tile.ContextMenu = new ContextMenu { Items = { pin } };
        }
        tile.Classes.Set("dragging", false);
        return tile;
    }

    private Button CreateTile(PageType page)
    {
        string title = Title(page);
        var panel = new Panel();
        panel.Children.Add(BuildTileContent(page));
        if (HasBadge(page))
        {
            var badgeText = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badgeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextOnAccentFillColorPrimaryBrush"));
            var badge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(5, 1),
                MinWidth = 18,
                Height = 18,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = badgeText,
            };
            badge.Bind(Border.BackgroundProperty, this.GetResourceObservable(
                page == PageType.Bundles ? "SystemFillColorCautionBrush" : "AccentFillColorDefaultBrush"));
            if (page == PageType.Bundles)
            {
                badge.MinWidth = 0;
                badge.Width = badge.Height = 10;
                badge.CornerRadius = new CornerRadius(5);
                badge.Margin = new Thickness(0, 4, 4, 0);
            }
            panel.Children.Add(badge);
            _badges[page] = (badge, badgeText);
        }

        var tile = new Button { Tag = page, Content = panel };
        tile.Classes.Add("more-tile");
        AutomationProperties.SetName(tile, title);
        ToolTip.SetTip(tile, title);
        tile.Click += (_, _) => _viewModel?.RequestNavigation(page.ToString());
        return tile;
    }

    private void SyncBadges()
    {
        if (_viewModel is not { } vm)
            return;

        foreach (var (page, (badge, text)) in _badges)
        {
            (bool visible, string label) = vm.BadgeFor(page);
            badge.IsVisible = visible;
            text.Text = label;
        }
    }

    private void PinToSidebar(PageType page, int? index)
    {
        if (_viewModel is not { } vm)
            return;

        vm.PinPageToSidebar(page, index);
        AccessibilityAnnouncementService.Announce(CoreTools.Translate("{0} pinned to the navigation menu", Title(page)));
    }

    private void PageTiles_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragCandidate = null;
        if (!e.GetCurrentPoint(PageTiles).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not { } tile
            || !PageTiles.Children.Contains(tile))
            return;

        _dragCandidate = tile;
        _dragStart = e.GetPosition(PageTiles);
        _dragGrabOffset = e.GetPosition(tile);
    }

    private void PageTiles_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { } tile)
            return;

        if (!_isDragging)
        {
            if (!e.GetCurrentPoint(PageTiles).Properties.IsLeftButtonPressed)
            {
                _dragCandidate = null;
                return;
            }
            Vector moved = e.GetPosition(PageTiles) - _dragStart;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                return;
            if (!BeginDrag(tile))
                return;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(PageTiles);
        }

        UpdateDrag(e);
        e.Handled = true;
    }

    private void PageTiles_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            _dragCandidate = null;
            return;
        }

        UpdateDrag(e);
        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private bool BeginDrag(Button tile)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel
            || DragGhost.Show(this, CreateDragGhost((PageType)tile.Tag!, this, tile.Bounds.Size), _dragGrabOffset) is not { } ghost)
        {
            _dragCandidate = null;
            return false;
        }

        _isDragging = true;
        _orderBeforeDrag = PageTileOrder;
        _dragGhost = ghost;
        tile.Classes.Set("dragging", true);
        _dragTopLevel = topLevel;
        _dragTopLevel.AddHandler(KeyDownEvent, DragTopLevel_KeyDown, RoutingStrategies.Tunnel);
        return true;
    }

    private static Grid BuildTileContent(PageType page)
    {
        var icon = new SvgIcon
        {
            Path = $"avares://UniGetUI/Assets/Symbols/{IconName(page)}.svg",
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var label = new TextBlock
        {
            Text = Title(page),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetRow(label, 1);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(icon);
        grid.Children.Add(label);
        return grid;
    }

    internal static Border CreateDragGhost(PageType page, Control resourceHost, Size size)
    {
        var ghost = new Border
        {
            Width = size.Width,
            Height = size.Height,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Opacity = 0.9,
            IsHitTestVisible = false,
            Child = BuildTileContent(page),
        };
        ghost.Bind(Border.BackgroundProperty, resourceHost.GetResourceObservable("SettingsCardBackground"));
        ghost.Bind(Border.BorderBrushProperty, resourceHost.GetResourceObservable("AccentFillColorDefaultBrush"));
        return ghost;
    }

    internal static readonly Size TileSize = new(168, 104);

    public int? UpdateExternalDrop(Point pointInTopLevel)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel
            || !IsEffectivelyVisible
            || topLevel.TranslatePoint(pointInTopLevel, this) is not { } inPage
            || !new Rect(Bounds.Size).Contains(inPage)
            || topLevel.TranslatePoint(pointInTopLevel, PageTiles) is not { } inPanel)
        {
            EndExternalDrop();
            return null;
        }

        Control[] tiles = [.. PageTiles.Children];
        int index = tiles.Count(tile => inPanel.Y > tile.Bounds.Bottom
            || (inPanel.Y >= tile.Bounds.Top && inPanel.X > tile.Bounds.Center.X));

        Rect anchor = tiles.Length == 0 ? new Rect(new Point(0, 0), TileSize)
            : index < tiles.Length ? tiles[index].Bounds
            : AfterLastTile(tiles[^1]);
        Canvas.SetLeft(PageDropIndicator, anchor.X - 5.5);
        Canvas.SetTop(PageDropIndicator, anchor.Y);
        PageDropIndicator.Height = anchor.Height;
        PageDropIndicator.IsVisible = true;
        PageDropZone.Classes.Set("drop-active", true);
        PageTiles.MinHeight = TileSize.Height;
        return index;
    }

    private Rect AfterLastTile(Control last)
    {
        double nextLeft = last.Bounds.Right + last.Margin.Right;
        return nextLeft + last.Bounds.Width <= PageTiles.Bounds.Width
            ? new Rect(new Point(nextLeft, last.Bounds.Top), last.Bounds.Size)
            : new Rect(new Point(0, last.Bounds.Bottom + last.Margin.Bottom), last.Bounds.Size);
    }

    public ScrollViewer? DropScrollViewer => PageScroller;

    public void EndExternalDrop()
    {
        PageDropIndicator.IsVisible = false;
        PageDropZone.Classes.Set("drop-active", false);
        PageTiles.MinHeight = 0;
    }

    private void UpdateDrag(PointerEventArgs e)
    {
        if (_dragCandidate is not { } tile || _dragGhost is null || _dragTopLevel is null)
            return;

        _dragGhost.Move(e);
        _lastDragPoint = e.GetPosition(_dragTopLevel);
        RefreshDropTarget();
        _autoScroller.Update(_dropTarget?.DropScrollViewer ?? PageScroller, _dragTopLevel, _lastDragPoint);
        if (_dropTargetIndex is not null)
            return;

        Point inPanel = e.GetPosition(PageTiles);
        int from = PageTiles.Children.IndexOf(tile);
        for (int i = 0; i < PageTiles.Children.Count; i++)
        {
            if (i != from && PageTiles.Children[i].Bounds.Contains(inPanel))
            {
                PageTiles.Children.Move(from, i);
                break;
            }
        }
    }

    private void RefreshDropTarget()
    {
        if (!_isDragging || _dragTopLevel is null)
            return;

        INavigationDropTarget? target = NavigationDropTargets.At(_dragTopLevel, _lastDragPoint, this);
        if (!ReferenceEquals(target, _dropTarget))
            _dropTarget?.EndExternalDrop();
        _dropTarget = target;
        _dropTargetIndex = target?.UpdateExternalDrop(_lastDragPoint);
    }

    private void DragTopLevel_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Escape)
            return;

        IPointer? pointer = _dragPointer;
        EndDrag(commit: false);
        pointer?.Capture(null);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (!_isDragging)
        {
            _dragCandidate = null;
            return;
        }

        Button? tile = _dragCandidate;
        INavigationDropTarget? target = _dropTarget;
        int? targetIndex = _dropTargetIndex;
        PageType[] before = _orderBeforeDrag;

        _isDragging = false;
        _dragCandidate = null;
        _dropTarget = null;
        _dropTargetIndex = null;
        _dragPointer = null;
        _dragTopLevel?.RemoveHandler(KeyDownEvent, DragTopLevel_KeyDown);
        _dragTopLevel = null;
        target?.EndExternalDrop();
        _autoScroller.Stop();
        _dragGhost?.Remove();
        _dragGhost = null;
        tile?.Classes.Set("dragging", false);

        if (tile?.Tag is not PageType page)
            return;

        if (commit && target is SidebarView && targetIndex is { } index)
        {
            PinToSidebar(page, index);
            return;
        }

        PageType[] after = PageTileOrder;
        if (commit && !after.SequenceEqual(before))
        {
            _viewModel?.ReorderMorePages(after);
            return;
        }

        if (!after.SequenceEqual(before))
            RebuildPageTiles();
    }

    private static bool HasBadge(PageType page) => page is PageType.Updates or PageType.Bundles
        or PageType.GitRepositories or PageType.Drivers or PageType.Xcode;

    internal static string Title(PageType page) => PageTitles.For(page) ?? page.ToString();

    private static string IconName(PageType page) => page switch
    {
        PageType.Catalog => "catalog",
        PageType.Discover => "DiscoverPackage",
        PageType.Updates => "update",
        PageType.Installed => "InstalledPackages",
        PageType.Bundles => "PackagesBundle",
        PageType.GitRepositories => "git_branch",
        PageType.AgentSkills => "skills",
        PageType.Drivers => "drivers",
        PageType.Xcode => "xcode",
        PageType.OwnLog => "buggy",
        PageType.ManagerLog => "console",
        PageType.OperationHistory => "history",
        PageType.ManagerHealth => "package_managers",
        PageType.ReleaseNotes => "megaphone",
        PageType.Help => "help",
        _ => "info_round",
    };
}
