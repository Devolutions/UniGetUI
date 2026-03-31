using Avalonia;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.Views.Controls.Settings;

public partial class SettingsPageButton : SettingsCard
{
    public string Text
    {
        set => Header = value;
    }

    public string UnderText
    {
        set => Description = value;
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
    }

    private static string IconTypeToName(IconType icon) => icon switch
    {
        IconType.Chocolatey => "choco",
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
        IconType.ClipboardList => "clipboard_list",
        _ => icon.ToString().ToLower(),
    };
}
