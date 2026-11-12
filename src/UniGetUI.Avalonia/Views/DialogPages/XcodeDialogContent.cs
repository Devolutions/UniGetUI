using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace UniGetUI.Avalonia.Views.DialogPages;

internal static class XcodeDialogContent
{
    internal static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        Focusable = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
}
