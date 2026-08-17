using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views.Controls;

public partial class InfoBar : UserControl
{
    // Severity glyphs reuse the app's shared round symbols (same SvgIcon-by-path convention
    // as the rest of the app), tinted with the severity colour.
    private const string InfoIcon = "avares://UniGetUI/Assets/Symbols/info_round.svg";
    private const string WarningIcon = "avares://UniGetUI/Assets/Symbols/warning_round.svg";
    private const string ErrorIcon = "avares://UniGetUI/Assets/Symbols/close_round.svg";
    private const string SuccessIcon = "avares://UniGetUI/Assets/Symbols/success_round.svg";

    public InfoBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        KeyDown += OnKeyDown;
        GotFocus += (_, e) =>
            BodyBorder.Classes.Set("body-focused", _vm?.IsBodyClickable == true && e.Source == this);
        LostFocus += (_, _) => BodyBorder.Classes.Remove("body-focused");

        // Play the slide-in entrance only when the OS isn't set to minimize motion.
        if (!MotionPreference.ReducedMotion)
            BodyBorder.Classes.Add("animate-in");
    }

    private InfoBarViewModel? _vm;
    private IDisposable? _severityStripBinding;
    private IDisposable? _severityIconBinding;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as InfoBarViewModel;

        _vm?.PropertyChanged += OnViewModelPropertyChanged;
        if (_vm is not null)
            ApplySeverity(_vm.Severity);
        ApplyClickable();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InfoBarViewModel.Severity) && _vm is not null)
            ApplySeverity(_vm.Severity);
        else if (e.PropertyName == nameof(InfoBarViewModel.IsBodyClickable))
            ApplyClickable();
    }

    private void ApplyClickable()
    {
        bool clickable = _vm?.IsBodyClickable == true;
        BodyBorder.Classes.Set("clickable", clickable);
        Focusable = clickable;

        var controlType = clickable ? (AutomationControlType?)AutomationControlType.Button : null;
        AutomationProperties.SetControlTypeOverride(this, controlType);
        AutomationProperties.SetControlTypeOverride(BodyBorder, controlType);

        if (!clickable)
            BodyBorder.Classes.Remove("body-focused");
    }

    private void Body_Tapped(object? sender, TappedEventArgs e)
    {
        if (_vm?.BodyCommand is not { } command)
            return;

        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.BodyCommand is not { } command)
            return;

        if (e.Source != this) return;
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (e.KeyModifiers is not KeyModifiers.None) return;

        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    private void ApplySeverity(InfoBarSeverity severity)
    {
        // Background + border: swap a single CSS class — DynamicResource in the style
        // handles theme changes automatically without any event subscription.
        BodyBorder.Classes.Set("severity-success", severity == InfoBarSeverity.Success);
        BodyBorder.Classes.Set("severity-error", severity == InfoBarSeverity.Error);
        BodyBorder.Classes.Set("severity-warning", severity == InfoBarSeverity.Warning);
        BodyBorder.Classes.Set("severity-info", severity == InfoBarSeverity.Informational);

        _severityStripBinding?.Dispose();
        _severityIconBinding?.Dispose();
        _severityStripBinding = null;
        _severityIconBinding = null;

        Color? stripColor = severity switch
        {
            InfoBarSeverity.Warning => Color.Parse("#F7A800"),
            InfoBarSeverity.Error => Color.Parse("#C42B1C"),
            InfoBarSeverity.Success => Color.Parse("#107C10"),
            _ => null,
        };

        SeverityIcon.Path = severity switch
        {
            InfoBarSeverity.Warning => WarningIcon,
            InfoBarSeverity.Error => ErrorIcon,
            InfoBarSeverity.Success => SuccessIcon,
            _ => InfoIcon,
        };

        if (stripColor is { } color)
        {
            var brush = new SolidColorBrush(color);
            SeverityStrip.Background = brush;
            SeverityIcon.Foreground = brush;
        }
        else
        {
            SeverityStrip.ClearValue(Border.BackgroundProperty);
            SeverityIcon.ClearValue(ForegroundProperty);
            _severityStripBinding = SeverityStrip.Bind(
                Border.BackgroundProperty,
                this.GetResourceObservable("AccentFillColorDefaultBrush"));
            _severityIconBinding = SeverityIcon.Bind(
                ForegroundProperty,
                this.GetResourceObservable("AccentFillColorDefaultBrush"));
        }
    }
}
