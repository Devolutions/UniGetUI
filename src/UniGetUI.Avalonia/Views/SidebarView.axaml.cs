using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

public partial class SidebarView : BaseView<SidebarViewModel>
{
    private bool _lastNavItemSelectionWasAuto;
    private bool _isMoreFlyoutOpen;
    private CancellationTokenSource? _pillAnimationCancellation;
    private int _pillAnimationVersion;

    private readonly TranslateTransform _pillTopCapTranslate = new();
    private readonly TransformGroup _pillStemTransform = new();
    private readonly ScaleTransform _pillStemScale = new() { ScaleX = 1d, ScaleY = 1d };
    private readonly TranslateTransform _pillStemTranslate = new();
    private readonly TranslateTransform _pillBottomCapTranslate = new();
    private double _pillTop;
    private double _pillBottom = PillHeight;
    private ListBoxItem? _pendingPillItem;
    private bool _pendingPillAnimate;

    private ListBoxItem? _dragCandidate;
    private Point _dragStart;
    private bool _isDragging;
    private ListBoxItem[] _dragItems = [];
    private double[] _dragTops = [];
    private int _dragFromIndex;
    private int _dragTargetIndex;
    private bool _dragHadFocus;
    private TopLevel? _dragTopLevel;
    private IPointer? _dragPointer;
    private SidebarViewModel? _subscribedViewModel;
    private PageType? _pendingPointerNavigation;

    private const double DragThreshold = 6d;
    private const double PillHeight = 16d;
    private static readonly TimeSpan PillAnimationDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Whether the nav item text labels are shown. False renders an icon-only rail; true renders the
    /// full labeled pane. Decoupled from the view-model's pane state so the same view can be used both
    /// as the always-visible rail and as the sliding flyout simultaneously.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<SidebarView, bool>(nameof(ShowLabels), defaultValue: true);

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public SidebarView()
    {
        InitializeComponent();
        PlatformShortcuts.AdaptAcceleratorKeys(this);

        NavigationPillTopCap.RenderTransform = _pillTopCapTranslate;
        _pillStemTransform.Children.Add(_pillStemScale);
        _pillStemTransform.Children.Add(_pillStemTranslate);
        NavigationPillStem.RenderTransform = _pillStemTransform;
        NavigationPillBottomCap.RenderTransform = _pillBottomCapTranslate;

        SidebarLayout.SizeChanged += (_, _) => QueueSelectionPillUpdate(animate: false);

        MoreNavBtn.AddHandler(KeyDownEvent, MoreNavBtn_KeyDown, RoutingStrategies.Tunnel);

        NavListBox.AddHandler(PointerPressedEvent, NavListBox_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        NavListBox.AddHandler(PointerMovedEvent, NavListBox_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        NavListBox.AddHandler(PointerReleasedEvent, NavListBox_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        NavListBox.AddHandler(KeyDownEvent, NavListBox_KeyDown, RoutingStrategies.Tunnel);
        NavListBox.PointerCaptureLost += (_, _) => EndDrag(commit: false);

        if (FlyoutBase.GetAttachedFlyout(MoreNavBtn) is { } moreFlyout)
        {
            moreFlyout.Opened += (_, _) => _isMoreFlyoutOpen = true;
            moreFlyout.Closed += (_, _) =>
            {
                _isMoreFlyoutOpen = false;
                SyncListBoxSelection(ViewModel?.SelectedPageType ?? PageType.Null);
            };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateViewModelSubscription(TopLevel.GetTopLevel(this) is not null);
        if (DataContext is SidebarViewModel vm)
        {
            ApplyNavigationOrder(vm.NavigationPages);
            // The startup page may already be set before this view subscribes, so apply it now.
            SyncListBoxSelection(vm.SelectedPageType);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateViewModelSubscription(attached: true);
        if (_subscribedViewModel is { } vm)
        {
            ApplyNavigationOrder(vm.NavigationPages);
            SyncListBoxSelection(vm.SelectedPageType);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndDrag(commit: false);
        UpdateViewModelSubscription(attached: false);
    }

    private void UpdateViewModelSubscription(bool attached)
    {
        SidebarViewModel? vm = attached ? DataContext as SidebarViewModel : null;
        if (ReferenceEquals(vm, _subscribedViewModel))
            return;

        if (_subscribedViewModel is { } previous)
            previous.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribedViewModel = vm;
        vm?.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not SidebarViewModel vm)
            return;

        if (args.PropertyName == nameof(SidebarViewModel.SelectedPageType))
            SyncListBoxSelection(vm.SelectedPageType);
        else if (args.PropertyName == nameof(SidebarViewModel.NavigationPages))
        {
            ApplyNavigationOrder(vm.NavigationPages);
            SyncListBoxSelection(vm.SelectedPageType);
        }
    }

    private void ApplyNavigationOrder(PageType[] pages)
    {
        ResetDragVisuals();
        _lastNavItemSelectionWasAuto = true;
        NavListBox.Items.Clear();
        foreach (var page in pages)
        {
            if (NavButtonFor(page) is { } button)
                NavListBox.Items.Add(button);
        }
        _lastNavItemSelectionWasAuto = false;
        AssignShortcutKeys(MainWindowViewModel.GetShortcutPages(pages));
    }

    private void NavListBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragCandidate = null;
        _pendingPointerNavigation = null;
        if (!e.GetCurrentPoint(NavListBox).Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item
            || !NavListBox.Items.Contains(item))
            return;

        _dragCandidate = item;
        _dragStart = e.GetPosition(DragSurface);
    }

    private Visual DragSurface => NavListBox.ItemsPanelRoot ?? (Visual)NavListBox;

    private void NavListBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is null)
            return;

        double offset = e.GetPosition(DragSurface).Y - _dragStart.Y;
        if (!_isDragging)
        {
            if (!e.GetCurrentPoint(NavListBox).Properties.IsLeftButtonPressed)
            {
                AbandonPointerPress();
                return;
            }
            if (Math.Abs(offset) < DragThreshold || !BeginDrag(_dragCandidate))
                return;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(NavListBox);
        }

        UpdateDrag(offset);
        e.Handled = true;
    }

    private void NavListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging)
        {
            _dragCandidate = null;
            if (_pendingPointerNavigation is { } page)
            {
                _pendingPointerNavigation = null;
                ViewModel?.RequestNavigation(page.ToString());
            }
            return;
        }

        UpdateDrag(e.GetPosition(DragSurface).Y - _dragStart.Y);
        EndDrag(commit: true);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private bool BeginDrag(ListBoxItem item)
    {
        ListBoxItem[] items = NavListBox.Items.OfType<ListBoxItem>().ToArray();
        int from = Array.IndexOf(items, item);
        double[] tops = new double[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].TranslatePoint(default, DragSurface) is not { } position)
            {
                _dragCandidate = null;
                return false;
            }
            tops[i] = position.Y;
        }
        if (from < 0 || items.Length < 2)
        {
            _dragCandidate = null;
            return false;
        }

        _dragItems = items;
        _dragTops = tops;
        _dragFromIndex = from;
        for (int i = 0; i < _dragItems.Length; i++)
        {
            var transform = new TranslateTransform();
            if (i != _dragFromIndex && !MotionPreference.ReducedMotion)
            {
                transform.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = TranslateTransform.YProperty,
                        Duration = TimeSpan.FromMilliseconds(150),
                        Easing = new CubicEaseOut(),
                    },
                ];
            }
            _dragItems[i].RenderTransform = transform;
        }

        _isDragging = true;
        _dragTargetIndex = _dragFromIndex;
        DiscardPendingPointerNavigation();
        _dragHadFocus = item.IsKeyboardFocusWithin;
        _dragTopLevel = TopLevel.GetTopLevel(this);
        _dragTopLevel?.AddHandler(KeyDownEvent, DragTopLevel_KeyDown, RoutingStrategies.Tunnel);
        item.ZIndex = 1;
        item.Opacity = 0.85;
        _pillAnimationCancellation?.Cancel();
        NavigationSelectionPill.IsVisible = false;
        return true;
    }

    private void UpdateDrag(double offset)
    {
        ListBoxItem dragged = _dragItems[_dragFromIndex];
        double fromTop = _dragTops[_dragFromIndex];
        offset = Math.Clamp(offset, _dragTops[0] - fromTop, _dragTops[^1] - fromTop);
        ((TranslateTransform)dragged.RenderTransform!).Y = offset;

        double draggedCenter = fromTop + offset + (dragged.Bounds.Height / 2d);
        int target = 0;
        for (int i = 0; i < _dragItems.Length; i++)
        {
            double center = _dragTops[i] + (_dragItems[i].Bounds.Height / 2d);
            if ((i < _dragFromIndex && center < draggedCenter) || (i > _dragFromIndex && center <= draggedCenter))
                target++;
        }
        _dragTargetIndex = target;

        double pitch = dragged.Bounds.Height + dragged.Margin.Top + dragged.Margin.Bottom;
        for (int i = 0; i < _dragItems.Length; i++)
        {
            if (i == _dragFromIndex)
                continue;
            double shift = 0d;
            if (_dragFromIndex < target && i > _dragFromIndex && i <= target)
                shift = -pitch;
            else if (target < _dragFromIndex && i >= target && i < _dragFromIndex)
                shift = pitch;
            ((TranslateTransform)_dragItems[i].RenderTransform!).Y = shift;
        }
    }

    private void AbandonPointerPress()
    {
        _dragCandidate = null;
        DiscardPendingPointerNavigation();
    }

    private void DiscardPendingPointerNavigation()
    {
        if (_pendingPointerNavigation is null)
            return;

        _pendingPointerNavigation = null;
        SyncListBoxSelection(ViewModel?.SelectedPageType ?? PageType.Null);
    }

    private void EndDrag(bool commit)
    {
        if (!_isDragging)
        {
            AbandonPointerPress();
            return;
        }
        _dragCandidate = null;
        _isDragging = false;
        _dragTopLevel?.RemoveHandler(KeyDownEvent, DragTopLevel_KeyDown);
        _dragTopLevel = null;
        _dragPointer = null;

        int from = _dragFromIndex;
        int target = _dragTargetIndex;
        ListBoxItem dragged = _dragItems[from];

        ResetDragVisuals();
        if (!commit || target == from || !MoveNavItem(dragged, target - from, _dragHadFocus ? NavigationMethod.Pointer : null))
            QueueSelectionPillUpdate(animate: false);
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

    private void NavListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) || _isDragging
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item
            || !NavListBox.Items.Contains(item))
            return;

        MoveNavItem(item, e.Key is Key.Up ? -1 : 1, NavigationMethod.Directional);
        e.Handled = true;
    }

    private bool MoveNavItem(ListBoxItem item, int offset, NavigationMethod? refocus)
    {
        if (ViewModel is not { } vm)
            return false;

        PageType[] pages = vm.NavigationPages;
        int from = Array.FindIndex(pages, page => NavButtonFor(page) == item);
        int target = Math.Clamp(from + offset, 0, pages.Length - 1);
        if (from < 0 || target == from)
            return false;

        List<PageType> reordered = [.. pages];
        reordered.RemoveAt(from);
        reordered.Insert(target, pages[from]);
        vm.ReorderNavigationPages(reordered);

        if (refocus is { } method)
            item.Focus(method);
        AccessibilityAnnouncementService.Announce(CoreTools.Translate(
            "{0} moved to position {1} of {2}",
            AutomationProperties.GetName(item) ?? pages[from].ToString(),
            target + 1,
            pages.Length));
        return true;
    }

    private void ResetDragVisuals()
    {
        foreach (var item in _dragItems)
        {
            item.RenderTransform = null;
            item.ZIndex = 0;
            item.Opacity = 1d;
        }
        _dragItems = [];
        _dragTops = [];
    }

    // Announces each item's Ctrl/Cmd+number shortcut, which follows the sidebar order.
    private void AssignShortcutKeys(PageType[] pages)
    {
        string modifier = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        for (int i = 0; i < pages.Length; i++)
        {
            if (NavButtonFor(pages[i]) is { } button)
                AutomationProperties.SetAcceleratorKey(button, $"{modifier}+{i + 1}");
        }
        AutomationProperties.SetAcceleratorKey(MoreNavBtn, $"{modifier}+{pages.Length + 1}");
    }

    private ListBoxItem? NavButtonFor(PageType page) => page switch
    {
        PageType.Catalog => CatalogNavBtn,
        PageType.Discover => DiscoverNavBtn,
        PageType.Updates => UpdatesNavBtn,
        PageType.Installed => InstalledNavBtn,
        PageType.Bundles => BundlesNavBtn,
        PageType.CatalogEditor => CatalogEditorNavBtn,
        PageType.Settings => SettingsNavBtn,
        PageType.Managers => ManagersNavBtn,
        _ => null,
    };

    private void SyncListBoxSelection(PageType page)
    {
        if (_isMoreFlyoutOpen)
            return;

        // Selection lives in two ListBoxes (main + footer); only one may hold a selection at a time.
        _lastNavItemSelectionWasAuto = true;
        NavListBox.SelectedItem = page switch
        {
            PageType.Discover => DiscoverNavBtn,
            PageType.Updates => UpdatesNavBtn,
            PageType.Installed => InstalledNavBtn,
            PageType.Bundles => BundlesNavBtn,
            PageType.Catalog => CatalogNavBtn,
            PageType.CatalogEditor => CatalogEditorNavBtn,
            _ => null,
        };
        FooterNavListBox.SelectedItem = page switch
        {
            PageType.Settings => SettingsNavBtn,
            PageType.Managers => ManagersNavBtn,
            _ => null,
        };
        _lastNavItemSelectionWasAuto = false;
        QueueSelectionPillUpdate(animate: NavigationSelectionPill.IsVisible);
    }

    private void NavListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => HandleNavSelectionChanged(NavListBox.SelectedItem);

    private void FooterNavListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => HandleNavSelectionChanged(FooterNavListBox.SelectedItem);

    private void HandleNavSelectionChanged(object? selectedItem)
    {
        if (_lastNavItemSelectionWasAuto) return;
        if (selectedItem is not ListBoxItem item || item.Tag is not string tag) return;

        if (tag == "More")
        {
            ShowMoreFlyout();
            return;
        }

        if (!Enum.TryParse<PageType>(tag, out var pageType))
            return;

        if (_dragCandidate is not null && ReferenceEquals(selectedItem, _dragCandidate))
            _pendingPointerNavigation = pageType;
        else
            ViewModel?.RequestNavigation(pageType.ToString());
    }

    // One full gear rotation on click, mirroring the spin of WinUI's AnimatedSettingsVisualSource.
    // The TransformAnimator manages the icon's RenderTransform, so the animation runs on the Visual itself.
    private readonly Animation _settingsIconSpin = new()
    {
        Duration = TimeSpan.FromSeconds(0.5),
        Easing = new CubicEaseOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
        },
    };

    private void SettingsNavBtn_Tapped(object? sender, TappedEventArgs e)
        => _ = _settingsIconSpin.RunAsync(SettingsIcon);

    public void ShowMoreFlyout()
    {
        if (_isMoreFlyoutOpen)
            return;

        // Keep the item selected until the menu closes so its accent pill remains anchored.
        _isMoreFlyoutOpen = true;
        QueueSelectionPillUpdate(animate: true, MoreNavBtn);
        Dispatcher.UIThread.Post(() => FlyoutBase.ShowAttachedFlyout(MoreNavBtn));
    }

    private void MoreNavBtn_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;

        ShowMoreFlyout();
        e.Handled = true;
    }

    public void FocusSelectedItem()
    {
        if ((NavListBox.SelectedItem ?? FooterNavListBox.SelectedItem) is InputElement item)
            item.Focus();
        else
            NavListBox.Focus();
    }

    private void QueueSelectionPillUpdate(bool animate, ListBoxItem? targetItem = null)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_isDragging)
                    return;

                if ((targetItem ?? NavListBox.SelectedItem ?? FooterNavListBox.SelectedItem) is ListBoxItem item)
                {
                    MoveSelectionPill(item, animate);
                }
                else
                {
                    ClearPendingPillLayout();
                    _pillAnimationCancellation?.Cancel();
                    NavigationSelectionPill.IsVisible = false;
                }
            },
            DispatcherPriority.Render);
    }

    private void MoveSelectionPill(ListBoxItem item, bool animate)
    {
        ClearPendingPillLayout();

        Point? itemPosition = item.TranslatePoint(default, SidebarLayout);
        if (itemPosition is null || item.Bounds.Height <= 0)
        {
            _pendingPillItem = item;
            _pendingPillAnimate = animate;
            item.LayoutUpdated += OnPendingPillLayoutUpdated;
            return;
        }

        double targetTop = itemPosition.Value.Y + ((item.Bounds.Height - PillHeight) / 2d);
        double targetLeft = itemPosition.Value.X;
        double targetCenter = targetTop + (PillHeight / 2d);

        _pillAnimationCancellation?.Cancel();
        _pillAnimationCancellation?.Dispose();
        _pillAnimationCancellation = null;

        Canvas.SetLeft(NavigationSelectionPill, targetLeft);

        if (!NavigationSelectionPill.IsVisible || !animate || MotionPreference.ReducedMotion)
        {
            SetPillEdges(targetTop, targetTop + PillHeight);
            NavigationSelectionPill.IsVisible = true;
            return;
        }

        double currentTop = _pillTop;
        double currentBottom = _pillBottom;
        if (Math.Abs(currentTop - targetTop) < 0.5)
        {
            SetPillEdges(targetTop, targetTop + PillHeight);
            return;
        }

        _pillAnimationCancellation = new CancellationTokenSource();
        int version = ++_pillAnimationVersion;
        _ = AnimatePillEdgesAsync(
            currentTop,
            currentBottom,
            targetTop,
            targetTop + PillHeight,
            targetCenter > currentTop + ((currentBottom - currentTop) / 2d),
            version,
            _pillAnimationCancellation.Token);
    }

    private void ClearPendingPillLayout()
    {
        if (_pendingPillItem is { } pending)
        {
            pending.LayoutUpdated -= OnPendingPillLayoutUpdated;
            _pendingPillItem = null;
        }
    }

    private void OnPendingPillLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingPillItem is { } item)
            MoveSelectionPill(item, _pendingPillAnimate);
    }

    private async Task AnimatePillEdgesAsync(
        double startTop,
        double startBottom,
        double targetTop,
        double targetBottom,
        bool movingDown,
        int version,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        double durationMs = PillAnimationDuration.TotalMilliseconds;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            double progress = Math.Clamp(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds / durationMs,
                0d,
                1d);

            // Preserve the original WinUI-like edge motion: the leading edge arrives quickly
            // while the trailing edge catches up. Fixed caps retain the original radius while
            // the stem stretches using render transforms, avoiding layout invalidation.
            double lead = EvaluateBezier(progress, 0d, 0d, 0d, 1d);
            double trail = EvaluateBezier(progress, 0.5d, 0d, 0.2d, 1d);
            double topProgress = movingDown ? trail : lead;
            double bottomProgress = movingDown ? lead : trail;
            double top = Lerp(startTop, targetTop, topProgress);
            double bottom = Lerp(startBottom, targetBottom, bottomProgress);
            SetPillEdges(top, bottom);

            if (progress >= 1d)
                break;

            if (await NextAnimationFrameAsync() is null)
                return;
        }

        if (version != _pillAnimationVersion || cancellationToken.IsCancellationRequested)
            return;

        SetPillEdges(targetTop, targetBottom);
    }

    private Task<TimeSpan?> NextAnimationFrameAsync()
    {
        if (TopLevel.GetTopLevel(NavigationSelectionPill) is not { } topLevel)
            return Task.FromResult<TimeSpan?>(null);

        var completion = new TaskCompletionSource<TimeSpan?>();
        topLevel.RequestAnimationFrame(time => completion.SetResult(time));
        return completion.Task;
    }

    private void SetPillEdges(double top, double bottom)
    {
        const double capDiameter = 3d;
        double stemHeight = Math.Max(0d, bottom - top - capDiameter);

        _pillTop = top;
        _pillBottom = bottom;
        _pillTopCapTranslate.Y = top;
        _pillStemScale.ScaleY = stemHeight;
        _pillStemTranslate.Y = top + (capDiameter / 2d);
        _pillBottomCapTranslate.Y = bottom - capDiameter;
    }

    private static double Lerp(double start, double end, double progress)
        => start + ((end - start) * progress);

    private static double EvaluateBezier(
        double progress,
        double control1X,
        double control1Y,
        double control2X,
        double control2Y)
    {
        double low = 0d;
        double high = 1d;
        for (int i = 0; i < 12; i++)
        {
            double parameter = (low + high) / 2d;
            if (BezierCoordinate(parameter, control1X, control2X) < progress)
                low = parameter;
            else
                high = parameter;
        }

        return BezierCoordinate((low + high) / 2d, control1Y, control2Y);
    }

    private static double BezierCoordinate(double parameter, double control1, double control2)
    {
        double inverse = 1d - parameter;
        return (3d * inverse * inverse * parameter * control1)
               + (3d * inverse * parameter * parameter * control2)
               + (parameter * parameter * parameter);
    }
}
