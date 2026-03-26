using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

namespace UniGetUI.Avalonia.Views.Pages.AboutPages;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        DataContext = new AboutPageViewModel();
        InitializeComponent();
    }
}
