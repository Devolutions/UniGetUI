using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels.Pages.AboutPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.AboutPages;

public partial class Translators : UserControl
{
    public Translators()
    {
        DataContext = new TranslatorsViewModel();
        InitializeComponent();
    }

    private void BecomeTranslatorButton_Click(object? sender, RoutedEventArgs e) =>
        CoreTools.Launch("https://github.com/Devolutions/UniGetUI/wiki#translating-wingetui");

    private void GitHubButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri url })
            CoreTools.Launch(url.ToString());
    }
}
