using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

        // Play the slide-in entrance only when the OS isn't set to minimize motion.
        if (!MotionPreference.ReducedMotion)
            BodyBorder.Classes.Add("animate-in");
    }

    private InfoBarViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as InfoBarViewModel;

        _vm?.PropertyChanged += OnViewModelPropertyChanged;
        if (_vm is not null)
            ApplySeverity(_vm.Severity);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InfoBarViewModel.Severity) && _vm is not null)
            ApplySeverity(_vm.Severity);
    }

    private void ApplySeverity(InfoBarSeverity severity)
    {
        // Background + border: swap a single CSS class — DynamicResource in the style
        // handles theme changes automatically without any event subscription.
        BodyBorder.Classes.Set("severity-success", severity == InfoBarSeverity.Success);
        BodyBorder.Classes.Set("severity-error", severity == InfoBarSeverity.Error);
        BodyBorder.Classes.Set("severity-warning", severity == InfoBarSeverity.Warning);
        BodyBorder.Classes.Set("severity-info", severity == InfoBarSeverity.Informational);

        // Strip colour (solid, not theme-sensitive)
        var stripColor = severity switch
        {
            InfoBarSeverity.Warning => Color.Parse("#F7A800"),
            InfoBarSeverity.Error => Color.Parse("#C42B1C"),
            InfoBarSeverity.Success => Color.Parse("#107C10"),
            _ => Color.Parse("#0078D4"),
        };
        SeverityStrip.Background = new SolidColorBrush(stripColor);

        // Icon (shared SVG asset, tinted with the severity colour)
        SeverityIcon.Path = severity switch
        {
            InfoBarSeverity.Warning => WarningIcon,
            InfoBarSeverity.Error => ErrorIcon,
            InfoBarSeverity.Success => SuccessIcon,
            _ => InfoIcon,
        };
        SeverityIcon.Foreground = new SolidColorBrush(stripColor);
    }
}
