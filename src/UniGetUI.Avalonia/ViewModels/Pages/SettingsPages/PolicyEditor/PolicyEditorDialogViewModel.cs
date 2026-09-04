using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Composite root DataContext for <c>PolicyEditorDialog</c>: bundles the domain
/// <see cref="PolicyEditorSessionViewModel"/> together with the UI-only <see cref="PolicyEditorDocumentUi"/>
/// wrapper and a live <see cref="Rules"/> wrapper collection, so the whole dialog AXAML tree can bind
/// through a single compiled <c>x:DataType</c> instead of juggling several sibling data contexts.
/// The <see cref="Rules"/> collection is only rebuilt after a structural rule-list operation
/// (add/duplicate/delete/move) or a raw→structured mode switch; ordinary field edits mutate the
/// existing <see cref="PolicyEditorRuleUi"/> instances in place so bound controls never lose focus.
/// </summary>
public sealed class PolicyEditorDialogViewModel : ObservableObject, IDisposable
{
    private readonly Action<string?, AutomationLiveSetting> _announce;
    private long _announcedWriteCompletionGeneration;

    public PolicyEditorSessionViewModel Session { get; }

    public PolicyEditorDocumentUi Document { get; }

    public ObservableCollection<PolicyEditorRuleUi> Rules { get; } = [];

    public InfoBarViewModel Status { get; } = new() { IsClosable = false, IsOpen = false };

    public PolicyEditorDialogViewModel(PolicyEditorSessionViewModel session)
        : this(session, AccessibilityAnnouncementService.Announce)
    {
    }

    internal PolicyEditorDialogViewModel(
        PolicyEditorSessionViewModel session,
        Action<string?, AutomationLiveSetting> announce)
    {
        Session = session;
        _announce = announce;
        _announcedWriteCompletionGeneration = session.LastWriteCompletion?.Generation ?? 0;
        Document = new PolicyEditorDocumentUi(session);
        Session.PropertyChanged += OnSessionPropertyChanged;
        RebuildRules();
        RefreshStatus();
    }

    public string Title => Session.Session.Operation switch
    {
        PolicyEditorOperationKind.Update => CoreTools.Translate("Edit policy '{0}'", Session.Draft.Metadata.Id),
        PolicyEditorOperationKind.ReplaceIdentity => CoreTools.Translate("Replace active policy identity"),
        PolicyEditorOperationKind.Create => CoreTools.Translate("Create a new package broker policy"),
        PolicyEditorOperationKind.Repair => CoreTools.Translate("Repair the stored package broker policy"),
        _ => CoreTools.Translate("Package broker policy editor"),
    };

    public bool HasWriteFailure => Session.LastWriteFailureKind != PolicyWriteFailureKind.None
        || Session.LastErrorCode is not null;

    public string WriteFailureMessage => DescribeWriteFailure(Session.LastWriteFailureKind, Session.LastErrorCode);

    /// <summary>
    /// Rebuilds every <see cref="PolicyEditorRuleUi"/> wrapper from the current
    /// <see cref="PolicyEditorSessionViewModel.Rules"/>. Call after any operation that changes the rule
    /// list's identity/order (add/duplicate/delete/move, or a raw→structured switch); never on ordinary
    /// field edits, which mutate existing wrappers in place instead.
    /// </summary>
    public void RebuildRules()
    {
        foreach (PolicyEditorRuleUi rule in Rules)
        {
            rule.Dispose();
        }
        Rules.Clear();
        foreach (PolicyEditorDraftRule rule in Session.Rules)
        {
            Rules.Add(new PolicyEditorRuleUi(rule, Session));
        }
    }

    public void RefreshStructuredProjection()
    {
        Document.RefreshFromDraft();
        RebuildRules();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PolicyEditorSessionViewModel.LastWriteCompletion)
            && Session.LastWriteCompletion is { } completion
            && completion.Generation > _announcedWriteCompletionGeneration)
        {
            _announcedWriteCompletionGeneration = completion.Generation;
            AnnounceWriteCompletion(completion);
        }

        if (e.PropertyName == nameof(PolicyEditorSessionViewModel.Findings))
        {
            foreach (PolicyEditorRuleUi rule in Rules)
            {
                rule.RefreshFindings();
            }

            PolicyValidationFinding? firstError = Session.Findings.FirstOrDefault(
                finding => finding.Severity == PolicyValidationSeverity.Error);
            if (firstError is not null)
            {
                _announce(
                    firstError.AutomationName,
                    AutomationLiveSetting.Assertive);
            }
            else if (Session.Findings.FirstOrDefault() is { } firstWarning)
            {
                _announce(
                    firstWarning.AutomationName,
                    AutomationLiveSetting.Polite);
            }
        }

        if (e.PropertyName is nameof(PolicyEditorSessionViewModel.LastWriteFailureKind)
            or nameof(PolicyEditorSessionViewModel.LastErrorCode))
        {
            OnPropertyChanged(nameof(HasWriteFailure));
            OnPropertyChanged(nameof(WriteFailureMessage));
        }

        if (e.PropertyName is nameof(PolicyEditorSessionViewModel.Draft)
            or nameof(PolicyEditorSessionViewModel.Operation))
        {
            OnPropertyChanged(nameof(Title));
        }
        else if (e.PropertyName == nameof(PolicyEditorSessionViewModel.LastSaveSucceeded)
                 && Session.LastSaveSucceeded
                 && !Session.SavedWithNewerChanges)
        {
            RefreshStructuredProjection();
        }

        if (e.PropertyName == nameof(PolicyEditorSessionViewModel.IsIdentityLocked))
        {
            Document.NotifyIdentityLockChanged();
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (Session.IsBusy)
        {
            SetStatus(
                CoreTools.Translate("Working…"),
                CoreTools.Translate("Contacting Devolutions Agent."),
                InfoBarSeverity.Informational);
            return;
        }

        if (!string.IsNullOrWhiteSpace(Session.StatusMessage))
        {
            SetStatus(
                CoreTools.Translate("Policy operation in progress"),
                Session.StatusMessage,
                InfoBarSeverity.Informational);
            return;
        }

        if (Session.HasLocalInputErrors)
        {
            SetStatus(
                CoreTools.Translate("Correct the highlighted fields"),
                Session.LocalInputErrorSummary,
                InfoBarSeverity.Error,
                announce: false);
            return;
        }

        if (Session.SyntaxError is { } syntaxError)
        {
            SetStatus(
                Session.SyntaxErrorTitle,
                Session.SyntaxErrorMessage,
                InfoBarSeverity.Error,
                announce: false);
            return;
        }

        if (Session.SavedWithNewerChanges)
        {
            SetStatus(
                CoreTools.Translate("Policy saved; newer changes remain"),
                CoreTools.Translate("The policy was saved, but newer draft changes remain unsaved."),
                InfoBarSeverity.Warning,
                announce: !HasAnnouncedWriteCompletion);
            return;
        }

        if (Session.SavedThenSuperseded)
        {
            SetStatus(
                CoreTools.Translate("Policy saved, then replaced again"),
                CoreTools.Translate("The policy was saved, but another writer replaced it before management state was refreshed."),
                InfoBarSeverity.Warning,
                announce: !HasAnnouncedWriteCompletion);
            return;
        }

        if (Session.LastSaveSucceeded)
        {
            SetStatus(
                CoreTools.Translate("Policy saved"),
                CoreTools.Translate("The package broker policy was saved successfully."),
                InfoBarSeverity.Success,
                announce: !HasAnnouncedWriteCompletion);
            return;
        }

        if (Session.HasConflict)
        {
            SetStatus(
                CoreTools.Translate("The policy changed since you started editing"),
                CoreTools.Translate("Review your changes, then choose Overwrite to save anyway."),
                InfoBarSeverity.Warning,
                announce: !HasAnnouncedWriteCompletion);
            return;
        }

        if (HasWriteFailure)
        {
            SetStatus(
                CoreTools.Translate("The policy could not be saved"),
                WriteFailureMessage,
                InfoBarSeverity.Error,
                announce: Session.LastWriteFailureKind == PolicyWriteFailureKind.None
                    || !HasAnnouncedWriteCompletion);
            return;
        }

        if (Session.HasFindings)
        {
            int errorCount = Session.Findings.Count(finding => finding.Severity == PolicyValidationSeverity.Error);
            SetStatus(
                errorCount > 0
                    ? CoreTools.Translate("Validation found errors")
                    : CoreTools.Translate("Validation found warnings"),
                CoreTools.Translate("Review the findings below before saving."),
                errorCount > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                announce: false);
            return;
        }

        Status.IsOpen = false;
    }

    private void SetStatus(
        string title,
        string message,
        InfoBarSeverity severity,
        bool announce = true)
    {
        bool changed = !Status.IsOpen
            || Status.Title != title
            || Status.Message != message
            || Status.Severity != severity;
        Status.Title = title;
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
        if (changed && announce)
        {
            AnnounceStatus();
        }
    }

    private void AnnounceStatus()
    {
        string message = string.IsNullOrEmpty(Status.Message)
            ? Status.Title
            : $"{Status.Title}. {Status.Message}";
        _announce(
            message,
            Status.Severity == InfoBarSeverity.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    private bool HasAnnouncedWriteCompletion =>
        Session.LastWriteCompletion is { } completion
        && completion.Generation <= _announcedWriteCompletionGeneration;

    private void AnnounceWriteCompletion(PolicyEditorWriteCompletion completion)
    {
        (string title, string message, InfoBarSeverity severity) = completion.Kind switch
        {
            PolicyEditorWriteCompletionKind.SavedWithNewerChanges => (
                CoreTools.Translate("Policy saved; newer changes remain"),
                CoreTools.Translate("The policy was saved, but newer draft changes remain unsaved."),
                InfoBarSeverity.Warning),
            PolicyEditorWriteCompletionKind.SavedThenSuperseded => (
                CoreTools.Translate("Policy saved, then replaced again"),
                CoreTools.Translate("The policy was saved, but another writer replaced it before management state was refreshed."),
                InfoBarSeverity.Warning),
            PolicyEditorWriteCompletionKind.Saved => (
                CoreTools.Translate("Policy saved"),
                CoreTools.Translate("The package broker policy was saved successfully."),
                InfoBarSeverity.Success),
            PolicyEditorWriteCompletionKind.Conflict => (
                CoreTools.Translate("The policy changed since you started editing"),
                CoreTools.Translate("Review your changes, then choose Overwrite to save anyway."),
                InfoBarSeverity.Warning),
            _ => (
                CoreTools.Translate("The policy could not be saved"),
                DescribeWriteFailure(completion.FailureKind, completion.ErrorCode),
                InfoBarSeverity.Error),
        };

        _announce(
            $"{title}. {message}",
            severity == InfoBarSeverity.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    private static string DescribeWriteFailure(PolicyWriteFailureKind kind, ErrorCode? errorCode)
    {
        string? reason = kind switch
        {
            PolicyWriteFailureKind.UacCanceled =>
                CoreTools.Translate("The elevation prompt was dismissed. No changes were saved."),
            PolicyWriteFailureKind.LaunchFailed =>
                CoreTools.Translate("The elevated helper could not be started."),
            PolicyWriteFailureKind.AuthenticationFailed =>
                CoreTools.Translate("The elevated helper could not be authenticated."),
            PolicyWriteFailureKind.ProtocolFailed =>
                CoreTools.Translate("Communication with the elevated helper failed."),
            PolicyWriteFailureKind.HelperFailed =>
                CoreTools.Translate("The elevated helper stopped unexpectedly."),
            PolicyWriteFailureKind.BrokerRejected =>
                CoreTools.Translate("Devolutions Agent rejected the policy replacement."),
            PolicyWriteFailureKind.WriteResultUnknown =>
                CoreTools.Translate("The policy write result is unknown. Refresh policy management state before retrying."),
            _ => null,
        };

        if (errorCode is { } code)
        {
            string codeText = CoreTools.Translate(code.ToString());
            return reason is null
                ? CoreTools.Translate("The save failed ({0}).", codeText)
                : CoreTools.Translate("{0} ({1})", reason, codeText);
        }

        return reason ?? CoreTools.Translate("The save failed.");
    }

    public void Dispose()
    {
        Session.PropertyChanged -= OnSessionPropertyChanged;
        foreach (PolicyEditorRuleUi rule in Rules)
        {
            rule.Dispose();
        }
        Session.Dispose();
    }
}
