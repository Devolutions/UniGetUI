using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class DriversPage : UserControl
{
    public DriversPage(DriversViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void DriverCard_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: DriverUpdateItemViewModel item }) return;
        if (e.Source is Visual source
            && source.GetSelfAndVisualAncestors().Any(visual =>
                visual is Button or ToggleButton or CheckBox or SelectableTextBlock
                || (visual is Border border && border.Classes.Contains("driver-details"))))
            return;
        item.IsExpanded = !item.IsExpanded;
    }
}
