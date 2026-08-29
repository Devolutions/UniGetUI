using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;

namespace UniGetUI.Avalonia.Views.Controls;

public sealed class PolicyJsonEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    public PolicyJsonEditor()
    {
        ShowLineNumbers = true;
        WordWrap = false;
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        FontSize = 12;
        Padding = new Thickness(8);
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }
}
