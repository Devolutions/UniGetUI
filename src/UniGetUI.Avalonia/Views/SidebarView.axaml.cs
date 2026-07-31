using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class SidebarView : BaseView<SidebarViewModel>
{
    private bool _lastNavItemSelectionWasAuto;
    private bool _isMoreFlyoutOpen;
    private CancellationTokenSource? _pillAnimationCancellation;
    private int _pillAnimationVersion;

    private readonly ScaleTransform _pillScale = new() { ScaleX = 1d, ScaleY = 1d };
    private readonly TranslateTransform _pillTranslate = new();
    private ListBoxItem? _pendingPillItem;
    private bool _pendingPillAnimate;

    private const double PillHeight = 16d;
    private const double MaxPillStretch = 1.0d;
    private const double PillStretchReferenceDistance = 120d;
    private static readonly TimeSpan PillAnimationDuration = TimeSpan.FromMilliseconds(180);

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

        NavigationSelectionPill.RenderTransformOrigin = new RelativePoint(0d, 0d, RelativeUnit.Relative);
        NavigationSelectionPill.RenderTransform = new TransformGroup
        {
            Children = { _pillScale, _pillTranslate },
        };

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
        if (DataContext is SidebarViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SidebarViewModel.SelectedPageType))
                    SyncListBoxSelection(vm.SelectedPageType);
            };
            // The startup page may already be set before this view subscribes, so apply it now.
            SyncListBoxSelection(vm.SelectedPageType);
        }
    }

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
            // Keep the item selected until the menu closes so its accent pill remains anchored.
            _isMoreFlyoutOpen = true;
            QueueSelectionPillUpdate(animate: true, item);
            FlyoutBase.ShowAttachedFlyout(item);
            return;
        }

        if (Enum.TryParse<PageType>(tag, out var pageType))
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
            _pillScale.ScaleY = 1d;
            _pillTranslate.Y = targetTop;
            NavigationSelectionPill.IsVisible = true;
            return;
        }

        double currentCenter = _pillTranslate.Y + ((PillHeight / 2d) * _pillScale.ScaleY);
        if (Math.Abs(currentCenter - targetCenter) < 0.5)
        {
            _pillScale.ScaleY = 1d;
            _pillTranslate.Y = targetTop;
            return;
        }

        _pillAnimationCancellation = new CancellationTokenSource();
        int version = ++_pillAnimationVersion;
        _ = AnimatePillAsync(currentCenter, targetCenter, version, _pillAnimationCancellation.Token);
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

    private async Task AnimatePillAsync(
        double startCenter,
        double targetCenter,
        int version,
        CancellationToken cancellationToken)
    {
        double distance = Math.Abs(targetCenter - startCenter);
        double stretch = MaxPillStretch * Math.Clamp(distance / PillStretchReferenceDistance, 0d, 1d);
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

            double eased = EvaluateBezier(progress, 0.33d, 0d, 0.1d, 1d);
            double center = Lerp(startCenter, targetCenter, eased);
            double scaleY = 1d + (stretch * Math.Sin(Math.PI * progress));

            _pillScale.ScaleY = scaleY;
            _pillTranslate.Y = center - ((PillHeight / 2d) * scaleY);

            if (progress >= 1d)
                break;

            try
            {
                await Task.Delay(16, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (version != _pillAnimationVersion || cancellationToken.IsCancellationRequested)
            return;

        _pillScale.ScaleY = 1d;
        _pillTranslate.Y = targetCenter - (PillHeight / 2d);
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
