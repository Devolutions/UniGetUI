using Avalonia;
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
    private static readonly TimeSpan DefaultDoubleClickTime = TimeSpan.FromMilliseconds(500);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    private sealed record Link(int Start, int Length, string? Url, Action Activate);

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
        _copySelectionItem.Click += (_, _) => Copy();
        _copyLinkItem = new MenuItem { Header = CoreTools.Translate("Copy link") };
        _copyLinkItem.Click += async (_, _) => await CopyUrlToClipboardAsync(_contextLink?.Url);
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

    public void Clear()
    {
        Inlines!.Clear();
        _links.Clear();
        _tips.Clear();
        _length = 0;
        _atLineStart = false;
        ClearSelection();
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
        _links.Add(new Link(start, text.Length, url, activate));
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
        _pendingActivation.Stop();
        _pressedLink = null;
        _activationLink = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Activate a link on a clean click, but let a drag or a multi-click run the text selection instead.</summary>
    private void OnLinkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pendingActivation.Stop();
        _activationLink = null;
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            _contextLink = LinkAt(point.Position);
            return;
        }

        if (e.ClickCount > 1 || !point.Properties.IsLeftButtonPressed)
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
        var moved = e.GetPosition(this) - _pressOrigin;
        if (Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4)
            return;

        // A plain click on a link should not leave a caret behind in the text.
        ClearSelection();
        _activationLink = link;
        _pendingActivation.Start();
    }

    private async Task CopyUrlToClipboardAsync(string? url)
    {
        if (string.IsNullOrEmpty(url) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        try
        {
            await clipboard.SetTextAsync(url);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not copy the link to the clipboard");
            Logger.Warn(ex);
        }
    }
}
