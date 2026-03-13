using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

public partial class LoadingView : UserControl
{
    public LoadingView()
    {
        InitializeComponent();
        this.FindControl<TextBlock>("TaglineBlock")!.Text = CoreTools.Translate("Package management made easy");
        this.FindControl<TextBlock>("StatusBlock")!.Text = CoreTools.Translate("Loading packages, please wait...");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
