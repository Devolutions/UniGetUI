using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>The kind of row a <see cref="TuiFormField"/> draws.</summary>
internal enum TuiFieldKind
{
    Check,
    Choice,
    Button,
}

/// <summary>
/// One focusable row of a <see cref="TuiForm"/>, drawn with plain characters only:
/// <c>[x] Label</c> for a checkbox, <c>Label: ‹ value ›</c> for a choice and <c>› Label</c> for a button.
/// The theme's CheckBox/Button templates use glyphs and shadows that render unevenly across terminal
/// fonts; this control looks the same in every terminal and highlights the whole row when focused.
/// Space / Enter / click raise <see cref="Activated"/>; Left / Right raise <see cref="Stepped"/>.
/// </summary>
internal sealed class TuiFormField : ContentControl
{
    private static readonly IBrush FocusBackground = TuiPalette.Focus;
    private static readonly IBrush Normal = TuiPalette.Text;
    private static readonly IBrush Dim = TuiPalette.TextDim;
    private static readonly IBrush MarkOff = TuiPalette.TextDim;

    private readonly TextBlock _mark = new();
    private readonly TextBlock _label = new();
    private readonly TextBlock _value = new();
    private readonly FieldText _text;

    public TuiFormField(TuiFieldKind kind, string label)
    {
        Kind = kind;
        Label = label;
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(1, 0, 1, 0);
        Margin = new Thickness(1, 0, 0, 0);
        Background = Brushes.Transparent;
        _text = new FieldText(this) { Orientation = Orientation.Horizontal };
        _text.Children.Add(_mark);
        _text.Children.Add(_label);
        _text.Children.Add(_value);
        Content = _text;
        Render();
    }

    public TuiFieldKind Kind { get; }

    public string Label { get; }

    /// <summary>Checkbox state (Check fields only).</summary>
    public bool IsChecked
    {
        get;
        set
        {
            field = value;
            Render();
        }
    }

    /// <summary>Displayed value (Choice fields only).</summary>
    public string Value
    {
        get;
        set
        {
            field = value;
            Render();
        }
    } = "";

    public event Action? Activated;

    public event Action<int>? Stepped;

    /// <summary>The row as text, e.g. "[x] Enable notifications" (used by tests and settings search).</summary>
    public string Text => Kind switch
    {
        TuiFieldKind.Check => $"{(IsChecked ? "[x]" : "[ ]")} {Label}",
        TuiFieldKind.Choice => $"{Label.TrimEnd(':')}: ‹ {Value} ›",
        _ => $"› {Label}",
    };

    private void Render()
    {
        bool enabled = IsEffectivelyEnabled;
        bool focused = IsFocused;
        Background = focused ? FocusBackground : Brushes.Transparent;
        IBrush text = enabled ? (focused ? TuiPalette.FocusText : Normal) : Dim;
        switch (Kind)
        {
            case TuiFieldKind.Check:
                _mark.Text = IsChecked ? "[x] " : "[ ] ";
                _mark.Foreground = !enabled ? Dim : focused ? TuiPalette.FocusText
                    : IsChecked ? TuiPalette.Success : MarkOff;
                _label.Text = Label;
                _value.Text = "";
                break;
            case TuiFieldKind.Choice:
                _mark.Text = "";
                _label.Text = Label.TrimEnd(':') + ": ";
                _value.Text = $"‹ {Value} ›";
                _value.Foreground = !enabled ? Dim : focused ? TuiPalette.FocusText : TuiPalette.Brand;
                break;
            default:
                _mark.Text = "› ";
                _mark.Foreground = !enabled ? Dim : focused ? TuiPalette.FocusText : TuiPalette.Brand;
                _label.Text = Label;
                _value.Text = "";
                break;
        }

        _label.Foreground = text;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Render();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEffectivelyEnabledProperty) Render();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Space or Key.Enter or Key.Return:
                Activated?.Invoke();
                e.Handled = true;
                break;
            case Key.Left when Kind == TuiFieldKind.Choice:
                Stepped?.Invoke(-1);
                e.Handled = true;
                break;
            case Key.Right when Kind == TuiFieldKind.Choice:
                Stepped?.Invoke(1);
                e.Handled = true;
                break;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // Space arrives as text too; it was already handled as a key.
        if (e.Text == " ") e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Activated?.Invoke();
        e.Handled = true;
    }

    /// <summary>The row's content; its text is the field's text so focus-based lookups can read it.</summary>
    private sealed class FieldText(TuiFormField owner) : StackPanel
    {
        public override string ToString() => owner.Text;
    }
}
