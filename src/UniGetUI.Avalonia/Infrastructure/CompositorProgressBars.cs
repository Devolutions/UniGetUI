#if WINDOWS
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using AvaloniaColor = Avalonia.Media.Color;

namespace UniGetUI.Avalonia.Infrastructure;

internal static partial class CompositorProgressBars
{
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(250);
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private static readonly Dictionary<ProgressBar, Entry> _entries = [];
    private static readonly Dictionary<Window, Host> _hosts = [];
    private static nint _dispatcherQueueController;
    private static Compositor? _compositor;
    private static DispatcherTimer? _timer;
    private static bool _disabled;
    private static bool _syncPosted;

    public static bool IsSupported()
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
           && !Design.IsDesignMode
           && !WindowsAvaloniaRenderingPolicy.ShouldUseSoftwareRendering;

    public static void Register(ProgressBar bar)
    {
        if (_disabled || _entries.ContainsKey(bar) || TopLevel.GetTopLevel(bar) is not Window window)
            return;

        Host? host = GetOrCreateHost(window);
        if (host is null)
            return;

        _entries[bar] = new Entry(bar, host);
        bar.Classes.Add(IndeterminateProgressBars.CompositorClass);
        PostSync();
    }

    public static void Refresh()
    {
        if (_entries.Count > 0)
            PostSync();
    }

    public static void Unregister(ProgressBar bar)
    {
        if (!_entries.Remove(bar, out Entry? entry))
            return;

        bar.Classes.Remove(IndeterminateProgressBars.CompositorClass);
        entry.Dispose();
        if (_entries.Count == 0)
            _timer?.Stop();
    }

    private static void UpdateTimer(bool anyAnimating)
    {
        if (!anyAnimating)
        {
            _timer?.Stop();
            return;
        }

        _timer ??= new DispatcherTimer(SyncInterval, DispatcherPriority.Background, (_, _) => SyncAll());
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private static Host? GetOrCreateHost(Window window)
    {
        if (_hosts.TryGetValue(window, out Host? existing))
            return existing;

        try
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
                return null;

            Compositor compositor = GetCompositor();
            var host = new Host(window, compositor, CreateTopmostTarget(compositor, hwnd));
            _hosts[window] = host;
            window.LayoutUpdated += OnLayoutUpdated;
            window.Closed += OnWindowClosed;
            return host;
        }
        catch (Exception ex)
        {
            _disabled = true;
            Logger.Warn("Could not animate progress bars in the system compositor; using the Avalonia animation instead");
            Logger.Warn(ex);
            foreach (ProgressBar bar in _entries.Keys.ToList())
                Unregister(bar);
            return null;
        }
    }

    private static Compositor GetCompositor()
    {
        if (_compositor is not null)
            return _compositor;

        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions
            {
                Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = DqTypeThreadCurrent,
                ApartmentType = DqtatComNone,
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out _dispatcherQueueController));
        }

        _compositor = new Compositor();
        return _compositor;
    }

    private static DesktopWindowTarget CreateTopmostTarget(Compositor compositor, nint hwnd)
    {
        nint compositorPtr = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Guid iid = typeof(ICompositorDesktopInterop).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositorPtr, in iid, out nint interopPtr));
        try
        {
            var interop = (ICompositorDesktopInterop)ComWrappers.GetOrCreateObjectForComInstance(
                interopPtr, CreateObjectFlags.None);
            Marshal.ThrowExceptionForHR(interop.CreateDesktopWindowTarget(hwnd, 1, out nint targetPtr));
            try
            {
                return DesktopWindowTarget.FromAbi(targetPtr);
            }
            finally
            {
                Marshal.Release(targetPtr);
            }
        }
        finally
        {
            Marshal.Release(interopPtr);
        }
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e) => PostSync();

    private static void PostSync()
    {
        if (_syncPosted)
            return;
        _syncPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncPosted = false;
            SyncAll();
        }, DispatcherPriority.Background);
    }

    private static void SyncAll()
    {
        bool anyAnimating = false;
        foreach (Entry entry in _entries.Values)
            anyAnimating |= entry.Sync();
        UpdateTimer(anyAnimating);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window || !_hosts.Remove(window, out Host? host))
            return;

        window.LayoutUpdated -= OnLayoutUpdated;
        window.Closed -= OnWindowClosed;
        foreach (ProgressBar bar in _entries.Where(pair => pair.Value.Host == host).Select(pair => pair.Key).ToList())
            Unregister(bar);
        host.Dispose();
    }

    private sealed class Host(Window window, Compositor compositor, DesktopWindowTarget target) : IDisposable
    {
        public Window Window { get; } = window;
        public Compositor Compositor { get; } = compositor;
        public ContainerVisual Root { get; } = CreateRoot(compositor, target);

        private static ContainerVisual CreateRoot(Compositor compositor, DesktopWindowTarget target)
        {
            ContainerVisual root = compositor.CreateContainerVisual();
            target.Root = root;
            return root;
        }

        public void Dispose()
        {
            target.Root = null;
            Root.Dispose();
            target.Dispose();
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly ProgressBar _bar;
        private readonly ContainerVisual _clip;
        private readonly ContainerVisual _track;
        private readonly CompositionRoundedRectangleGeometry _trackShape;
        private readonly CompositionColorBrush _brush;
        private readonly ShapeVisual _first;
        private readonly ShapeVisual _second;
        private readonly CompositionRoundedRectangleGeometry _firstShape;
        private readonly CompositionRoundedRectangleGeometry _secondShape;
        private IndeterminateBarSnapshot? _animated;
        private double _animatedScale;

        public Entry(ProgressBar bar, Host host)
        {
            _bar = bar;
            Host = host;
            Compositor c = host.Compositor;

            _clip = c.CreateContainerVisual();
            _clip.Clip = c.CreateInsetClip();
            _clip.IsVisible = false;

            _trackShape = c.CreateRoundedRectangleGeometry();
            _track = c.CreateContainerVisual();
            _track.Clip = c.CreateGeometricClip(_trackShape);
            _clip.Children.InsertAtTop(_track);

            _brush = c.CreateColorBrush();
            (_first, _firstShape) = CreateIndicator(c, _brush);
            (_second, _secondShape) = CreateIndicator(c, _brush);
            _track.Children.InsertAtTop(_first);
            _track.Children.InsertAtTop(_second);

            host.Root.Children.InsertAtTop(_clip);
        }

        public Host Host { get; }

        private static (ShapeVisual, CompositionRoundedRectangleGeometry) CreateIndicator(Compositor c, CompositionBrush brush)
        {
            CompositionRoundedRectangleGeometry geometry = c.CreateRoundedRectangleGeometry();
            CompositionSpriteShape shape = c.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            ShapeVisual visual = c.CreateShapeVisual();
            visual.Shapes.Add(shape);
            return (visual, geometry);
        }

        public bool Sync()
        {
            IndeterminateBarSnapshot? snapshot = IndeterminateBarSnapshot.Capture(_bar, Host.Window);
            if (snapshot is null)
            {
                _clip.IsVisible = false;
                return _bar.IsIndeterminate;
            }

            float scale = (float)Host.Window.RenderScaling;
            Rect clip = snapshot.Clip;
            _clip.Offset = new Vector3((float)(clip.X * scale), (float)(clip.Y * scale), 0);
            _clip.Size = new Vector2((float)(clip.Width * scale), (float)(clip.Height * scale));
            _clip.Opacity = (float)snapshot.Opacity;

            Rect track = snapshot.Track;
            _track.Offset = new Vector3((float)((track.X - clip.X) * scale), (float)((track.Y - clip.Y) * scale), 0);
            _track.Size = new Vector2((float)(track.Width * scale), (float)(track.Height * scale));
            _trackShape.Size = _track.Size;
            float radius = (float)(snapshot.CornerRadius * scale);
            _trackShape.CornerRadius = new Vector2(radius, radius);

            AvaloniaColor color = snapshot.Color;
            _brush.Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);

            if (!SameAnimation(snapshot, scale))
            {
                StartLane(_first, _firstShape, snapshot, snapshot.First, radius, scale, 0f, 0.75f);
                StartLane(_second, _secondShape, snapshot, snapshot.Second, radius, scale, 0.375f, 1f);
                _animated = snapshot;
                _animatedScale = scale;
            }

            _clip.IsVisible = true;
            return true;
        }

        private bool SameAnimation(IndeterminateBarSnapshot snapshot, double scale)
            => _animated is { } previous
               && _animatedScale == scale
               && previous.First == snapshot.First
               && previous.Second == snapshot.Second
               && previous.IndicatorTop == snapshot.IndicatorTop
               && previous.IndicatorHeight == snapshot.IndicatorHeight;

        private void StartLane(ShapeVisual visual, CompositionRoundedRectangleGeometry shape,
            IndeterminateBarSnapshot snapshot, IndicatorLane lane, float radius, float scale,
            float holdUntil, float arriveAt)
        {
            Compositor c = Host.Compositor;
            var size = new Vector2((float)(lane.Width * scale), (float)(snapshot.IndicatorHeight * scale));
            visual.Size = size;
            shape.Size = size;
            shape.CornerRadius = new Vector2(radius, radius);

            float origin = (float)(lane.Origin * scale);
            float start = origin + (float)(lane.Start * scale);
            float end = origin + (float)(lane.End * scale);
            visual.Offset = new Vector3(start, (float)(snapshot.IndicatorTop * scale), 0);

            CubicBezierEasingFunction easing = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));
            ScalarKeyFrameAnimation animation = c.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, start);
            if (holdUntil > 0)
                animation.InsertKeyFrame(holdUntil, start, easing);
            animation.InsertKeyFrame(arriveAt, end, easing);
            if (arriveAt < 1)
                animation.InsertKeyFrame(1f, end, easing);
            animation.Duration = Cycle;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Offset.X", animation);
        }

        public void Dispose()
        {
            Host.Root.Children.Remove(_clip);
            _clip.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    private const int DqTypeThreadCurrent = 2;
    private const int DqtatComNone = 0;

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);

    [GeneratedComInterface]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(nint hwndTarget, int isTopmost, out nint result);

        [PreserveSig]
        int EnsureOnThread(uint threadId);
    }
}
#endif
