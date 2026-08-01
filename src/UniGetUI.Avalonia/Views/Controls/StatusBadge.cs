using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace UniGetUI.Avalonia.Views.Controls;

public enum StatusBadgeSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A compact, theme-aware status indicator shared across status surfaces.</summary>
public sealed class StatusBadge : Border
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string>(nameof(Text), "");

    public static readonly StyledProperty<StatusBadgeSeverity> SeverityProperty =
        AvaloniaProperty.Register<StatusBadge, StatusBadgeSeverity>(nameof(Severity));

    private readonly TextBlock _text;
    private readonly AvaloniaPath _glyph;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusBadgeSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public StatusBadge()
    {
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(6, 3);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;

        var icon = new Ellipse { Width = 12, Height = 12 };
        icon.Classes.Add("status-badge-icon");
        _glyph = new AvaloniaPath
        {
            Width = 5,
            Height = 5,
            StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _glyph.Classes.Add("status-badge-glyph");

        var iconHost = new Grid
        {
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, _glyph },
        };
        AutomationProperties.SetAccessibilityView(iconHost, AccessibilityView.Raw);

        _text = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.Classes.Add("status-badge-text");

        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { iconHost, _text },
        };
        UpdateAppearance();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == SeverityProperty)
            UpdateAppearance();
    }

    private void UpdateAppearance()
    {
        if (_text is null || _glyph is null)
            return;

        _text.Text = Text;
        Classes.Remove("status-info");
        Classes.Remove("status-success");
        Classes.Remove("status-warning");
        Classes.Remove("status-error");
        Classes.Add(Severity switch
        {
            StatusBadgeSeverity.Success => "status-success",
            StatusBadgeSeverity.Warning => "status-warning",
            StatusBadgeSeverity.Error => "status-error",
            _ => "status-info",
        });

        _glyph.RenderTransform = null;
        _glyph.Data = Severity switch
        {
            StatusBadgeSeverity.Success => Geometry.Parse("M0.5,2.6 L2,4.1 L4.5,0.9"),
            StatusBadgeSeverity.Warning => Geometry.Parse("M2.5,0.4 L2.5,2.1 M2.1,4.5 L2.9,4.5"),
            StatusBadgeSeverity.Error => Geometry.Parse("M0.75,0.75 L4.25,4.25 M4.25,0.75 L0.75,4.25"),
            _ => Geometry.Parse("M2.5,2 L2.5,4.4 M2.5,0.6 L2.5,0.7"),
        };
        if (Severity == StatusBadgeSeverity.Success)
            _glyph.RenderTransform = new TranslateTransform(0, 0.5);
    }
}
