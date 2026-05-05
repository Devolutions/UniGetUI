using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views.Controls;

public partial class InfoBar : UserControl
{
    // Icon path data for each severity
    private const string InfoPath = "M12,2A10,10,0,1,0,22,12,10,10,0,0,0,12,2Zm1,15H11V11h2Zm0-8H11V7h2Z";
    private const string WarningPath = "M12,2,1,21H23Zm1,14H11V14h2Zm0-4H11V9h2Z";
    private const string ErrorPath = "M12,2A10,10,0,1,0,22,12,10,10,0,0,0,12,2Zm1,13H11V13h2Zm0-6H11V7h2Z";
    private const string SuccessPath = "M12,2A10,10,0,1,0,22,12,10,10,0,0,0,12,2ZM10,17,5,12l1.41-1.41L10,14.17l7.59-7.59L19,8Z";

    public InfoBar()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private InfoBarViewModel? _vm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as InfoBarViewModel;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplySeverity(_vm.Severity);
        }
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

        // Icon shape
        SeverityIcon.Data = Geometry.Parse(severity switch
        {
            InfoBarSeverity.Warning => WarningPath,
            InfoBarSeverity.Error => ErrorPath,
            InfoBarSeverity.Success => SuccessPath,
            _ => InfoPath,
        });

        // Icon foreground (solid, not theme-sensitive)
        SeverityIcon.Foreground = new SolidColorBrush(stripColor);
    }
}
