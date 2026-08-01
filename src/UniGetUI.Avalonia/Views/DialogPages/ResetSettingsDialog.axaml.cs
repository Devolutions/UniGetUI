using Avalonia.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public sealed class ResetSettingsDialog : ImmersiveConfirmationDialog
{
    public bool Confirmed => Result is true;

    public ResetSettingsDialog()
    {
        Configure(
            CoreTools.Translate("Reset UniGetUI"),
            new TextBlock
            {
                Text = CoreTools.Translate("Do you really want to reset UniGetUI to its default settings? This action cannot be undone."),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.85,
            },
            CoreTools.Translate("Reset UniGetUI"),
            CoreTools.Translate("Cancel"));
        MaxWidth = 460;
        MinHeight = 160;
        FocusPrimaryButton = false;
    }
}
