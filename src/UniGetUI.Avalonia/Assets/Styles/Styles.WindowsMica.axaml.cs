using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UniGetUI.Avalonia.Assets.Styles;

public partial class WindowsMicaStyles : ResourceDictionary
{
    public WindowsMicaStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
