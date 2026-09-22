using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.Controls;
using ICommand = System.Windows.Input.ICommand;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

/// <summary>
/// Avalonia equivalent of CommunityToolkit.WinUI.Controls.SettingsCard.
/// Layout: [icon][header / description stack]    [content]
/// </summary>
public class SettingsCard : UserControl
{
    // ── Internal layout elements ───────────────────────────────────────────
    private readonly Border _border;
    private readonly ContentControl _iconPresenter;
    private readonly ContentControl _headerPresenter;
    private readonly ContentControl _descriptionPresenter;
    private readonly ContentControl _contentPresenter;
    private readonly StackPanel _descriptionRow;
    private readonly StackPanel _headerStack;
    private readonly Grid _layoutGrid;
    private readonly SvgIcon _chevron;

    // Match CommunityToolkit SettingsCard's responsive breakpoints. At normal widths
    // the layout is unchanged; below these widths the content drops below the header,
    // and at the narrowest width the header icon is hidden.
    private const double ContentWrapThreshold = 476;
    private const double HideHeaderIconThreshold = 286;
    private bool _contentWrapped;

    // ── Styled properties ──────────────────────────────────────────────────
    public static readonly StyledProperty<object?> HeaderProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Header));

    public static readonly StyledProperty<object?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(Description));

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<SettingsCard, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<SettingsCard, object?>(nameof(CommandParameter));

    // ── Backing stores ─────────────────────────────────────────────────────
    private Control? _headerIcon;
    private object? _rightContent;
    private bool _isClickEnabled;

    /// <summary>
    /// Keep compact toggle-style content at the right edge when the card wraps.
    /// Other settings controls follow the WinUI SettingsCard behavior and align left.
    /// </summary>
    protected bool RightAlignWrappedContent { get; set; }

    // ── Events ─────────────────────────────────────────────────────────────
    public event EventHandler<RoutedEventArgs>? Click;

    // ── Properties ────────────────────────────────────────────────────────

    public new object? Content
    {
        get => _rightContent;
        set
        {
            _rightContent = value;
            _contentPresenter.Content = value is string s
                ? new TextBlock { Text = s, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }
                : value;
            UpdateResponsiveLayout(Bounds.Width);
        }
    }

    public object? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public object? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Control? HeaderIcon
    {
        get => _headerIcon;
        set
        {
            _headerIcon = value;
            _iconPresenter.Content = value;
            UpdateResponsiveLayout(Bounds.Width);
        }
    }

    public bool IsClickEnabled
    {
        get => _isClickEnabled;
        set
        {
            _isClickEnabled = value;
            Focusable = value;
            Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            _chevron.IsVisible = value;
            if (value)
                _border.Classes.Add("settings-card-clickable");
            else
                _border.Classes.Remove("settings-card-clickable");
        }
    }

    public new CornerRadius CornerRadius
    {
        get => _border.CornerRadius;
        set => _border.CornerRadius = value;
    }

    public new Thickness BorderThickness
    {
        get => _border.BorderThickness;
        set => _border.BorderThickness = value;
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public SettingsCard()
    {
        _iconPresenter = new ContentControl
        {
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Width = 24,
            Height = 24,
        };
        AutomationProperties.SetAccessibilityView(_iconPresenter, AccessibilityView.Raw);

        _headerPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        _descriptionPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        _descriptionRow = new StackPanel
        {
            Orientation = Orientation.Vertical,
            IsVisible = false,
        };
        _descriptionRow.Children.Add(_descriptionPresenter);

        _headerStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _headerStack.Children.Add(_headerPresenter);
        _headerStack.Children.Add(_descriptionRow);

        _contentPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 80,
            Margin = new Thickness(16, 0, 0, 0),
        };

        _chevron = new SvgIcon
        {
            Path = "avares://UniGetUI/Assets/Symbols/forward.svg",
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = false,
        };
        AutomationProperties.SetAccessibilityView(_chevron, AccessibilityView.Raw);

        // This mirrors the WinUI SettingsCard template: icon | header | content | action.
        // Keeping the header directly in the star-sized grid column is important: unlike a
        // horizontal StackPanel, it receives a finite width and TextBlock wrapping can engage
        // before the right-side control overlaps it.
        _layoutGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("*,Auto"),
            MinHeight = 60,
            Margin = new Thickness(16, 8, 16, 8),
        };
        Grid.SetRow(_iconPresenter, 0);
        Grid.SetColumn(_iconPresenter, 0);
        Grid.SetRow(_headerStack, 0);
        Grid.SetColumn(_headerStack, 1);
        Grid.SetRow(_contentPresenter, 0);
        Grid.SetColumn(_contentPresenter, 2);
        Grid.SetRow(_chevron, 0);
        Grid.SetRowSpan(_chevron, 2);
        Grid.SetColumn(_chevron, 3);
        _layoutGrid.Children.Add(_iconPresenter);
        _layoutGrid.Children.Add(_headerStack);
        _layoutGrid.Children.Add(_contentPresenter);
        _layoutGrid.Children.Add(_chevron);

        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = _layoutGrid,
        };
        _border.Classes.Add("settings-card");

        base.Content = _border;

        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
        SizeChanged += (_, e) => UpdateResponsiveLayout(e.NewSize.Width);
        // Keyboard focus is rendered by the app-wide Avalonia FocusAdorner. Do not also
        // recolor/thicken the card border; the double outline is not WinUI-like.
        SyncAutomationProperties();
    }

    private void UpdateResponsiveLayout(double width)
    {
        // Before first layout there is no useful width yet. HeaderIcon starts hidden when null,
        // and the first SizeChanged pass will apply the responsive state.
        if (width <= 0)
        {
            _iconPresenter.IsVisible = _headerIcon is not null;
            return;
        }

        bool wrapContent = _rightContent is not null && width <= ContentWrapThreshold;
        bool hideHeaderIcon = width <= HideHeaderIconThreshold;

        _iconPresenter.IsVisible = _headerIcon is not null && !hideHeaderIcon;

        if (wrapContent == _contentWrapped)
            return;

        _contentWrapped = wrapContent;
        if (wrapContent)
        {
            Grid.SetRow(_contentPresenter, 1);
            Grid.SetColumn(_contentPresenter, 1);
            _contentPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
            _contentPresenter.HorizontalContentAlignment =
                RightAlignWrappedContent ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _contentPresenter.Margin = new Thickness(0);
            _layoutGrid.RowSpacing = 8;
        }
        else
        {
            Grid.SetRow(_contentPresenter, 0);
            Grid.SetColumn(_contentPresenter, 2);
            _contentPresenter.HorizontalAlignment = HorizontalAlignment.Right;
            _contentPresenter.HorizontalContentAlignment = HorizontalAlignment.Right;
            _contentPresenter.Margin = new Thickness(16, 0, 0, 0);
            _layoutGrid.RowSpacing = 0;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HeaderProperty)
        {
            var value = change.NewValue;
            _headerPresenter.Content = value is string s
                ? new TextBlock
                {
                    Text = s,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : value;
            SyncAutomationProperties();
        }
        else if (change.Property == DescriptionProperty)
        {
            var value = change.NewValue;
            if (value is null)
            {
                _descriptionRow.IsVisible = false;
                SyncAutomationProperties();
                return;
            }
            _descriptionPresenter.Content = value is string s
                ? new TextBlock
                {
                    Text = s,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.None,
                    FontSize = 12,
                    Opacity = 0.7,
                }
                : value;
            _descriptionRow.IsVisible = true;
            SyncAutomationProperties();
        }
    }

    protected string? GetAutomationNameText() => ExtractAutomationText(Header);

    protected string? GetAutomationHelpText() => ExtractAutomationText(Description);

    protected void ApplyAutomationMetadata(Control control, string? name = null, string? helpText = null)
    {
        name ??= GetAutomationNameText();
        helpText ??= GetAutomationHelpText();

        if (!string.IsNullOrWhiteSpace(name))
            AutomationProperties.SetName(control, name);

        if (!string.IsNullOrWhiteSpace(helpText))
            AutomationProperties.SetHelpText(control, helpText);
    }

    private static string? ExtractAutomationText(object? value) => value switch
    {
        string s when !string.IsNullOrWhiteSpace(s) => s,
        TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text) => tb.Text,
        SelectableTextBlock stb when !string.IsNullOrWhiteSpace(stb.Text) => stb.Text,
        ContentControl cc when cc.Content is not null => ExtractAutomationText(cc.Content),
        _ => null,
    };

    private void SyncAutomationProperties()
    {
        string? name = GetAutomationNameText();
        string? help = GetAutomationHelpText();
        ApplyAutomationMetadata(this, name, help);
        // Also propagate to _border: on macOS, Avalonia may surface the Border element
        // rather than the UserControl wrapper, so set the name on both.
        if (!string.IsNullOrWhiteSpace(name))
            AutomationProperties.SetName(_border, name);
        if (!string.IsNullOrWhiteSpace(help))
            AutomationProperties.SetHelpText(_border, help);
        var type = IsClickEnabled ? (AutomationControlType?)AutomationControlType.Button : null;
        AutomationProperties.SetControlTypeOverride(this, type);
        if (type.HasValue)
            AutomationProperties.SetControlTypeOverride(_border, type);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isClickEnabled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Pointer activation leaves the card's normal stroke untouched; keyboard focus is
        // handled solely by the external focus adorner.
        InvokeClick();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isClickEnabled) return;
        if (e.Source != this) return;   // only when the card itself has focus, not a child
        if (e.Key is not (Key.Enter or Key.Space)) return;

        InvokeClick();
        e.Handled = true;
    }

    private void InvokeClick()
    {
        Click?.Invoke(this, new RoutedEventArgs());
        var cmd = Command;
        var param = CommandParameter;
        if (cmd?.CanExecute(param) == true)
            cmd.Execute(param);
    }
}
