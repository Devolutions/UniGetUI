using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

namespace UniGetUI.Avalonia.Views.Pages.AboutPages;

public partial class ThirdPartyLicenses : UserControl
{
    public ThirdPartyLicenses()
    {
        DataContext = new ThirdPartyLicensesViewModel();
        InitializeComponent();
    }
}
