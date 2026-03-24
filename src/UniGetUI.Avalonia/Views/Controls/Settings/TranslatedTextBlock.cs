using Avalonia.Controls;
using Avalonia.Media;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

/// <summary>
/// A TextBlock whose Text property is automatically translated via CoreTools.Translate.
/// </summary>
public class TranslatedTextBlock : TextBlock
{
    public new string Text
    {
        get => base.Text ?? "";
        set => base.Text = CoreTools.Translate(value);
    }
}
