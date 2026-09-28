using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>A value option for <see cref="TuiForm.AddChoice"/>.</summary>
internal sealed record TuiOption(string Label, string Value);

/// <summary>
/// A keyboard-first vertical form: headers, notes, checkboxes, choice fields, text fields and buttons.
/// Up/Down move between fields, Space/Enter toggle or activate, Left/Right cycle a choice field and
/// Enter on a choice field opens a picker. Every field reads its value through a getter so
/// <see cref="Refresh"/> can re-sync the whole form after an external change.
/// </summary>
internal sealed class TuiForm : UserControl, IFocusablePage
{
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly ScrollViewer _scroll;
    private readonly List<Action> _refreshers = [];

    public TuiForm()
    {
        _scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = _scroll;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public StackPanel Rows => _rows;

    public void Clear()
    {
        _rows.Children.Clear();
        _refreshers.Clear();
    }

    /// <summary>
    /// Clears and rebuilds the form with <paramref name="build"/>, keeping keyboard focus on the field at
    /// the same position (a rebuild replaces every control, which would otherwise drop focus).
    /// </summary>
    public void Rebuild(Action build)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        List<InputElement> before = Focusables().ToList();
        int index = before.FindIndex(f => ReferenceEquals(f, focused) || (focused is not null && f.IsVisualAncestorOf(focused)));
        bool hadFocus = index >= 0 || (focused is not null && this.IsVisualAncestorOf(focused));
        Clear();
        build();
        Refresh();
        if (!hadFocus) return;
        Dispatcher.UIThread.Post(() =>
        {
            List<InputElement> after = Focusables().ToList();
            if (after.Count == 0) return;
            after[Math.Clamp(index, 0, after.Count - 1)].Focus();
        }, DispatcherPriority.Loaded);
    }

    public TextBlock AddHeader(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.Bold,
            Foreground = TuiPalette.Brand,
            Margin = new Thickness(0, _rows.Children.Count == 0 ? 0 : 1, 0, 0),
        };
        _rows.Children.Add(block);
        return block;
    }

    public TextBlock AddNote(string text, IBrush? foreground = null)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? TuiPalette.TextMuted,
            Margin = new Thickness(4, 0, 0, 0),
        };
        _rows.Children.Add(block);
        return block;
    }

    /// <summary>Adds a note whose text is recomputed on every <see cref="Refresh"/>.</summary>
    public TextBlock AddLiveNote(Func<string> text, IBrush? foreground = null)
    {
        TextBlock block = AddNote(text(), foreground);
        _refreshers.Add(() => block.Text = text());
        return block;
    }

    public TuiFormField AddCheck(string label, Func<bool> get, Action<bool> set, Func<bool>? enabled = null)
    {
        var field = new TuiFormField(TuiFieldKind.Check, label) { IsChecked = get() };
        field.Activated += () =>
        {
            if (!field.IsEffectivelyEnabled) return;
            bool value = !field.IsChecked;
            field.IsChecked = value;
            try
            {
                set(value);
            }
            catch (Exception ex)
            {
                TuiNotifications.Error(label, ex.Message);
            }

            Refresh();
        };
        _refreshers.Add(() =>
        {
            field.IsChecked = get();
            field.IsEnabled = enabled?.Invoke() ?? true;
        });
        field.IsEnabled = enabled?.Invoke() ?? true;
        _rows.Children.Add(field);
        return field;
    }

    public TuiFormField AddChoice(string label, IReadOnlyList<TuiOption> options, Func<string> get, Action<string> set,
        Func<bool>? enabled = null)
    {
        var field = new TuiFormField(TuiFieldKind.Choice, label);

        string Shown()
        {
            string value = get();
            return options.FirstOrDefault(o => o.Value == value)?.Label ?? (value.Length == 0 ? "(default)" : value);
        }

        void Apply(string value)
        {
            try
            {
                set(value);
            }
            catch (Exception ex)
            {
                TuiNotifications.Error(label, ex.Message);
            }

            Refresh();
        }

        field.Value = Shown();
        field.Stepped += delta =>
        {
            if (options.Count == 0) return;
            int i = Math.Max(0, options.ToList().FindIndex(o => o.Value == get()));
            Apply(options[(i + delta + options.Count) % options.Count].Value);
        };
        field.Activated += async () =>
        {
            int current = Math.Max(0, options.ToList().FindIndex(o => o.Value == get()));
            int? picked = await TuiPrompts.ChooseAsync(label, options.Select(o => new TuiChoice(o.Label)).ToList(), current);
            if (picked is int p) Apply(options[p].Value);
        };
        _refreshers.Add(() =>
        {
            field.Value = Shown();
            field.IsEnabled = enabled?.Invoke() ?? true;
        });
        field.IsEnabled = enabled?.Invoke() ?? true;
        _rows.Children.Add(field);
        return field;
    }

    /// <summary>A labelled single-line text field. The value is committed on Enter or when focus leaves.</summary>
    public TextBox AddText(string label, Func<string> get, Action<string> set, string? watermark = null,
        Func<bool>? enabled = null)
    {
        var box = new TextBox { Text = get(), PlaceholderText = watermark ?? string.Empty, MinWidth = 30 };
        box.AddHandler(TextInputEvent, (_, e) => TuiInputGuard.HandleTextInput(box, e, label, 4096), RoutingStrategies.Tunnel);
        string committed = box.Text ?? string.Empty;

        void Commit()
        {
            string value = box.Text ?? string.Empty;
            if (value == committed) return;
            committed = value;
            try
            {
                set(value);
            }
            catch (Exception ex)
            {
                TuiNotifications.Error(label, ex.Message);
            }

            Refresh();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                Commit();
                e.Handled = true;
            }
        };

        var row = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
        var caption = new TextBlock { Text = label.TrimEnd(':') + ": ", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);
        row.Children.Add(box);
        _refreshers.Add(() =>
        {
            if (!box.IsFocused)
            {
                committed = get();
                box.Text = committed;
            }

            box.IsEnabled = enabled?.Invoke() ?? true;
        });
        box.IsEnabled = enabled?.Invoke() ?? true;
        _rows.Children.Add(row);
        return box;
    }

    public TuiFormField AddButton(string label, Func<Task> action, Func<bool>? enabled = null)
    {
        var button = new TuiFormField(TuiFieldKind.Button, label);
        button.Activated += async () =>
        {
            if (!button.IsEffectivelyEnabled) return;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                TuiNotifications.Error(label, ex.Message);
                UniGetUI.Core.Logging.Logger.Error(ex);
            }

            Refresh();
        };
        _refreshers.Add(() => button.IsEnabled = enabled?.Invoke() ?? true);
        button.IsEnabled = enabled?.Invoke() ?? true;
        _rows.Children.Add(button);
        return button;
    }

    public TuiFormField AddButton(string label, Action action, Func<bool>? enabled = null)
        => AddButton(label, () =>
        {
            action();
            return Task.CompletedTask;
        }, enabled);

    public void AddControl(Control control) => _rows.Children.Add(control);

    public void Refresh()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement;
        List<InputElement> before = focused is null ? [] : Focusables().ToList();
        int index = focused is null ? -1 : before.FindIndex(f => ReferenceEquals(f, focused));
        foreach (Action refresh in _refreshers) refresh();

        // A refresh can disable the focused field (e.g. "Log in" once signed in): keep keyboard focus in
        // the form by moving it to the nearest field that is still enabled.
        if (index >= 0 && focused is { IsEffectivelyEnabled: false })
        {
            Dispatcher.UIThread.Post(() =>
            {
                List<InputElement> after = Focusables().ToList();
                if (after.Count == 0) return;
                InputElement? next = before.Skip(index + 1).FirstOrDefault(after.Contains)
                                     ?? before.Take(index).LastOrDefault(after.Contains)
                                     ?? after[0];
                next.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Focuses the first field whose text contains <paramref name="label"/>.</summary>
    public bool FocusField(string label)
    {
        foreach (InputElement field in Focusables())
        {
            string text = field switch
            {
                ContentControl c => c.Content?.ToString() ?? "",
                TextBox t => (t.Parent as DockPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "",
                _ => "",
            };
            if (!text.Contains(label, StringComparison.Ordinal)) continue;
            field.Focus();
            (field as Control)?.BringIntoView();
            return true;
        }

        return false;
    }

    /// <summary>Scrolls the form by <paramref name="lines"/> rows (negative scrolls up), for read-only uses.</summary>
    public void ScrollBy(double lines)
        => _scroll.Offset = new Vector(_scroll.Offset.X, Math.Clamp(_scroll.Offset.Y + lines, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));

    /// <summary>The height of the visible part of the form, in rows.</summary>
    public double ViewportRows => _scroll.Viewport.Height;

    public bool FocusPrimary()
    {
        InputElement? first = Focusables().FirstOrDefault();
        return first?.Focus() == true;
    }

    private IEnumerable<InputElement> Focusables()
        => _rows.GetVisualDescendants().OfType<InputElement>()
            .Where(c => c is TuiFormField or CheckBox or Button or TextBox && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down)) return;
        List<InputElement> fields = Focusables().ToList();
        if (fields.Count == 0) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        int index = fields.FindIndex(f => ReferenceEquals(f, focused) || (focused is not null && f.IsVisualAncestorOf(focused)));
        int next = index < 0 ? 0 : Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, fields.Count - 1);
        if (next == index) return;
        fields[next].Focus();
        (fields[next] as Control)?.BringIntoView();
        e.Handled = true;
    }
}
