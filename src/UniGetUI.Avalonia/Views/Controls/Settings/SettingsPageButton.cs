using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public partial class SettingsPageButton : SettingsCard
{
    public string Text
    {
        set => Header = CoreTools.Translate(value);
    }

    public string UnderText
    {
        set => Description = CoreTools.Translate(value);
    }

    public IconType Icon
    {
        set => HeaderIcon = new SvgIcon
        {
            Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{IconTypeToName(value)}.svg",
            Width = 24,
            Height = 24,
        };
    }

    public SettingsPageButton()
    {
        CornerRadius = new CornerRadius(8);
        IsClickEnabled = true;

        Content = new TextBlock
        {
            Text = "›",
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
        };
    }

    private static string IconTypeToName(IconType icon) => icon switch
    {
        IconType.Package => "package",
        IconType.UAC => "uac",
        IconType.Update => "update",
        IconType.Help => "help",
        IconType.Console => "console",
        IconType.Checksum => "checksum",
        IconType.Download => "download",
        IconType.Settings => "settings",
        IconType.SaveAs => "save_as",
        IconType.OpenFolder => "open_folder",
        IconType.Experimental => "experimental",
        _ => icon.ToString().ToLower(),
    };
}
