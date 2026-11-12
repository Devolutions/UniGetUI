#if WINDOWS
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia.Controls;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using Rect = Avalonia.Rect;

namespace UniGetUI.Avalonia.Infrastructure;

internal static partial class CompositorProgressBars
{
    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(2);
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private static nint _dispatcherQueueController;
    private static Compositor? _compositor;

    public static bool IsSupported()
        => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
           && !Design.IsDesignMode
           && !WindowsAvaloniaRenderingPolicy.ShouldUseSoftwareRendering;

    public static IIndeterminateBarSurface? CreateSurface(Window window)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0)
            return null;

        Compositor compositor = GetCompositor();
        return new Surface(compositor, CreateTopmostTarget(compositor, hwnd));
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

    private sealed class Surface : IIndeterminateBarSurface
    {
        private readonly Compositor _compositor;
        private readonly DesktopWindowTarget _target;
        private readonly ContainerVisual _root;

        public Surface(Compositor compositor, DesktopWindowTarget target)
        {
            _compositor = compositor;
            _target = target;
            _root = compositor.CreateContainerVisual();
            _target.Root = _root;
        }

        public IIndeterminateBarLayer CreateLayer() => new Layer(_compositor, _root);

        public void Dispose()
        {
            _target.Root = null;
            _root.Dispose();
            _target.Dispose();
        }
    }

    private sealed class Layer : IIndeterminateBarLayer
    {
        private readonly Compositor _compositor;
        private readonly ContainerVisual _root;
        private readonly ContainerVisual _clip;
        private readonly ContainerVisual _track;
        private readonly CompositionRoundedRectangleGeometry _trackShape;
        private readonly CompositionColorBrush _brush;
        private readonly Indicator _first;
        private readonly Indicator _second;
        private IndeterminateBarSnapshot? _animated;
        private double _animatedScale;

        public Layer(Compositor compositor, ContainerVisual root)
        {
            _compositor = compositor;
            _root = root;

            _clip = compositor.CreateContainerVisual();
            _clip.Clip = compositor.CreateInsetClip();
            _clip.IsVisible = false;

            _trackShape = compositor.CreateRoundedRectangleGeometry();
            _track = compositor.CreateContainerVisual();
            _track.Clip = compositor.CreateGeometricClip(_trackShape);
            _clip.Children.InsertAtTop(_track);

            _brush = compositor.CreateColorBrush();
            _first = new Indicator(compositor, _brush);
            _second = new Indicator(compositor, _brush);
            _track.Children.InsertAtTop(_first.Visual);
            _track.Children.InsertAtTop(_second.Visual);

            root.Children.InsertAtTop(_clip);
        }

        public void Show(IndeterminateBarSnapshot snapshot, double scale)
        {
            float s = (float)scale;
            Rect clip = snapshot.Clip;
            _clip.Offset = new Vector3((float)(clip.X * s), (float)(clip.Y * s), 0);
            _clip.Size = new Vector2((float)(clip.Width * s), (float)(clip.Height * s));
            _clip.Opacity = (float)snapshot.Opacity;

            Rect track = snapshot.Track;
            _track.Offset = new Vector3((float)((track.X - clip.X) * s), (float)((track.Y - clip.Y) * s), 0);
            _track.Size = new Vector2((float)(track.Width * s), (float)(track.Height * s));
            ApplyShape(_trackShape, snapshot.TrackShape, s);

            _brush.Color = Windows.UI.Color.FromArgb(255, snapshot.Color.R, snapshot.Color.G, snapshot.Color.B);

            if (!SameAnimation(snapshot, scale))
            {
                _first.Start(_compositor, snapshot, snapshot.First, snapshot.FirstShape, s, 0f, 0.75f);
                _second.Start(_compositor, snapshot, snapshot.Second, snapshot.SecondShape, s, 0.375f, 1f);
                _animated = snapshot;
                _animatedScale = scale;
            }

            _clip.IsVisible = true;
        }

        public void Hide() => _clip.IsVisible = false;

        private bool SameAnimation(IndeterminateBarSnapshot snapshot, double scale)
            => _animated is { } previous
               && _animatedScale == scale
               && previous.First == snapshot.First
               && previous.Second == snapshot.Second
               && previous.FirstShape == snapshot.FirstShape
               && previous.SecondShape == snapshot.SecondShape
               && previous.IndicatorTop == snapshot.IndicatorTop
               && previous.IndicatorHeight == snapshot.IndicatorHeight;

        public void Dispose()
        {
            _root.Children.Remove(_clip);
            _clip.Dispose();
        }
    }

    private sealed class Indicator
    {
        private readonly CompositionRoundedRectangleGeometry _shape;

        public Indicator(Compositor compositor, CompositionBrush brush)
        {
            _shape = compositor.CreateRoundedRectangleGeometry();
            CompositionSpriteShape sprite = compositor.CreateSpriteShape(_shape);
            sprite.FillBrush = brush;
            Visual = compositor.CreateShapeVisual();
            Visual.Clip = compositor.CreateInsetClip();
            Visual.Shapes.Add(sprite);
        }

        public ShapeVisual Visual { get; }

        public void Start(Compositor compositor, IndeterminateBarSnapshot snapshot, IndicatorLane lane,
            RoundedShape shape, float scale, float holdUntil, float arriveAt)
        {
            Visual.Size = new Vector2((float)(lane.Width * scale), (float)(snapshot.IndicatorHeight * scale));
            ApplyShape(_shape, shape, scale);

            float origin = (float)(lane.Origin * scale);
            float start = origin + (float)(lane.Start * scale);
            float end = origin + (float)(lane.End * scale);
            Visual.Offset = new Vector3(start, (float)(snapshot.IndicatorTop * scale), 0);

            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1));
            ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, start);
            if (holdUntil > 0)
                animation.InsertKeyFrame(holdUntil, start, easing);
            animation.InsertKeyFrame(arriveAt, end, easing);
            if (arriveAt < 1)
                animation.InsertKeyFrame(1f, end, easing);
            animation.Duration = Cycle;
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            Visual.StartAnimation("Offset.X", animation);
        }
    }

    private static void ApplyShape(CompositionRoundedRectangleGeometry geometry, RoundedShape shape, float scale)
    {
        geometry.Offset = new Vector2((float)(shape.OffsetX * scale), (float)(shape.OffsetY * scale));
        geometry.Size = new Vector2((float)(shape.Width * scale), (float)(shape.Height * scale));
        float radius = (float)(shape.Radius * scale);
        geometry.CornerRadius = new Vector2(radius, radius);
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
