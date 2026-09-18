using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.Avalonia.Views.Controls;
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
        EnsureOwnerVisible();
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
            () => Dispatcher.UIThread.Post(dialog.CancelPendingChoice));
        await dialog.ShowDialog(_owner);
        return !cancellationToken.IsCancellationRequested && dialog.Result == true;
    }

    private void EnsureOwnerVisible()
    {
        if (!_owner.IsVisible)
            _owner.Show();
        if (_owner.WindowState == WindowState.Minimized)
            _owner.WindowState = WindowState.Normal;
        _owner.Activate();
    }

    private static (string Title, string PrimaryText) DescribeAction(PolicyEditorConfirmationKind kind) => kind switch
    {
        PolicyEditorConfirmationKind.Warnings =>
            (CoreTools.Translate("Save policy with warnings?"), CoreTools.Translate("Save anyway")),
        PolicyEditorConfirmationKind.EnableAuditMode =>
            (CoreTools.Translate("Enable Audit mode?"), CoreTools.Translate("Enable Audit mode")),
        PolicyEditorConfirmationKind.EnableDefaultAllow =>
            (CoreTools.Translate("Allow unmatched package requests?"), CoreTools.Translate("Use Allow as default")),
        PolicyEditorConfirmationKind.RemoveAllowSafetyLimits =>
            (CoreTools.Translate("Change this rule to Deny?"), CoreTools.Translate("Remove limits and change")),
        PolicyEditorConfirmationKind.ReplaceIdentity =>
            (CoreTools.Translate("Replace the active policy?"), CoreTools.Translate("Replace")),
        PolicyEditorConfirmationKind.Create =>
            (CoreTools.Translate("Create a new policy?"), CoreTools.Translate("Create")),
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
        PolicyHelp.SetText(
            description,
            request.Kind == PolicyEditorConfirmationKind.Warnings
                ? CoreTools.Translate("Saving with warnings acknowledges the listed risks but does not ignore validation errors.")
                : description.Text);
        panel.Children.Add(description);

        if (request.Kind == PolicyEditorConfirmationKind.Warnings && request.Findings.Count > 0)
        {
            var findingsList = new StackPanel
            {
                Spacing = 6,
                Margin = new global::Avalonia.Thickness(0, 4, 0, 0),
            };
            foreach (string message in GetWarningPresentationMessages(request.Findings))
            {
                var warning = new TextBlock
                {
                    Text = message,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    Focusable = true,
                };
                AutomationProperties.SetName(warning, message);
                var callout = new Border
                {
                    Padding = new global::Avalonia.Thickness(10, 8),
                    CornerRadius = new global::Avalonia.CornerRadius(6),
                    Margin = new global::Avalonia.Thickness(0),
                    Child = warning,
                };
                callout.Classes.Add("warning-banner");
                AutomationProperties.SetName(callout, message);
                findingsList.Children.Add(callout);
            }

            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 240,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = findingsList,
            });
        }

        return panel;
    }

    internal static IReadOnlyList<string> GetWarningPresentationMessages(
        IReadOnlyList<PolicyValidationFinding> findings)
    {
        var presented = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<string>();
        foreach (PolicyValidationFinding finding in findings)
        {
            if (!finding.IsWarning || !presented.Add(finding.ConfirmationMessage))
                continue;
            messages.Add(finding.ConfirmationMessage);
        }
        return messages;
    }

    internal static string DescribeMessage(PolicyEditorConfirmationRequest request) => request.Kind switch
    {
        PolicyEditorConfirmationKind.Warnings => CoreTools.Translate(
            "Validation reported {0} allowed behavior warning(s) for policy {1}. Review them and choose Save anyway only to acknowledge these behaviors intentionally.",
            request.WarningCount,
            request.DraftId),
        PolicyEditorConfirmationKind.EnableAuditMode => CoreTools.Translate(
            "Audit mode still evaluates and logs policy decisions, but requests the policy would deny will be permitted. Enable Audit mode and save this policy?"),
        PolicyEditorConfirmationKind.EnableDefaultAllow => CoreTools.Translate(
            "The default decision will permit every package request that does not match an enabled rule. Use Allow as the default and save this policy?"),
        PolicyEditorConfirmationKind.RemoveAllowSafetyLimits => CoreTools.Translate(
            "Rule {0} has Additional safety limits that apply only to Allow rules. Changing it to Deny will remove those limits. Continue?",
            request.RuleId ?? "?"),
        PolicyEditorConfirmationKind.ReplaceIdentity => CoreTools.Translate(
            "This will replace the active policy {0} with a new policy {1}. This cannot be undone.",
            request.ActivePolicyId ?? "?",
            request.DraftId),
        PolicyEditorConfirmationKind.Create => CoreTools.Translate(
            "This will create a new package broker policy {0}.",
            request.DraftId),
        PolicyEditorConfirmationKind.ConfirmOverwrite => request.Operation switch
        {
            PolicyReplacementOperation.Update => CoreTools.Translate(
                "The active policy {0} changed since editing began. Overwrite that exact current version with your changes?",
                request.ActivePolicyId ?? request.DraftId),
            PolicyReplacementOperation.ReplaceIdentity => CoreTools.Translate(
                "The policy store now contains active policy {0}. Replace it with the different policy identity {1}?",
                request.ActivePolicyId ?? "?",
                request.DraftId),
            PolicyReplacementOperation.Create => CoreTools.Translate(
                "The policy store is now missing. Create policy {0} against that exact current state?",
                request.DraftId),
            _ => CoreTools.Translate("Do you want to continue?"),
        },
        PolicyEditorConfirmationKind.DiscardChanges =>
            string.IsNullOrWhiteSpace(request.DraftId)
                ? CoreTools.Translate("You have unsaved policy changes. Discard them?")
                : CoreTools.Translate(
                    "You have unsaved changes to policy {0}. Discard them?",
                    request.DraftId),
        _ => CoreTools.Translate("Do you want to continue?"),
    };
}
