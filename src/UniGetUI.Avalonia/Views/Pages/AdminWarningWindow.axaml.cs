using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class AdminWarningWindow : Window
{
    public AdminWarningWindow()
    {
        InitializeComponent();

        Title = CoreTools.Translate("Administrator privileges");
        TitleBlock.Text = CoreTools.Translate("Administrator privileges");
        MessageBlock.Text = CoreTools.Translate(
            "UniGetUI has been run as administrator, which is not recommended. When running UniGetUI as administrator, EVERY operation launched from UniGetUI will have administrator privileges. You can still use the program, but we highly recommend not running UniGetUI with administrator privileges."
        );
        UnderstandBtn.Content = CoreTools.Translate("I understand");
    }

    private void UnderstandBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
