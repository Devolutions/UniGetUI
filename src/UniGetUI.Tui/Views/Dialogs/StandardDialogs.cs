using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>A message with a row of buttons. Returns the index of the pressed button, or null on Esc.</summary>
internal sealed class MessageDialog : TuiDialog
{
    private readonly List<Button> _buttonControls = [];
    private readonly int _defaultButton;

    public MessageDialog(string title, string message, IReadOnlyList<string> buttons, int defaultButton = 0)
        : base(title, width: Math.Clamp(Math.Max(message.Split('\n').Max(l => l.Length) + 6, title.Length + 8), 40, 90))
    {
        _defaultButton = defaultButton;
        Body.Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            _buttonControls.Add(AddButton(buttons[i], () => Close(index)));
        }
    }

    public override void FocusInitial()
    {
        if (_buttonControls.Count > 0) _buttonControls[Math.Clamp(_defaultButton, 0, _buttonControls.Count - 1)].Focus();
    }

    public override void OnPreviewKey(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right && _buttonControls.Count > 1)
        {
            int current = _buttonControls.FindIndex(b => b.IsFocused);
            int next = e.Key == Key.Left ? current - 1 : current + 1;
            _buttonControls[(next + _buttonControls.Count) % _buttonControls.Count].Focus();
            e.Handled = true;
        }
    }
}

/// <summary>A single-line text prompt. Returns the text, or null on Esc/Cancel.</summary>
internal sealed class TextInputDialog : TuiDialog
{
    private readonly TextBox _box;
    private readonly Func<string, string?>? _validate;
    private readonly TextBlock _error;

    public TextInputDialog(string title, string prompt, string initial = "", string? watermark = null,
        Func<string, string?>? validate = null)
        : base(title, width: 70)
    {
        _validate = validate;
        _box = new TextBox { Text = initial, PlaceholderText = watermark ?? string.Empty, AcceptsReturn = false };
        _box.AddHandler(TextInputEvent, (_, e) => TuiInputGuard.HandleTextInput(_box, e, title, 4096),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _error = new TextBlock { Foreground = TuiPalette.Error, IsVisible = false };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) });
        panel.Children.Add(_box);
        panel.Children.Add(_error);
        Body.Content = panel;
        AddButton("OK", Submit);
        AddButton("Cancel", () => Close(null));
    }

    public override void FocusInitial()
    {
        _box.Focus();
        _box.CaretIndex = _box.Text?.Length ?? 0;
    }

    public override void OnPreviewKey(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && _box.IsFocused)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        string text = _box.Text ?? string.Empty;
        if (_validate?.Invoke(text) is { } error)
        {
            _error.Text = error;
            _error.IsVisible = true;
            return;
        }

        Close(text);
    }
}

/// <summary>One entry of a <see cref="ChoiceDialog"/>.</summary>
internal sealed record TuiChoice(string Label, bool Enabled = true, string? Hint = null, object? Tag = null)
{
    public static TuiChoice Separator { get; } = new("─────────────", Enabled: false);

    public override string ToString() => Hint is null ? Label : $"{Label}   {Hint}";
}

/// <summary>
/// A vertical list of choices (menus, pickers, context menus). Returns the index of the chosen entry,
/// or null on Esc. Disabled entries are shown dimmed and can't be picked. Typing the underlined-free
/// first letter jumps to the next matching entry.
/// </summary>
internal sealed class ChoiceDialog : TuiDialog
{
    private readonly ListBox _list;
    private readonly IReadOnlyList<TuiChoice> _choices;

    public ChoiceDialog(string title, IReadOnlyList<TuiChoice> choices, int selected = 0, string? message = null)
        : base(title, width: Math.Clamp(choices.Select(c => c.ToString().Length).DefaultIfEmpty(20).Max() + 8, title.Length + 8, 90))
    {
        _choices = choices;
        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemsSource = choices,
            MaxHeight = 24,
            ItemTemplate = new FuncDataTemplate<TuiChoice>((choice, _) => new TextBlock
            {
                Text = choice?.ToString() ?? string.Empty,
                Foreground = choice?.Enabled == false
                    ? TuiPalette.TextDim
                    : TuiPalette.Text,
            }),
        };
        _list.SelectedIndex = FirstEnabledFrom(Math.Clamp(selected, 0, Math.Max(0, choices.Count - 1)), 1);
        _list.DoubleTapped += (_, _) => Pick();

        if (message is null)
        {
            Body.Content = _list;
        }
        else
        {
            var panel = new DockPanel();
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1) };
            DockPanel.SetDock(text, Dock.Top);
            panel.Children.Add(text);
            panel.Children.Add(_list);
            Body.Content = panel;
        }
    }

    public override void FocusInitial() => TuiFocus.SeatListFocus(_list);

    public string SelectedLabel => _list.SelectedItem is TuiChoice c ? c.Label : string.Empty;

    public override void OnPreviewKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter or Key.Return or Key.Space:
                Pick();
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Home:
                Select(FirstEnabledFrom(0, 1));
                e.Handled = true;
                break;
            case Key.End:
                Select(FirstEnabledFrom(_choices.Count - 1, -1));
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        char c = char.ToLowerInvariant(e.Text[0]);
        for (int step = 1; step <= _choices.Count; step++)
        {
            int i = (_list.SelectedIndex + step) % _choices.Count;
            if (_choices[i].Enabled && _choices[i].Label.Length > 0 && char.ToLowerInvariant(_choices[i].Label[0]) == c)
            {
                Select(i);
                e.Handled = true;
                return;
            }
        }
    }

    private void Move(int delta)
    {
        if (_choices.Count == 0) return;
        int i = _list.SelectedIndex;
        for (int step = 0; step < _choices.Count; step++)
        {
            i = (i + delta + _choices.Count) % _choices.Count;
            if (_choices[i].Enabled)
            {
                Select(i);
                return;
            }
        }
    }

    private void Select(int index)
    {
        if (index < 0) return;
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => (_list.ContainerFromIndex(index) as Control)?.Focus(), DispatcherPriority.Loaded);
    }

    private int FirstEnabledFrom(int start, int direction)
    {
        for (int i = start; i >= 0 && i < _choices.Count; i += direction)
            if (_choices[i].Enabled) return i;
        return -1;
    }

    private void Pick()
    {
        int i = _list.SelectedIndex;
        if (i >= 0 && i < _choices.Count && _choices[i].Enabled) Close(i);
    }
}

/// <summary>A scrollable read-only text viewer with copy and export actions. Logs open at the end.</summary>
internal sealed class TextViewerDialog : TuiDialog
{
    private readonly ScrollViewer _scroll;
    private readonly string _text;

    public TextViewerDialog(string title, string text, IReadOnlyList<(string Label, Action Action)>? extraButtons = null,
        bool scrollToEnd = false)
        : base(title)
    {
        _text = text;
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        _scroll = new ScrollViewer { Content = block, Focusable = true };
        if (scrollToEnd) _scroll.Loaded += (_, _) => Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Loaded);
        Body.Content = _scroll;
        SetFrameWidth(100);
        SetFrameHeight(30);
        AddButton("Copy", () => _ = TuiClipboard.CopyAsync(this, title, _text));
        AddButton("Export…", () => _ = ExportAsync());
        foreach (var (label, action) in extraButtons ?? [])
            AddButton(label, action);
        AddButton("Close", () => Close(null));
    }

    public override void FocusInitial() => _scroll.Focus();

    private async Task ExportAsync()
    {
        string? path = await TuiPrompts.AskTextAsync("Export", "Save to file:",
            Path.Join(UniGetUI.Core.Data.CoreData.UniGetUIDataDirectory, "Exports", "export.txt"));
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, _text);
            TuiNotifications.Success("Exported", path);
        }
        catch (Exception ex)
        {
            TuiNotifications.Error("Export failed", ex.Message);
        }
    }
}

/// <summary>Convenience wrappers over the standard dialogs.</summary>
internal static class TuiPrompts
{
    public static async Task<bool> ConfirmAsync(string title, string message, string yes = "Yes", string no = "No")
        => await TuiModal.ShowAsync(new MessageDialog(title, message, [yes, no])) is 0;

    public static Task InfoAsync(string title, string message)
        => TuiModal.ShowAsync(new MessageDialog(title, message, ["OK"]));

    public static async Task<string?> AskTextAsync(string title, string prompt, string initial = "",
        Func<string, string?>? validate = null)
        => await TuiModal.ShowAsync(new TextInputDialog(title, prompt, initial, validate: validate)) as string;

    public static async Task<int?> ChooseAsync(string title, IReadOnlyList<TuiChoice> choices, int selected = 0,
        string? message = null)
        => await TuiModal.ShowAsync(new ChoiceDialog(title, choices, selected, message)) as int?;

    public static Task ShowTextAsync(string title, string text,
        IReadOnlyList<(string Label, Action Action)>? extraButtons = null, bool scrollToEnd = false)
        => TuiModal.ShowAsync(new TextViewerDialog(title, text, extraButtons, scrollToEnd));
}
