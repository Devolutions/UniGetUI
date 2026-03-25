using Avalonia.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

/// <summary>
/// A TextBlock whose Text property is automatically translated via CoreTools.Translate.
/// Used for section headers set from code-behind; prefer {t:Translate} in AXAML instead.
/// </summary>
public class TranslatedTextBlock : TextBlock
{
    public new string Text
    {
        get => base.Text ?? "";
        set => base.Text = CoreTools.Translate(value);
    }
}
