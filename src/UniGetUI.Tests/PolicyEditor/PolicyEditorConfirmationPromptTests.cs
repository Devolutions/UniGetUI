using UniGetUI.Avalonia.Views.DialogPages;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorConfirmationPromptTests
{
    [Fact]
    public void CancelPendingChoice_DisablesRequiredChoiceBeforeRequestingClose()
    {
        var dialog = new ImmersiveConfirmationDialog
        {
            RequireChoice = true,
        };
        bool closeRequested = false;
        dialog.CloseRequested += (_, _) => closeRequested = true;

        dialog.CancelPendingChoice();

        Assert.False(dialog.RequireChoice);
        Assert.True(closeRequested);
        Assert.Null(dialog.Result);
    }
}
