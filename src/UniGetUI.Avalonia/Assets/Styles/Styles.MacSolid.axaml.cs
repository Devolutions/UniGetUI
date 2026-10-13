using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UniGetUI.Avalonia.Assets.Styles;

public partial class MacSolidStyles : ResourceDictionary
{
    public MacSolidStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
