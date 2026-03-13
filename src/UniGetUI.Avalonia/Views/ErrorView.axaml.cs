using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views;

public partial class ErrorView : UserControl
{
    private TextBlock ErrorHeadingText => GetControl<TextBlock>("ErrorHeadingBlock");

    private TextBlock ErrorTitleText => GetControl<TextBlock>("ErrorTitleBlock");

    private TextBlock ErrorDescriptionText => GetControl<TextBlock>("ErrorDescriptionBlock");

    public ErrorView()
        : this("Error", string.Empty)
    {
    }

    public ErrorView(string title, string description)
    {
        InitializeComponent();
        ErrorHeadingText.Text = CoreTools.Translate("Error");
        ErrorTitleText.Text = title;
        ErrorDescriptionText.Text = description;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
