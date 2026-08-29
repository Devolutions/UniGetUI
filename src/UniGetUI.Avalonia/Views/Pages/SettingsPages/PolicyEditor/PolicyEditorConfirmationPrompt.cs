using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Production <see cref="IPolicyEditorConfirmationPrompt"/> built on the app's existing
/// <see cref="ImmersiveConfirmationDialog"/> Yes/No pattern (the same primitive used by
/// <c>ConfirmationDialog</c> elsewhere in the app). Renders a distinct title/body per
/// <see cref="PolicyEditorConfirmationKind"/>, including the finding list for the Warnings case.
/// </summary>
public sealed class PolicyEditorConfirmationPrompt : IPolicyEditorConfirmationPrompt
{
    private readonly Window _owner;

    public PolicyEditorConfirmationPrompt(Window owner)
    {
        _owner = owner;
    }

    public async Task<bool> ConfirmAsync(PolicyEditorConfirmationRequest request, CancellationToken cancellationToken)
    {
        (string title, string primaryText) = DescribeAction(request.Kind);
        object body = BuildBody(request);

        var dialog = new ImmersiveConfirmationDialog(
            title,
            body,
            primaryText,
            CoreTools.Translate("Cancel"))
        {
            RequireChoice = true,
        };

        // The immersive overlay is a ContentControl, not a native Window, so screen readers are
        // not guaranteed to announce it as a newly opened modal the way they would a real dialog.
        // Explicitly announce the prompt so it is not silently missed.
        AccessibilityAnnouncementService.Announce(
            $"{title} {DescribeMessage(request)}",
            AutomationLiveSetting.Assertive);

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(_owner);
        return !cancellationToken.IsCancellationRequested && dialog.Result == true;
    }

    private static (string Title, string PrimaryText) DescribeAction(PolicyEditorConfirmationKind kind) => kind switch
    {
        PolicyEditorConfirmationKind.Warnings =>
            (CoreTools.Translate("Save policy with warnings?"), CoreTools.Translate("Save anyway")),
        PolicyEditorConfirmationKind.ReplaceIdentity =>
            (CoreTools.Translate("Replace the active policy?"), CoreTools.Translate("Replace")),
        PolicyEditorConfirmationKind.Create =>
            (CoreTools.Translate("Create a new policy?"), CoreTools.Translate("Create")),
        PolicyEditorConfirmationKind.Repair =>
            (CoreTools.Translate("Repair the stored policy?"), CoreTools.Translate("Repair")),
        PolicyEditorConfirmationKind.ConfirmOverwrite =>
            (CoreTools.Translate("The policy changed since you started editing"), CoreTools.Translate("Overwrite")),
        PolicyEditorConfirmationKind.DiscardChanges =>
            (CoreTools.Translate("Discard unsaved changes?"), CoreTools.Translate("Discard changes")),
        _ => (CoreTools.Translate("Confirm"), CoreTools.Translate("Continue")),
    };

    private static object BuildBody(PolicyEditorConfirmationRequest request)
    {
        var panel = new StackPanel { Spacing = 8 };
        var description = new TextBlock
        {
            Text = DescribeMessage(request),
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
        };
        AutomationProperties.SetName(description, description.Text);
        panel.Children.Add(description);

        if (request.Kind == PolicyEditorConfirmationKind.Warnings && request.Findings.Count > 0)
        {
            var findingsList = new StackPanel
            {
                Spacing = 4,
                Margin = new global::Avalonia.Thickness(0, 4, 0, 0),
            };
            foreach (PolicyValidationFinding finding in request.Findings)
            {
                if (finding.Severity != PolicyValidationSeverity.Warning) continue;
                var warning = new TextBlock
                {
                    Text = $"\u2022 {finding.Message}",
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    Opacity = 0.85,
                    Focusable = true,
                };
                AutomationProperties.SetName(warning, finding.AutomationName);
                findingsList.Children.Add(warning);
            }

            panel.Children.Add(findingsList);
        }

        return panel;
    }

    private static string DescribeMessage(PolicyEditorConfirmationRequest request) => request.Kind switch
    {
        PolicyEditorConfirmationKind.Warnings => CoreTools.Translate(
            "Validation reported {0} warning(s) for policy '{1}'. Do you want to save it anyway?",
            request.Findings.Count(f => f.Severity == PolicyValidationSeverity.Warning),
            request.DraftId),
        PolicyEditorConfirmationKind.ReplaceIdentity => CoreTools.Translate(
            "This will replace the active policy '{0}' with a new policy '{1}'. This cannot be undone.",
            request.ActivePolicyId ?? "?",
            request.DraftId),
        PolicyEditorConfirmationKind.Create => CoreTools.Translate(
            "This will create a new package broker policy '{0}'.",
            request.DraftId),
        PolicyEditorConfirmationKind.Repair => CoreTools.Translate(
            "The stored policy file is invalid and will be replaced with '{0}'.",
            request.DraftId),
        PolicyEditorConfirmationKind.ConfirmOverwrite => request.Operation switch
        {
            PolicyReplacementOperation.Update => CoreTools.Translate(
                "The active policy '{0}' changed since editing began. Overwrite that exact current version with your changes?",
                request.ActivePolicyId ?? request.DraftId),
            PolicyReplacementOperation.ReplaceIdentity => CoreTools.Translate(
                "The policy store now contains active policy '{0}'. Replace it with the different policy identity '{1}'?",
                request.ActivePolicyId ?? "?",
                request.DraftId),
            PolicyReplacementOperation.Create => CoreTools.Translate(
                "The policy store is now missing. Create policy '{0}' against that exact current state?",
                request.DraftId),
            PolicyReplacementOperation.Repair => CoreTools.Translate(
                "The policy store is now invalid. Replace it with repaired policy '{0}' against that exact current state?",
                request.DraftId),
            _ => CoreTools.Translate("Do you want to continue?"),
        },
        PolicyEditorConfirmationKind.DiscardChanges => CoreTools.Translate(
            "You have unsaved changes to policy '{0}'. Discard them?",
            request.DraftId),
        _ => CoreTools.Translate("Do you want to continue?"),
    };
}
