using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.Controls;

public class HyperlinkText : SelectableTextBlock
{
    private static readonly TimeSpan DefaultDoubleClickTime = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _pendingActivation = new();
    private Point _pressOrigin;
    private bool _tracking;

    public event EventHandler? Activated;

    public HyperlinkText()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        TextDecorations = global::Avalonia.Media.TextDecorations.Underline;
        TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis;
        // Without a background only the glyph pixels are hit-testable, so drags, multi-clicks and right-clicks miss.
        Background = global::Avalonia.Media.Brushes.Transparent;

        AddHandler(PointerPressedEvent, OnLinkPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnLinkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _pendingActivation.Tick += (_, _) =>
        {
            _pendingActivation.Stop();
            Activate();
        };
    }

    // Themes match by exact type; without this the SelectableTextBlock theme (and its selection brush) is skipped.
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pendingActivation.Stop();
        _tracking = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.None && e.Key is Key.Enter or Key.Space)
        {
            Activate();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new HyperlinkTextAutomationPeer(this);

    /// <summary>Activate on a clean click, but let a press-and-drag run the text selection instead.</summary>
    private void OnLinkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pendingActivation.Stop();
        if (e.ClickCount > 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _tracking = false;
            return;
        }

        _tracking = true;
        _pressOrigin = e.GetPosition(this);
        _pendingActivation.Interval =
            Application.Current?.PlatformSettings?.GetDoubleTapTime(e.Pointer.Type) is { } doubleClickTime
            && doubleClickTime > TimeSpan.Zero
                ? doubleClickTime
                : DefaultDoubleClickTime;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // Once the pointer leaves the click threshold, the gesture is a selection even if it comes back.
        if (_tracking && IsBeyondClickThreshold(e.GetPosition(this)))
            _tracking = false;
    }

    private bool IsBeyondClickThreshold(Point point)
    {
        var moved = point - _pressOrigin;
        return Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4;
    }

    private void OnLinkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tracking || e.InitialPressMouseButton is not MouseButton.Left)
            return;

        _tracking = false;
        if (!IsBeyondClickThreshold(e.GetPosition(this)))
            _pendingActivation.Start();
    }
}

public class HyperlinkTextAutomationPeer : TextBlockAutomationPeer, IInvokeProvider
{
    public HyperlinkTextAutomationPeer(HyperlinkText owner) : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Hyperlink;

    void IInvokeProvider.Invoke()
    {
        EnsureEnabled();
        Dispatcher.UIThread.Post(((HyperlinkText)Owner).Activate);
    }
}
