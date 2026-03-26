using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

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
                ? new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center }
                : value;
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
            _iconPresenter.IsVisible = value is not null;
        }
    }

    public bool IsClickEnabled
    {
        get => _isClickEnabled;
        set
        {
            _isClickEnabled = value;
            Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
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

        var leftStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftStack.Children.Add(_headerPresenter);
        leftStack.Children.Add(_descriptionRow);

        var leftRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        leftRow.Children.Add(_iconPresenter);
        leftRow.Children.Add(leftStack);

        _contentPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 0, 0),
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 60,
            Margin = new Thickness(16, 8, 16, 8),
        };
        Grid.SetColumn(leftRow, 0);
        Grid.SetColumn(_contentPresenter, 1);
        grid.Children.Add(leftRow);
        grid.Children.Add(_contentPresenter);

        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        _border.Classes.Add("settings-card");

        base.Content = _border;

        PointerPressed += OnPointerPressed;
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
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : value;
        }
        else if (change.Property == DescriptionProperty)
        {
            var value = change.NewValue;
            if (value is null)
            {
                _descriptionRow.IsVisible = false;
                return;
            }
            _descriptionPresenter.Content = value is string s
                ? new TextBlock
                {
                    Text = s,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.7,
                }
                : value;
            _descriptionRow.IsVisible = true;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_isClickEnabled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        Click?.Invoke(this, new RoutedEventArgs());
        var cmd = Command;
        var param = CommandParameter;
        if (cmd?.CanExecute(param) == true)
            cmd.Execute(param);
    }
}
