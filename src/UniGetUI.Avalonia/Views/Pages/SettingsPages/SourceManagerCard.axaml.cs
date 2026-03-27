using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class SourceManagerCard : UserControl
{
    public SourceManagerCard(IPackageManager manager)
    {
        DataContext = new SourceManagerCardViewModel(manager);
        InitializeComponent();
    }
}
