using Avalonia.Controls;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.DialogPages;

public sealed class DiscardBundleChangesDialog : ImmersiveConfirmationDialog
{
    public bool Confirmed => Result is true;

    public DiscardBundleChangesDialog()
    {
        Configure(
            CoreTools.Translate("Unsaved changes"),
            new TextBlock
            {
                Text = CoreTools.Translate("You have unsaved changes in the current bundle. Do you want to discard them?"),
                TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                Opacity = 0.85,
            },
            CoreTools.Translate("Discard changes"),
            CoreTools.Translate("Cancel"));
        MaxWidth = 460;
        MinHeight = 160;
    }
}
