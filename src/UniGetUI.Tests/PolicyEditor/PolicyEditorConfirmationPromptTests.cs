using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorConfirmationPromptTests
{
    [Fact]
    public void ConfirmationPromptRestoresAndActivatesItsOwnerBeforeShowing()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "UniGetUI.Avalonia",
            "Views",
            "Pages",
            "SettingsPages",
            "PolicyEditor",
            "PolicyEditorConfirmationPrompt.cs"));
        int restore = source.IndexOf("EnsureOwnerVisible();", StringComparison.Ordinal);
        int show = source.IndexOf("dialog.ShowDialog(_owner)", StringComparison.Ordinal);

        Assert.True(restore >= 0 && show > restore);
        Assert.Contains("WindowState.Minimized", source);
        Assert.Contains("_owner.Activate()", source);
    }

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

    [Theory]
    [InlineData("existing-policy", "You have unsaved changes to policy existing-policy. Discard them?")]
    [InlineData("", "You have unsaved policy changes. Discard them?")]
    [InlineData("   ", "You have unsaved policy changes. Discard them?")]
    public void DiscardMessage_FormatsIdOrUsesNaturalBlankFallback(
        string draftId,
        string expected)
    {
        string message = PolicyEditorConfirmationPrompt.DescribeMessage(new(
            PolicyEditorConfirmationKind.DiscardChanges,
            PolicyReplacementOperation.Update,
            draftId,
            "token",
            PolicyManagementState.Active,
            "active",
            []));

        Assert.Equal(expected, message);
        Assert.DoesNotMatch(@"\{\d+\}", message);
    }

    [Fact]
    public void FeatureConfirmationMessages_SubstituteAllArgumentsInOrder()
    {
        PolicyEditorConfirmationKind[] kinds =
        [
            PolicyEditorConfirmationKind.Warnings,
            PolicyEditorConfirmationKind.RemoveAllowSafetyLimits,
            PolicyEditorConfirmationKind.ReplaceIdentity,
            PolicyEditorConfirmationKind.Create,
            PolicyEditorConfirmationKind.Repair,
            PolicyEditorConfirmationKind.ConfirmOverwrite,
            PolicyEditorConfirmationKind.DiscardChanges,
            PolicyEditorConfirmationKind.EnableAuditMode,
            PolicyEditorConfirmationKind.EnableDefaultAllow,
        ];
        foreach (PolicyEditorConfirmationKind kind in kinds)
        {
            string message = PolicyEditorConfirmationPrompt.DescribeMessage(new(
                kind,
                PolicyReplacementOperation.ReplaceIdentity,
                "draft-id",
                "token",
                PolicyManagementState.Active,
                "active-id",
                [],
                WarningCount: 3,
                RuleId: "rule-id"));

            Assert.DoesNotMatch(@"\{\d+\}", message);
        }

        string warnings = PolicyEditorConfirmationPrompt.DescribeMessage(new(
            PolicyEditorConfirmationKind.Warnings,
            PolicyReplacementOperation.Update,
            "draft-id",
            "token",
            PolicyManagementState.Active,
            "active-id",
            [],
            WarningCount: 3));
        Assert.Contains("3", warnings);
        Assert.Contains("draft-id", warnings);
        Assert.True(warnings.IndexOf("3", StringComparison.Ordinal)
            < warnings.IndexOf("draft-id", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "UniGetUI.Windows.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
