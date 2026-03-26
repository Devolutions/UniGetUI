using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.AboutPages;

public partial class ThirdPartyLicenses : UserControl
{
    public ThirdPartyLicenses()
    {
        DataContext = new ThirdPartyLicensesViewModel();
        InitializeComponent();
    }

    private void LicenseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri url })
            CoreTools.Launch(url.ToString());
    }

    private void HomepageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri url })
            CoreTools.Launch(url.ToString());
    }
}
