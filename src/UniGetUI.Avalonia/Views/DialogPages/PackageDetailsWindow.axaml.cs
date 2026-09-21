using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views;

public partial class PackageDetailsWindow : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    private const double WideThreshold = 950;
    private const double ScreenshotGestureRetention = 0.12;
    private const double ScreenshotEdgeOverpan = 48.0;
    private const double ScreenshotEdgeResistance = 0.4;
    private const double ScreenshotIntentDeadZone = 6.0;
    private const double ScreenshotIntentBias = 1.15;
    private const double ScreenshotIntentFallback = 18.0;
    private const double ScreenshotSnapProjectionSeconds = 0.14;
    private const double ScreenshotSnapSpringStrength = 260.0;
    private const double ScreenshotSnapSpringDamping = 31.0;
    private const double ScreenshotSnapStopDistance = 0.35;
    private const double ScreenshotSnapStopVelocity = 6.0;
    private const double ScreenshotMaximumFrameTime = 1.0 / 30.0;
    private const string ContributeUrl = "https://github.com/Devolutions/UniGetUI";

    private enum LayoutMode { Unset, Normal, Wide }
    private enum GestureAxis { Undecided, Horizontal, Vertical }
    private LayoutMode _layoutMode = LayoutMode.Unset;

    /// <summary>True when the user confirmed the main action (install/update/uninstall) without extras.</summary>
    public bool ShouldProceedWithOperation { get; private set; }

    private readonly PackageDetailsViewModel _vm;
    private readonly TEL_InstallReferral _referral;
    private InstallOptionsViewModel? _installVm;
    private InstallOptions? _installOpts;
    private readonly DispatcherTimer _screenshotGestureTimer;
    private readonly TranslateTransform _screenshotStripTranslate = new();
    private Vector _screenshotGestureIntent;
    private GestureAxis _screenshotGestureAxis;
    private double _screenshotPosition;
    private double _screenshotVelocity;
    private double? _screenshotSnapTarget;
    private double _screenshotPageWidth;
    private long _lastScreenshotGestureInputTimestamp;
    private long _lastScreenshotHorizontalInputTimestamp;
    private TimeSpan? _screenshotLastFrame;
    private bool _screenshotFrameRequested;
    private bool _updatingScreenshotSelection;
    private int _lastScreenshotPipIndex = -1;

    public PackageDetailsWindow(
        IPackage package,
        OperationType operation,
        TEL_InstallReferral referral)
    {
        _referral = referral;
        _vm = new PackageDetailsViewModel(package, operation);
        DataContext = _vm;
        InitializeComponent();

        ScreenshotStrip.RenderTransform = _screenshotStripTranslate;
        _screenshotGestureTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ScreenshotGestureRetention),
        };
        _screenshotGestureTimer.Tick += CompleteScreenshotGesture;

        _vm.CloseRequested += (_, _) => Close();
        _vm.DetailsLoaded += (_, _) =>
        {
            BuildBasicInfoInlines();
            BuildDetailsInlines();
        };
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Screenshots.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildScreenshotStrip();
            UpdateScreenshotHeight();
            UpdateScreenshotStripLayout();
            UpdatePips();
        }, DispatcherPriority.Loaded);

        MainActionButton.Click += (_, _) => OnMainAction();
        ActionVariantsButton.Flyout = BuildActionFlyout();
        InstallOptionsSaveButton.Click += (_, _) => _ = SaveInstallOptionsAsync();
        ContributeButton.Click += (_, _) => OpenUrl(ContributeUrl);
        PrevScreenshotButton.Click += (_, _) => NavigateScreenshot(-1);
        NextScreenshotButton.Click += (_, _) => NavigateScreenshot(1);
        ScreenshotPips.AddHandler(Button.ClickEvent, OnPipClicked);
        ScreenshotPips.ContainerPrepared += (_, _) =>
            Dispatcher.UIThread.Post(UpdatePips, DispatcherPriority.Loaded);
        ScreenshotsBorder.AddHandler(
            PointerWheelChangedEvent,
            OnScreenshotPointerWheelChanged,
            RoutingStrategies.Tunnel);

        SizeChanged += (_, _) =>
        {
            ApplyLayoutForCurrentSize();
            // Recalculate after the responsive grid has received its new column width.
            Dispatcher.UIThread.Post(() =>
            {
                UpdateScreenshotHeight();
                UpdateScreenshotStripLayout();
            }, DispatcherPriority.Loaded);
        };

        // Seed inline blocks with loading placeholders.
        BuildBasicInfoInlines();
        BuildDetailsInlines();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyLayoutForCurrentSize();
        Dispatcher.UIThread.Post(() => MainActionButton.Focus(), DispatcherPriority.Background);
        _ = _vm.LoadDetailsAsync();
        TelemetryHandler.PackageDetails(_vm.Package, _vm.OperationRole.ToString());
        _ = InitInstallOptionsAsync();
    }

    private async Task InitInstallOptionsAsync()
    {
        _installOpts = await InstallOptionsFactory.LoadForPackageAsync(_vm.Package);
        _installVm = new InstallOptionsViewModel(_vm.Package, _vm.OperationRole, _installOpts);
        _installVm.CloseRequested += (_, _) =>
        {
            _ = SaveInstallOptionsAsync();
            Close();
        };
        var embed = new InstallOptionsControl { DataContext = _installVm };
        InstallOptionsHolder.Content = embed;
    }

    private async Task SaveInstallOptionsAsync()
    {
        if (_installVm is null || _installOpts is null) return;
        _installVm.ApplyChanges();
        await InstallOptionsFactory.SaveForPackageAsync(_installOpts, _vm.Package);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageDetailsViewModel.SelectedScreenshotIndex)
            || e.PropertyName == nameof(PackageDetailsViewModel.ScreenshotCount))
        {
            bool selectionUpdateWasInternal = _updatingScreenshotSelection;
            Dispatcher.UIThread.Post(() =>
            {
                if (e.PropertyName == nameof(PackageDetailsViewModel.SelectedScreenshotIndex)
                    && !selectionUpdateWasInternal
                    && _screenshotSnapTarget is null
                    && _screenshotGestureAxis != GestureAxis.Horizontal)
                {
                    SetScreenshotPositionToIndex(_vm.SelectedScreenshotIndex);
                }

                UpdatePips();
                UpdateScreenshotHeight();
                UpdateScreenshotStripLayout();
            }, DispatcherPriority.Loaded);
        }
    }

    private void OnPipClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control src) return;
        Control? cursor = src;
        while (cursor is not null && cursor.Parent is not ItemsControl)
            cursor = cursor.Parent as Control;
        if (cursor is null) return;

        int idx = ScreenshotPips.IndexFromContainer(cursor);
        if (idx >= 0 && idx < _vm.ScreenshotCount)
            SnapScreenshotToIndex(idx);
    }

    private void NavigateScreenshot(int delta)
    {
        if (_vm.ScreenshotCount == 0) return;
        int current = GetScreenshotNavigationIndex();
        SnapScreenshotToIndex(Math.Clamp(current + delta, 0, _vm.ScreenshotCount - 1));
    }

    private int GetScreenshotNavigationIndex()
    {
        if (_vm.ScreenshotCount == 0) return 0;
        double width = GetScreenshotPageWidth();
        double position = _screenshotSnapTarget ?? _screenshotPosition;
        return Math.Clamp(
            (int)Math.Round(position / width, MidpointRounding.AwayFromZero),
            0,
            _vm.ScreenshotCount - 1);
    }

    private void OnScreenshotPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (MotionPreference.ReducedMotion || e.Delta == default || e.KeyModifiers != KeyModifiers.None ||
            e.Source is not Visual source || TopLevel.GetTopLevel(this) is not { } top ||
            !SmoothScrollManager.IsPrecisionTouchpadScroll(top, e.Delta))
            return;

        e.Handled = true;
        _screenshotGestureTimer.Stop();

        long now = Stopwatch.GetTimestamp();
        _lastScreenshotGestureInputTimestamp = now;
        _screenshotGestureTimer.Interval = TimeSpan.FromSeconds(ScreenshotGestureRetention);

        if (_screenshotGestureAxis == GestureAxis.Undecided)
        {
            _screenshotGestureIntent += e.Delta;
            _screenshotGestureAxis = ResolveScreenshotGestureAxis(_screenshotGestureIntent);
            if (_screenshotGestureAxis == GestureAxis.Undecided)
            {
                _screenshotGestureTimer.Start();
                return;
            }

            if (_screenshotGestureAxis == GestureAxis.Horizontal && _vm.ScreenshotCount > 0)
            {
                ApplyScreenshotPanInput(
                    _screenshotGestureIntent.X * SmoothScrollPhysics.PrecisionTouchpadDistance,
                    now);
            }
            else
            {
                SmoothScrollManager.RoutePrecisionInput(
                    source,
                    new Vector(0, _screenshotGestureIntent.Y));
            }

            _screenshotGestureIntent = default;
        }
        else if (_screenshotGestureAxis == GestureAxis.Horizontal && _vm.ScreenshotCount > 0)
        {
            ApplyScreenshotPanInput(
                e.Delta.X * SmoothScrollPhysics.PrecisionTouchpadDistance,
                now);
        }
        else
        {
            SmoothScrollManager.RoutePrecisionInput(source, new Vector(0, e.Delta.Y));
        }

        _screenshotGestureTimer.Start();
    }

    private static GestureAxis ResolveScreenshotGestureAxis(Vector delta)
    {
        double x = Math.Abs(delta.X * SmoothScrollPhysics.PrecisionTouchpadDistance);
        double y = Math.Abs(delta.Y * SmoothScrollPhysics.PrecisionTouchpadDistance);
        double largest = Math.Max(x, y);
        if (largest < ScreenshotIntentDeadZone)
            return GestureAxis.Undecided;

        if (x >= y * ScreenshotIntentBias)
            return GestureAxis.Horizontal;
        if (y >= x * ScreenshotIntentBias)
            return GestureAxis.Vertical;

        if (largest >= ScreenshotIntentFallback)
            return x >= y ? GestureAxis.Horizontal : GestureAxis.Vertical;

        return GestureAxis.Undecided;
    }

    private void ApplyScreenshotPanInput(double input, long timestamp)
    {
        if (input == 0) return;

        if (_screenshotSnapTarget is not null)
        {
            // Direct manipulation always takes ownership from the snap at its current position.
            _screenshotSnapTarget = null;
            _screenshotVelocity = 0;
            _screenshotLastFrame = null;
        }

        double positionDelta = -input;
        if (_lastScreenshotHorizontalInputTimestamp != 0)
        {
            double elapsed = Stopwatch.GetElapsedTime(
                _lastScreenshotHorizontalInputTimestamp,
                timestamp).TotalSeconds;
            if (elapsed is > 0 and < 0.1)
            {
                double instantaneousVelocity = positionDelta / elapsed;
                if (_screenshotVelocity != 0 &&
                    Math.Sign(_screenshotVelocity) != Math.Sign(instantaneousVelocity))
                {
                    _screenshotVelocity = instantaneousVelocity;
                }
                else
                {
                    _screenshotVelocity =
                        _screenshotVelocity * 0.55 + instantaneousVelocity * 0.45;
                }
            }
        }

        _lastScreenshotHorizontalInputTimestamp = timestamp;
        _screenshotPosition = ApplyScreenshotPositionDelta(
            _screenshotPosition,
            positionDelta);
        RequestScreenshotFrame();
    }

    private double ApplyScreenshotPositionDelta(double position, double delta)
    {
        if (delta == 0) return position;

        double maximum = GetMaximumScreenshotPosition();
        if (position < 0)
        {
            if (delta > 0)
            {
                double next = position + delta;
                if (next <= 0) return next;
                return ApplyScreenshotPositionDelta(0, next);
            }

            return -AddScreenshotEdgeResistance(-position, -delta);
        }

        if (position > maximum)
        {
            if (delta < 0)
            {
                double next = position + delta;
                if (next >= maximum) return next;
                return ApplyScreenshotPositionDelta(maximum, next - maximum);
            }

            return maximum + AddScreenshotEdgeResistance(
                position - maximum,
                delta);
        }

        double candidate = position + delta;
        if (candidate < 0)
            return -AddScreenshotEdgeResistance(0, -candidate);
        if (candidate > maximum)
            return maximum + AddScreenshotEdgeResistance(0, candidate - maximum);

        return candidate;
    }

    private static double AddScreenshotEdgeResistance(double displacement, double input)
    {
        double remaining = Math.Max(0, ScreenshotEdgeOverpan - displacement);
        if (remaining == 0 || input <= 0) return displacement;

        double added = remaining *
                       (1.0 - Math.Exp(-input * ScreenshotEdgeResistance / ScreenshotEdgeOverpan));
        return displacement + added;
    }

    private void CompleteScreenshotGesture(object? sender, EventArgs e)
    {
        _screenshotGestureTimer.Stop();

        TimeSpan retention = TimeSpan.FromSeconds(ScreenshotGestureRetention);
        if (_lastScreenshotGestureInputTimestamp != 0)
        {
            TimeSpan idle = Stopwatch.GetElapsedTime(_lastScreenshotGestureInputTimestamp);
            if (idle < retention)
            {
                _screenshotGestureTimer.Interval = retention - idle;
                _screenshotGestureTimer.Start();
                return;
            }
        }

        if (_screenshotGestureAxis == GestureAxis.Horizontal && _vm.ScreenshotCount > 0)
            StartScreenshotSnapToRest();

        ResetScreenshotGestureRouting();
    }

    private void StartScreenshotSnapToRest()
    {
        double width = GetScreenshotPageWidth();
        double maximum = GetMaximumScreenshotPosition();

        int targetIndex;
        if (_screenshotPosition <= 0)
        {
            targetIndex = 0;
        }
        else if (_screenshotPosition >= maximum)
        {
            targetIndex = _vm.ScreenshotCount - 1;
        }
        else
        {
            double projected = _screenshotPosition +
                               _screenshotVelocity * ScreenshotSnapProjectionSeconds;
            targetIndex = Math.Clamp(
                (int)Math.Round(projected / width, MidpointRounding.AwayFromZero),
                0,
                _vm.ScreenshotCount - 1);
        }

        SnapScreenshotToIndex(targetIndex, preserveVelocity: true);
    }

    private void SnapScreenshotToIndex(int index, bool preserveVelocity = false)
    {
        if (_vm.ScreenshotCount == 0) return;

        index = Math.Clamp(index, 0, _vm.ScreenshotCount - 1);
        _screenshotGestureTimer.Stop();
        ResetScreenshotGestureRouting();

        double target = index * GetScreenshotPageWidth();
        if (MotionPreference.ReducedMotion)
        {
            _screenshotPosition = target;
            _screenshotVelocity = 0;
            _screenshotSnapTarget = null;
            RenderScreenshotStrip();
            SettleScreenshotSelection(index);
            return;
        }

        if (!preserveVelocity)
        {
            double direction = target - _screenshotPosition;
            if (_screenshotVelocity == 0 ||
                direction == 0 ||
                Math.Sign(_screenshotVelocity) != Math.Sign(direction))
                _screenshotVelocity = 0;
        }

        _screenshotSnapTarget = target;
        _screenshotLastFrame = null;
        RequestScreenshotFrame();
    }

    private void RequestScreenshotFrame()
    {
        if (_screenshotFrameRequested) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            RenderScreenshotStrip();
            return;
        }

        _screenshotFrameRequested = true;
        top.RequestAnimationFrame(OnScreenshotFrame);
    }

    private void OnScreenshotFrame(TimeSpan now)
    {
        _screenshotFrameRequested = false;

        if (_screenshotSnapTarget is { } target)
        {
            double dt = _screenshotLastFrame is { } last
                ? (now - last).TotalSeconds
                : 1.0 / 60.0;
            _screenshotLastFrame = now;
            if (dt <= 0) dt = 1.0 / 60.0;
            dt = Math.Min(dt, ScreenshotMaximumFrameTime);

            double acceleration =
                -ScreenshotSnapSpringStrength * (_screenshotPosition - target) -
                ScreenshotSnapSpringDamping * _screenshotVelocity;
            _screenshotVelocity += acceleration * dt;
            _screenshotPosition += _screenshotVelocity * dt;

            double distance = Math.Abs(_screenshotPosition - target);
            if (distance <= ScreenshotSnapStopDistance &&
                Math.Abs(_screenshotVelocity) <= ScreenshotSnapStopVelocity)
            {
                _screenshotPosition = target;
                _screenshotVelocity = 0;
                _screenshotSnapTarget = null;
                _screenshotLastFrame = null;
                RenderScreenshotStrip();

                int index = Math.Clamp(
                    (int)Math.Round(target / GetScreenshotPageWidth()),
                    0,
                    _vm.ScreenshotCount - 1);
                SettleScreenshotSelection(index);
                return;
            }

            RenderScreenshotStrip();
            RequestScreenshotFrame();
            return;
        }

        RenderScreenshotStrip();
    }

    private void RenderScreenshotStrip()
    {
        _screenshotStripTranslate.X = -_screenshotPosition;
        int active = GetVisualScreenshotIndex();
        if (active != _lastScreenshotPipIndex)
        {
            _lastScreenshotPipIndex = active;
            UpdatePips();
            UpdateScreenshotHeight();
            Dispatcher.UIThread.Post(UpdateScreenshotStripLayout, DispatcherPriority.Loaded);
        }
    }

    private int GetVisualScreenshotIndex()
    {
        if (_vm.ScreenshotCount == 0) return -1;
        return Math.Clamp(
            (int)Math.Round(
                _screenshotPosition / GetScreenshotPageWidth(),
                MidpointRounding.AwayFromZero),
            0,
            _vm.ScreenshotCount - 1);
    }

    private double GetScreenshotPageWidth()
    {
        if (_screenshotPageWidth > 0) return _screenshotPageWidth;

        double width = ScreenshotsBorder.Bounds.Width;
        if (width > 0) return width;

        double contentWidth = Math.Max(1, Bounds.Width - 48);
        return _layoutMode == LayoutMode.Wide
            ? Math.Max(1, (contentWidth - MainGrid.ColumnSpacing) / 2)
            : contentWidth;
    }

    private double GetMaximumScreenshotPosition() =>
        Math.Max(0, (_vm.ScreenshotCount - 1) * GetScreenshotPageWidth());

    private void RebuildScreenshotStrip()
    {
        double oldWidth = _screenshotPageWidth;
        double pagePosition = oldWidth > 0
            ? _screenshotPosition / oldWidth
            : Math.Clamp(_vm.SelectedScreenshotIndex, 0, Math.Max(0, _vm.ScreenshotCount - 1));
        double? targetPage = _screenshotSnapTarget is { } target && oldWidth > 0
            ? target / oldWidth
            : null;

        ScreenshotStrip.Children.Clear();
        foreach (var screenshot in _vm.Screenshots)
        {
            ScreenshotStrip.Children.Add(new Image
            {
                Source = screenshot,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            });
        }

        _screenshotPosition = pagePosition * GetScreenshotPageWidth();
        if (targetPage is { } page)
            _screenshotSnapTarget = page * GetScreenshotPageWidth();

        UpdateScreenshotStripLayout();
        RenderScreenshotStrip();
    }

    private void UpdateScreenshotStripLayout()
    {
        double width = ScreenshotsBorder.Bounds.Width;
        if (width <= 0) return;

        double oldWidth = _screenshotPageWidth;
        double pagePosition = oldWidth > 0
            ? _screenshotPosition / oldWidth
            : Math.Clamp(_vm.SelectedScreenshotIndex, 0, Math.Max(0, _vm.ScreenshotCount - 1));
        double? targetPage = _screenshotSnapTarget is { } target && oldWidth > 0
            ? target / oldWidth
            : null;

        _screenshotPageWidth = width;
        double height = !double.IsNaN(ScreenshotsBorder.Height) && ScreenshotsBorder.Height > 0
            ? ScreenshotsBorder.Height
            : Math.Max(1, ScreenshotsBorder.Bounds.Height);

        foreach (Control child in ScreenshotStrip.Children)
        {
            child.Width = width;
            child.Height = height;
        }

        _screenshotPosition = pagePosition * width;
        if (targetPage is { } page)
            _screenshotSnapTarget = page * width;

        RenderScreenshotStrip();
    }

    private void SetScreenshotPositionToIndex(int index)
    {
        if (_vm.ScreenshotCount == 0)
        {
            _screenshotPosition = 0;
            RenderScreenshotStrip();
            return;
        }

        index = Math.Clamp(index, 0, _vm.ScreenshotCount - 1);
        _screenshotPosition = index * GetScreenshotPageWidth();
        _screenshotVelocity = 0;
        _screenshotSnapTarget = null;
        _screenshotLastFrame = null;
        RenderScreenshotStrip();
    }

    private void SettleScreenshotSelection(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _vm.ScreenshotCount - 1));
        if (_vm.ScreenshotCount > 0 && _vm.SelectedScreenshotIndex != index)
        {
            _updatingScreenshotSelection = true;
            _vm.SelectedScreenshotIndex = index;
            _updatingScreenshotSelection = false;
        }

        UpdatePips();
        UpdateScreenshotHeight();
        Dispatcher.UIThread.Post(UpdateScreenshotStripLayout, DispatcherPriority.Loaded);
    }

    private void ResetScreenshotGestureRouting()
    {
        _screenshotGestureAxis = GestureAxis.Undecided;
        _screenshotGestureIntent = default;
        _lastScreenshotGestureInputTimestamp = 0;
        _lastScreenshotHorizontalInputTimestamp = 0;
    }

    private void UpdatePips()
    {
        int active = GetVisualScreenshotIndex();
        foreach (var container in ScreenshotPips.GetRealizedContainers())
        {
            int index = ScreenshotPips.IndexFromContainer(container);
            Ellipse? ellipse = container is Button { Content: Ellipse direct }
                ? direct
                : container.GetVisualDescendants()
                    .OfType<Ellipse>()
                    .FirstOrDefault(candidate => candidate.Classes.Contains("pip"));
            ellipse?.Classes.Set("active", index == active);
        }
    }

    // ── Responsive layout ────────────────────────────────────────────────────

    private void ApplyLayoutForCurrentSize()
    {
        var wide = Bounds.Width >= WideThreshold;
        var mode = wide ? LayoutMode.Wide : LayoutMode.Normal;
        if (mode != _layoutMode)
        {
            _layoutMode = mode;

            if (mode == LayoutMode.Wide)
            {
                // Ensure two columns and the right-column panels live in RightPanel.
                EnsureChild(RightPanel, ScreenshotsPanel, 0);
                EnsureChild(RightPanel, DetailsPanel, 1);
                RightPanel.IsVisible = true;
                MainGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                // Move screenshots + details into LeftPanel (after install options) and collapse the right column.
                EnsureChild(LeftPanel, ScreenshotsPanel, LeftPanel.Children.Count);
                EnsureChild(LeftPanel, DetailsPanel, LeftPanel.Children.Count);
                RightPanel.IsVisible = false;
                MainGrid.ColumnDefinitions[1].Width = new GridLength(0);
            }
        }

        UpdateScreenshotHeight();
    }

    private void UpdateScreenshotHeight()
    {
        int screenshotIndex = GetVisualScreenshotIndex();
        if (!_vm.HasScreenshots
            || screenshotIndex < 0
            || screenshotIndex >= _vm.Screenshots.Count)
        {
            ScreenshotsBorder.Height = _layoutMode == LayoutMode.Wide ? 150 : 130;
            return;
        }

        PixelSize pixels = _vm.Screenshots[screenshotIndex].PixelSize;
        if (pixels.Width <= 0 || pixels.Height <= 0)
            return;

        double width = ScreenshotsBorder.Bounds.Width;
        if (width <= 0)
        {
            double contentWidth = Math.Max(0, Bounds.Width - 48);
            width = _layoutMode == LayoutMode.Wide
                ? Math.Max(0, (contentWidth - MainGrid.ColumnSpacing) / 2)
                : contentWidth;
        }

        double aspectRatio = (double)pixels.Width / pixels.Height;
        double maximumHeight = _layoutMode == LayoutMode.Wide ? 420 : 560;
        ScreenshotsBorder.Height = Math.Clamp(width / aspectRatio, 180, maximumHeight);
    }

    /// <summary>Move <paramref name="child"/> to <paramref name="target"/> at the given index, removing from its old parent first.</summary>
    private static void EnsureChild(Panel target, Control child, int index)
    {
        if (child.Parent is Panel current && current != target)
            current.Children.Remove(child);
        if (!target.Children.Contains(child))
            target.Children.Insert(Math.Min(index, target.Children.Count), child);
    }

    // ── Per-row layout builders (inline flow like WinUI's RichTextBlock) ─────

    private void BuildBasicInfoInlines()
    {
        BasicInfoPanel.Children.Clear();

        if (!string.IsNullOrWhiteSpace(_vm.Description))
        {
            BasicInfoPanel.Children.Add(new SelectableTextBlock
            {
                Text = _vm.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        AddInlineRow(BasicInfoPanel, _vm.LabelHomepage, _vm.HomepageUrl);
        AddInlineRow(BasicInfoPanel, _vm.LabelPublisher, _vm.Publisher);
        AddInlineRow(BasicInfoPanel, _vm.LabelAuthor, _vm.Author);
        AddLicenseRow(BasicInfoPanel);
        AddInlineRow(BasicInfoPanel, _vm.LabelUpdateDate, _vm.UpdateDate);
        AddInlineRow(BasicInfoPanel, _vm.LabelSource, _vm.SourceDisplay);
    }

    private void BuildDetailsInlines()
    {
        DetailsPanel.Children.Clear();

        AddInlineRow(DetailsPanel, _vm.LabelPackageId, _vm.PackageId);
        AddInlineRow(DetailsPanel, _vm.LabelManifest, _vm.ManifestUrl);
        AddInlineRow(DetailsPanel, _vm.LabelVersion, _vm.VersionDisplay, _vm.InstalledVersionTooltip);

        AddSpacer(DetailsPanel);

        AddInlineRow(DetailsPanel, _vm.LabelInstallerType, _vm.InstallerType);
        AddInlineRow(DetailsPanel, _vm.LabelInstallerUrl, _vm.InstallerUrl);
        AddInlineRow(DetailsPanel, _vm.InstallerHashLabel.TrimEnd(':'), _vm.InstallerHash);

        AddDownloadRow(DetailsPanel);

        AddSpacer(DetailsPanel);

        // Dependencies header + list
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.LabelDependencies + ":",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (_vm.HasDependencyNote)
        {
            DetailsPanel.Children.Add(new SelectableTextBlock
            {
                Text = "  " + _vm.DependencyNote,
                Foreground = NotAvailableBrush,
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
        else
        {
            foreach (var dep in _vm.Dependencies)
            {
                DetailsPanel.Children.Add(new SelectableTextBlock
                {
                    Text = dep.DisplayText,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        AddSpacer(DetailsPanel);

        // Release notes
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.LabelReleaseNotes + ":",
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        DetailsPanel.Children.Add(new SelectableTextBlock
        {
            Text = _vm.ReleaseNotes,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        AddInlineRow(DetailsPanel, _vm.LabelReleaseNotesUrl, _vm.ReleaseNotesUrl);
    }

    private static readonly IBrush NotAvailableBrush =
        new SolidColorBrush(Color.FromArgb(255, 127, 127, 127));

    private static readonly IBrush WarningBrush =
        new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));

    private static void AddSpacer(StackPanel host) =>
        host.Children.Add(new Border { Height = 10 });

    /// <summary>
    /// Builds a single wrap-able row with "<bold>Label:</bold> value" all on one line,
    /// matching the WinUI RichTextBlock paragraph layout.
    /// </summary>
    private void AddInlineRow(
        StackPanel host,
        string label,
        string value,
        string? warningTooltip = null
    )
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        var inlines = tb.Inlines ??= new InlineCollection();
        inlines.Add(new Run(label + ": ") { FontWeight = FontWeight.Bold });
        if (string.IsNullOrWhiteSpace(value) || value == _vm.LabelNotAvailable)
            inlines.Add(new Run(_vm.LabelNotAvailable)
            {
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
        else
            inlines.Add(new Run(value));

        if (warningTooltip is null)
        {
            host.Children.Add(tb);
            return;
        }

        host.Children.Add(BuildWarningRow(tb, warningTooltip));
    }

    private static Grid BuildWarningRow(Control content, string tooltip)
    {
        ToolTip.SetTip(content, tooltip);
        var icon = new SvgIcon
        {
            Path = "avares://UniGetUI/Assets/Symbols/warning_filled.svg",
            Width = 16,
            Height = 16,
            Foreground = WarningBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, 2, 0, 0),
        };
        ToolTip.SetTip(icon, tooltip);

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
                new ColumnDefinition(GridLength.Auto),
            ],
        };
        Grid.SetColumn(content, 0);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(content);
        grid.Children.Add(icon);
        return grid;
    }

    private void AddInlineRow(StackPanel host, string label, Uri? url)
    {
        if (url is null)
        {
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            var inlines = tb.Inlines ??= new InlineCollection();
            inlines.Add(new Run(label + ": ") { FontWeight = FontWeight.Bold });
            inlines.Add(new Run(_vm.LabelNotAvailable)
            {
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
            host.Children.Add(tb);
            return;
        }

        host.Children.Add(BuildLinkRow(BoldLabel(label + ": "), url.ToString()));
    }

    private void AddLicenseRow(StackPanel host)
    {
        bool hasName = !string.IsNullOrEmpty(_vm.LicenseName);
        bool hasUrl = _vm.LicenseUrl is not null;

        if (!hasUrl)
        {
            // No link: render as a plain selectable line with the usual cursor.
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            var inlines = tb.Inlines ??= new InlineCollection();
            inlines.Add(new Run(_vm.LabelLicense + ": ") { FontWeight = FontWeight.Bold });
            if (hasName)
                inlines.Add(new Run(_vm.LicenseName!));
            else
                inlines.Add(new Run(_vm.LabelNotAvailable)
                {
                    Foreground = NotAvailableBrush,
                    FontStyle = FontStyle.Italic,
                });
            host.Children.Add(tb);
            return;
        }

        var labelBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var labelInlines = labelBlock.Inlines ??= new InlineCollection();
        labelInlines.Add(new Run(_vm.LabelLicense + ": ") { FontWeight = FontWeight.Bold });
        if (hasName)
            labelInlines.Add(new Run(_vm.LicenseName! + " "));

        host.Children.Add(BuildLinkRow(labelBlock, _vm.LicenseUrl!.ToString()));
    }

    private void AddDownloadRow(StackPanel host)
    {
        if (_vm.CanDownloadInstaller && !_vm.IsLoading)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var link = CreateLinkBlock(_vm.LabelDownloadInstaller, DownloadInstaller);
            link.FontWeight = FontWeight.SemiBold;
            panel.Children.Add(link);
            if (!string.IsNullOrEmpty(_vm.InstallerSize))
                panel.Children.Add(new SelectableTextBlock { Text = $" ({_vm.InstallerSize})" });
            host.Children.Add(panel);
        }
        else
        {
            host.Children.Add(new SelectableTextBlock
            {
                Text = _vm.LabelInstallerNotAvailable,
                Foreground = NotAvailableBrush,
                FontStyle = FontStyle.Italic,
            });
        }
    }

    private static SelectableTextBlock BoldLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>"Label: url" laid out so a long URL wraps under itself; only the URL is the link.</summary>
    private Grid BuildLinkRow(Control label, string url)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            ],
        };
        Grid.SetColumn(label, 0);
        var link = CreateLinkBlock(url, () => OpenUrl(url));
        link.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(link, 1);
        grid.Children.Add(label);
        grid.Children.Add(link);
        return grid;
    }

    /// <summary>A selectable, copyable hyperlink. The hand cursor and activation cover only this text.</summary>
    private SelectableTextBlock CreateLinkBlock(string text, Action activate)
    {
        var link = new SelectableTextBlock
        {
            Text = text,
            Foreground = LinkBrush,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AttachLinkActivation(link, activate);
        return link;
    }

    /// <summary>Activate on a clean click, but let a press-and-drag run the text selection instead.</summary>
    private static void AttachLinkActivation(Control link, Action activate)
    {
        Point press = default;
        bool tracking = false;

        link.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(link).Properties.IsLeftButtonPressed)
            {
                tracking = true;
                press = e.GetPosition(link);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        link.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!tracking) return;
            tracking = false;
            var moved = e.GetPosition(link) - press;
            if (Math.Abs(moved.X) <= 4 && Math.Abs(moved.Y) <= 4)
                activate();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private IBrush LinkBrush =>
        this.TryFindResource("HyperlinkForeground", ActualThemeVariant, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open URL: {url}");
            Logger.Error(ex);
        }
    }

    private void DownloadInstaller()
    {
        if (!_vm.Package.Manager.Capabilities.CanDownloadInstaller) return;
        Close();
        _ = AvaloniaPackageOperationHelper.AskLocationAndDownloadAsync(_vm.Package, _referral);
    }

    // ── Action flyout ────────────────────────────────────────────────────────

    private MenuFlyout BuildActionFlyout()
    {
        var flyout = new MenuFlyout();
        var asAdmin = new MenuItem { Header = _vm.AsAdminLabel, IsEnabled = _vm.CanRunAsAdmin };
        var interactive = new MenuItem { Header = _vm.InteractiveLabel, IsEnabled = _vm.CanRunInteractively };
        var skipOrRemove = new MenuItem { Header = _vm.SkipHashOrRemoveDataLabel, IsEnabled = _vm.CanSkipHashOrRemoveData };

        var role = _vm.OperationRole;
        if (role is OperationType.Uninstall)
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, remove_data: true);
        }
        else
        {
            asAdmin.Click += (_, _) => _ = LaunchAndClose(role, elevated: true);
            interactive.Click += (_, _) => _ = LaunchAndClose(role, interactive: true);
            skipOrRemove.Click += (_, _) => _ = LaunchAndClose(role, no_integrity: true);
        }

        flyout.Items.Add(asAdmin);
        flyout.Items.Add(interactive);
        flyout.Items.Add(skipOrRemove);
        return flyout;
    }

    private void OnMainAction()
    {
        ShouldProceedWithOperation = true;
        Close();
    }

    private async Task LaunchAndClose(
        OperationType role,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null,
        bool? remove_data = null)
    {
        Close();

        var pkg = _vm.Package;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(
            pkg,
            elevated: elevated,
            interactive: interactive,
            no_integrity: no_integrity,
            remove_data: remove_data);

        if (PackageOperation.HasPendingOperation(pkg, role)) return;

        AbstractOperation op = role switch
        {
            OperationType.Install => new InstallPackageOperation(pkg, opts),
            OperationType.Update => new UpdatePackageOperation(pkg, opts),
            OperationType.Uninstall => new UninstallPackageOperation(pkg, opts),
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        switch (role)
        {
            case OperationType.Install:
                op.OperationSucceeded += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.SUCCESS, _referral);
                op.OperationFailed += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.FAILED, _referral);
                break;
            case OperationType.Update:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
                break;
            case OperationType.Uninstall:
                op.OperationSucceeded += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.SUCCESS);
                op.OperationFailed += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.FAILED);
                break;
        }

        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }
}
