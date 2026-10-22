using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls;

public class HyperlinkText : SelectableTextBlock
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<HyperlinkText, string?>(nameof(Url));

    private static readonly TimeSpan DefaultDoubleClickTime = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _pendingActivation = new();
    private Point _pressOrigin;
    private bool _tracking;

    public event EventHandler? Activated;

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public HyperlinkText()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        TextDecorations = global::Avalonia.Media.TextDecorations.Underline;
        TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis;

        AddHandler(PointerPressedEvent, OnLinkPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnLinkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _pendingActivation.Tick += (_, _) =>
        {
            _pendingActivation.Stop();
            Activate();
        };
    }

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);

    private async Task CopyUrlToClipboardAsync()
    {
        if (string.IsNullOrEmpty(Url) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(Url);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != UrlProperty)
            return;

        if (string.IsNullOrEmpty(Url))
            ClearValue(ContextFlyoutProperty);
        else
            ContextFlyout = BuildLinkContextFlyout();
    }

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

    private void OnLinkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_tracking || e.InitialPressMouseButton is not MouseButton.Left)
            return;

        _tracking = false;
        var moved = e.GetPosition(this) - _pressOrigin;
        if (Math.Abs(moved.X) <= 4 && Math.Abs(moved.Y) <= 4)
            _pendingActivation.Start();
    }

    private MenuFlyout BuildLinkContextFlyout()
    {
        var copySelection = new MenuItem { Header = CoreTools.Translate("Copy") };
        copySelection.Click += (_, _) => Copy();
        var copyLink = new MenuItem { Header = CoreTools.Translate("Copy link") };
        copyLink.Click += async (_, _) => await CopyUrlToClipboardAsync();

        var flyout = new MenuFlyout();
        flyout.Items.Add(copySelection);
        flyout.Items.Add(copyLink);
        flyout.Opening += (_, _) => copySelection.IsEnabled = CanCopy;
        return flyout;
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
