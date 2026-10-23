using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>
/// A single selectable block of rich text whose links live inside the text, so one selection
/// can run across labels, values and links like on a web page.
/// </summary>
public class LinkTextBlock : SelectableTextBlock
{
    private const double DragThreshold = 4;
    private static readonly TimeSpan DefaultDoubleClickTime = TimeSpan.FromMilliseconds(500);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    internal sealed record Link(int Start, int Length, string Text, string? Url, Action Activate);

    private sealed record Tip(int Start, int Length, string Text);

    private readonly List<Link> _links = [];
    private readonly List<Tip> _tips = [];
    private readonly DispatcherTimer _pendingActivation = new();
    private readonly MenuItem _copyLinkItem;
    private readonly MenuItem _copySelectionItem;
    private Link? _pressedLink;
    private Link? _contextLink;
    private Link? _activationLink;
    private Point _pressOrigin;
    private int _focusedLink = -1;

    // Positions are counted here rather than read from Inlines.Text: they must match the TextLayout.
    private int _length;
    private bool _atLineStart;

    public LinkTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        // Without a background only the glyph pixels are hit-testable, so drags between words miss.
        Background = Brushes.Transparent;
        Inlines = new InlineCollection();

        // Tunnel, so the hit-test runs before the base class changes the selection, which drops the text layout.
        AddHandler(PointerPressedEvent, OnLinkPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnLinkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _pendingActivation.Tick += (_, _) =>
        {
            _pendingActivation.Stop();
            _activationLink?.Activate();
            _activationLink = null;
        };

        _copySelectionItem = new MenuItem { Header = CoreTools.Translate("Copy") };
        _copySelectionItem.Click += async (_, _) => await CopyToClipboardAsync(SelectedText);
        _copyLinkItem = new MenuItem { Header = CoreTools.Translate("Copy link") };
        _copyLinkItem.Click += async (_, _) => await CopyToClipboardAsync(_contextLink?.Url);
        var flyout = new MenuFlyout();
        flyout.Items.Add(_copySelectionItem);
        flyout.Items.Add(_copyLinkItem);
        flyout.Opening += (_, _) =>
        {
            _copySelectionItem.IsEnabled = CanCopy;
            _copyLinkItem.IsVisible = !string.IsNullOrEmpty(_contextLink?.Url);
        };
        ContextFlyout = flyout;
    }

    // Themes match by exact type; without this the SelectableTextBlock theme (and its selection brush) is skipped.
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    internal IReadOnlyList<Link> Links => _links;

    public void Clear()
    {
        ResetLinkInteraction();
        Inlines!.Clear();
        _links.Clear();
        _tips.Clear();
        _length = 0;
        _atLineStart = false;
        ClearSelection();
        (ControlAutomationPeer.FromElement(this) as LinkTextBlockAutomationPeer)?.LinksChanged();
    }

    /// <summary>Starts a new line, unless the block is empty or already at the start of a line.</summary>
    public void StartLine()
    {
        if (_length > 0 && !_atLineStart)
            AddLineBreak();
    }

    public void AddRun(Run run, string? tooltip = null)
    {
        _atLineStart = false;
        int start = _length;
        Inlines!.Add(run);
        _length += run.Text?.Length ?? 0;
        if (tooltip is not null)
            _tips.Add(new Tip(start, _length - start, tooltip));
    }

    public void AddLineBreak()
    {
        Inlines!.Add(new LineBreak());
        _length += Environment.NewLine.Length;
        _atLineStart = true;
    }

    /// <summary>Adds a link. A URL also enables "Copy link"; leave it null for an action-only link.</summary>
    public void AddLink(string text, string? url, Action activate, IBrush foreground, FontWeight? fontWeight = null)
    {
        var run = new Run(text)
        {
            Foreground = foreground,
            TextDecorations = global::Avalonia.Media.TextDecorations.Underline,
        };
        if (fontWeight is { } weight)
            run.FontWeight = weight;

        int start = _length;
        AddRun(run);
        _links.Add(new Link(start, text.Length, text, url, activate));
        (ControlAutomationPeer.FromElement(this) as LinkTextBlockAutomationPeer)?.LinksChanged();
    }

    /// <summary>The link the keyboard is on: the one Tab last moved to, while it is still selected.</summary>
    private Link? FocusedLink =>
        _focusedLink >= 0 && _focusedLink < _links.Count
        && _links[_focusedLink] is var link
        && Math.Min(SelectionStart, SelectionEnd) == link.Start
        && Math.Max(SelectionStart, SelectionEnd) == link.Start + link.Length
            ? link
            : null;

    internal bool IsLinkFocused(Link link) => IsFocused && FocusedLink == link;

    /// <summary>Moves keyboard focus to a link by selecting it, so the selection highlight shows where focus is.</summary>
    internal void FocusLink(Link link)
    {
        int index = _links.IndexOf(link);
        if (index < 0)
            return;
        // Focus first: OnGotFocus runs synchronously and selects the first link on a Tab focus.
        if (!IsFocused)
            Focus(NavigationMethod.Tab);
        _focusedLink = index;
        SetCurrentValue(SelectionStartProperty, link.Start);
        SetCurrentValue(SelectionEndProperty, link.Start + link.Length);
        if (TextLayout.HitTestTextRange(link.Start, link.Length).FirstOrDefault() is var bounds && bounds != default)
            this.BringIntoView(bounds.Translate(new Vector(Padding.Left, Padding.Top)));
    }

    /// <summary>The bounds of a link, in this control's coordinates, for automation.</summary>
    internal Rect LinkBounds(Link link)
    {
        var rects = TextLayout.HitTestTextRange(link.Start, link.Length).ToList();
        if (rects.Count == 0)
            return default;
        var union = rects.Aggregate((a, b) => a.Union(b));
        return union.Translate(new Vector(Padding.Left, Padding.Top));
    }

    private void ResetLinkInteraction()
    {
        _pendingActivation.Stop();
        _pressedLink = null;
        _activationLink = null;
        _contextLink = null;
        _focusedLink = -1;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LinkTextBlockAutomationPeer(this);

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.NavigationMethod == NavigationMethod.Tab && _links.Count > 0 && FocusedLink is null)
            FocusLink(_links[e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _links.Count - 1 : 0]);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = e.KeyModifiers is KeyModifiers.Shift;
        if (e.Key is Key.Tab && (e.KeyModifiers is KeyModifiers.None || shift) && _links.Count > 0)
        {
            // Tab steps through the links, then lets focus leave the block past the first or last one.
            int next = FocusedLink is null ? (shift ? _links.Count - 1 : 0) : _focusedLink + (shift ? -1 : 1);
            if (next >= 0 && next < _links.Count)
            {
                FocusLink(_links[next]);
                e.Handled = true;
                return;
            }

            _focusedLink = -1;
        }
        else if (e.KeyModifiers is KeyModifiers.None && e.Key is Key.Enter or Key.Space && FocusedLink is { } link)
        {
            link.Activate();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private Link? LinkAt(int position) =>
        _links.FirstOrDefault(l => position >= l.Start && position < l.Start + l.Length);

    private Link? LinkAt(Point point)
    {
        return CharacterAt(point) is { } position ? LinkAt(position) : null;
    }

    private Tip? TipAt(Point point) =>
        CharacterAt(point) is { } position
            ? _tips.FirstOrDefault(t => position >= t.Start && position < t.Start + t.Length)
            : null;

    /// <summary>The character under the point, or null past the end of a line.</summary>
    private int? CharacterAt(Point point)
    {
        // TextPosition is the nearest caret, so use the hit character instead. IsInside is not used
        // either: it is false on every line after the first, so check the character's own box.
        var local = point - new Point(Padding.Left, Padding.Top);
        int index = TextLayout.HitTestPoint(local).CharacterHit.FirstCharacterIndex;
        return TextLayout.HitTestTextPosition(index).Contains(local) ? index : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        // Hit-test before the base class extends the selection, which drops the text layout.
        var point = e.GetPosition(this);
        bool selecting = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        Cursor = !selecting && LinkAt(point) is not null ? HandCursor : TextCursor;
        // Once the pointer leaves the click threshold, the gesture is a selection even if it comes back.
        if (_pressedLink is not null && IsBeyondDragThreshold(point))
            _pressedLink = null;
        ToolTip.SetTip(this, selecting ? null : TipAt(point)?.Text);
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ResetLinkInteraction();
        base.OnDetachedFromVisualTree(e);
    }

    private bool IsBeyondDragThreshold(Point point)
    {
        var moved = point - _pressOrigin;
        return Math.Abs(moved.X) > DragThreshold || Math.Abs(moved.Y) > DragThreshold;
    }

    /// <summary>Activate a link on a clean click, but let a drag or a multi-click run the text selection instead.</summary>
    private void OnLinkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pendingActivation.Stop();
        _activationLink = null;
        _focusedLink = -1;
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            _contextLink = LinkAt(point.Position);
            return;
        }

        // Shift+click extends the selection, so it must not open a link at its endpoint.
        if (e.ClickCount > 1 || !point.Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _pressedLink = null;
            return;
        }

        _pressedLink = LinkAt(point.Position);
        _pressOrigin = point.Position;
        _pendingActivation.Interval =
            Application.Current?.PlatformSettings?.GetDoubleTapTime(e.Pointer.Type) is { } doubleClickTime
            && doubleClickTime > TimeSpan.Zero
                ? doubleClickTime
                : DefaultDoubleClickTime;
    }

    private void OnLinkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedLink is not { } link || e.InitialPressMouseButton is not MouseButton.Left)
            return;

        _pressedLink = null;
        if (IsBeyondDragThreshold(e.GetPosition(this)))
            return;

        // A plain click on a link should not leave a caret behind in the text.
        ClearSelection();
        _activationLink = link;
        _pendingActivation.Start();
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not copy the text to the clipboard");
            Logger.Warn(ex);
        }
    }
}

public class LinkTextBlockAutomationPeer : TextBlockAutomationPeer
{
    private readonly Dictionary<LinkTextBlock.Link, LinkAutomationPeer> _peers = [];

    public LinkTextBlockAutomationPeer(LinkTextBlock owner) : base(owner)
    {
    }

    public new LinkTextBlock Owner => (LinkTextBlock)base.Owner;

    internal void LinksChanged()
    {
        foreach (var stale in _peers.Keys.Except(Owner.Links).ToList())
            _peers.Remove(stale);
        InvalidateChildren();
    }

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() =>
        Owner.Links.Select(link => (AutomationPeer)(_peers.TryGetValue(link, out var peer)
            ? peer
            : _peers[link] = new LinkAutomationPeer(Owner, link))).ToList();
}

/// <summary>Exposes one link inside a <see cref="LinkTextBlock"/> to screen readers as an invokable hyperlink.</summary>
public class LinkAutomationPeer : AutomationPeer, IInvokeProvider
{
    private readonly LinkTextBlock _owner;
    private readonly LinkTextBlock.Link _link;
    private AutomationPeer? _parent;

    internal LinkAutomationPeer(LinkTextBlock owner, LinkTextBlock.Link link)
    {
        _owner = owner;
        _link = link;
    }

    void IInvokeProvider.Invoke()
    {
        EnsureEnabled();
        Dispatcher.UIThread.Post(_link.Activate);
    }

    protected override void BringIntoViewCore() => _owner.FocusLink(_link);
    protected override string? GetAcceleratorKeyCore() => null;
    protected override string? GetAccessKeyCore() => null;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
    protected override string? GetAutomationIdCore() => null;

    protected override Rect GetBoundingRectangleCore()
    {
        var bounds = _owner.LinkBounds(_link);
        return TopLevel.GetTopLevel(_owner) is { } root && _owner.TransformToVisual(root) is { } transform
            ? bounds.TransformToAABB(transform)
            : default;
    }

    protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
    protected override string GetClassNameCore() => "Hyperlink";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override string? GetNameCore() => _link.Text;
    protected override AutomationPeer? GetParentCore() => _parent;
    protected override bool HasKeyboardFocusCore() => _owner.IsLinkFocused(_link);
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override bool IsEnabledCore() => _owner.IsEffectivelyEnabled;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override void SetFocusCore() => _owner.FocusLink(_link);
    protected override bool ShowContextMenuCore() => false;

    protected override bool TrySetParent(AutomationPeer? parent)
    {
        _parent = parent;
        return true;
    }
}
