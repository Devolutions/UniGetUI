using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.AboutPages;

public partial class Contributors : UserControl
{
    public Contributors()
    {
        DataContext = new ContributorsViewModel();
        InitializeComponent();
    }

    private void GitHubButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri url })
            CoreTools.Launch(url.ToString());
    }
}
