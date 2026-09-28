using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>
/// Base class for every TUI dialog. Dialogs are ordinary controls hosted in the main window's overlay
/// layer (<see cref="TuiModal"/>), never Avalonia popups or managed windows: popup roots capture the
/// mouse and keyboard unreliably across Windows console hosts and PTYs, while an in-tree overlay
/// behaves the same everywhere, including the headless test console.
/// </summary>
internal abstract class TuiDialog : UserControl
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Border _frame;
    private readonly WrapPanel _buttons;

    protected TuiDialog(string title, double? width = null)
    {
        Title = title;
        // Wraps onto a second line when the buttons are wider than the dialog, instead of running past the
        // frame (and over the body's scrollbar).
        _buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 2,
            ItemsAlignment = WrapPanelItemsAlignment.End,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 0, 0),
        };

        Body = new ContentControl();
        var layout = new DockPanel { LastChildFill = true };
        var titleBlock = new TextBlock
        {
            Text = $" {title} ",
            FontWeight = FontWeight.Bold,
            Foreground = TuiPalette.ChromeText,
            Background = TuiPalette.Chrome,
            Margin = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(titleBlock, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        layout.Children.Add(titleBlock);
        layout.Children.Add(_buttons);
        layout.Children.Add(Body);

        _frame = new Border
        {
            Background = TuiPalette.Surface,
            BorderBrush = TuiPalette.Brand,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = layout,
        };
        if (width is double w) _frame.Width = w;
        Content = _frame;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
    }

    public string Title { get; }

    /// <summary>The dialog's main content area.</summary>
    protected ContentControl Body { get; }

    public Task<object?> Completion => _completion.Task;

    public bool IsClosed => _completion.Task.IsCompleted;

    /// <summary>Sets the maximum height of the dialog frame (defaults to the host size).</summary>
    protected void SetFrameHeight(double height) => _frame.Height = height;

    protected void SetFrameWidth(double width) => _frame.Width = width;

    protected Button AddButton(string label, Action onClick)
    {
        var button = new Button { Content = label, Padding = new Thickness(1, 0, 1, 0) };
        button.Click += (_, _) => onClick();
        _buttons.Children.Add(button);
        return button;
    }

    public void Close(object? result)
    {
        if (_completion.Task.IsCompleted) return;
        TuiModal.OnDialogClosed(this);
        _completion.TrySetResult(result);
    }

    /// <summary>Esc behaviour. Returning false lets a focused control consume Esc first.</summary>
    public virtual bool OnEscape()
    {
        Close(null);
        return true;
    }

    /// <summary>Called for every key while the dialog is on top, before the focused control sees it.</summary>
    public virtual void OnPreviewKey(KeyEventArgs e) { }

    /// <summary>Seats keyboard focus inside the dialog when it opens.</summary>
    public virtual void FocusInitial()
    {
        var first = this.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
        first?.Focus();
    }
}

/// <summary>Stack of open dialogs rendered over the main window.</summary>
internal static class TuiModal
{
    private static readonly List<TuiDialog> _stack = [];
    private static Panel? _layer;
    private static readonly Dictionary<TuiDialog, IInputElement?> _previousFocus = new();

    public static event Action? Changed;

    public static bool IsOpen => _stack.Count > 0;

    public static TuiDialog? Top => _stack.Count > 0 ? _stack[^1] : null;

    public static IReadOnlyList<TuiDialog> OpenDialogs => _stack.ToArray();

    /// <summary>Called once by the main window with the panel dialogs are drawn in.</summary>
    public static void Attach(Panel layer)
    {
        _layer = layer;
        _stack.Clear();
        _previousFocus.Clear();
    }

    public static async Task<object?> ShowAsync(TuiDialog dialog)
    {
        if (_layer is null) throw new InvalidOperationException("No modal layer is attached.");
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync<Task<object?>>(() => ShowAsync(dialog)).GetTask().Unwrap();

        var top = TopLevel.GetTopLevel(_layer);
        _previousFocus[dialog] = top?.FocusManager?.GetFocusedElement();

        var shade = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Child = dialog,
        };
        shade.PointerPressed += (_, e) => e.Handled = true;
        _stack.Add(dialog);
        _layer.Children.Add(shade);
        _layer.IsVisible = true;
        Changed?.Invoke();

        Dispatcher.UIThread.Post(dialog.FocusInitial, DispatcherPriority.Loaded);
        return await dialog.Completion;
    }

    public static async Task<T?> ShowAsync<T>(TuiDialog dialog)
        => await ShowAsync(dialog) is T value ? value : default;

    internal static void OnDialogClosed(TuiDialog dialog)
    {
        int index = _stack.IndexOf(dialog);
        if (index < 0 || _layer is null) return;
        _stack.RemoveAt(index);
        var shade = _layer.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Child, dialog));
        if (shade is not null) _layer.Children.Remove(shade);
        _layer.IsVisible = _stack.Count > 0;

        _previousFocus.Remove(dialog, out IInputElement? previous);
        Dispatcher.UIThread.Post(() =>
        {
            // Another dialog may have opened in the meantime (a menu action opening its own dialog): it owns focus.
            if (Top is { } next) next.FocusInitial();
            else if (previous is InputElement element && element.IsAttachedToVisualTree()) element.Focus();
        }, DispatcherPriority.Loaded);

        Changed?.Invoke();
    }

    /// <summary>Routes a key to the top dialog. Returns true when the key was consumed.</summary>
    public static bool HandleKey(KeyEventArgs e)
    {
        if (Top is not { } dialog) return false;
        dialog.OnPreviewKey(e);
        if (e.Handled) return true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (dialog.OnEscape()) e.Handled = true;
            return e.Handled;
        }

        // Keep focus inside the dialog: if it escaped (e.g. to a control behind the shade), pull it back.
        var focused = TopLevel.GetTopLevel(dialog)?.FocusManager?.GetFocusedElement() as Visual;
        if (focused is null || !dialog.IsVisualAncestorOf(focused))
            dialog.FocusInitial();
        return false;
    }

    /// <summary>Closes every dialog (used when the app shuts down or tests reset).</summary>
    public static void CloseAll()
    {
        foreach (var dialog in _stack.ToArray()) dialog.Close(null);
    }
}
