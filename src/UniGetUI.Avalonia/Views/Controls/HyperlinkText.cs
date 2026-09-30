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
    private Point _pressOrigin;
    private bool _tracking;

    public event EventHandler? Activated;

    public HyperlinkText()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        TextDecorations = global::Avalonia.Media.TextDecorations.Underline;
        TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis;

        AddHandler(PointerPressedEvent, OnLinkPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnLinkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);

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
        if (e.ClickCount > 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _tracking = false;
            return;
        }

        _tracking = true;
        _pressOrigin = e.GetPosition(this);
    }

    private void OnLinkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tracking || e.InitialPressMouseButton is not MouseButton.Left)
            return;

        _tracking = false;
        var moved = e.GetPosition(this) - _pressOrigin;
        if (Math.Abs(moved.X) <= 4 && Math.Abs(moved.Y) <= 4)
            Activate();
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
