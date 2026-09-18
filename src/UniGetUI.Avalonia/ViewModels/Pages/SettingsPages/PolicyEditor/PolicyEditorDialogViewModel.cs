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
    private long _handledFindingNavigationGeneration;
    private int _selectedFindingIndex = -1;

    public PolicyEditorSessionViewModel Session { get; }

    public PolicyEditorDocumentUi Document { get; }

    public ObservableCollection<PolicyEditorRuleUi> Rules { get; } = [];

    public InfoBarViewModel Status { get; } = new() { IsClosable = false, IsOpen = false };
    public event EventHandler<PolicyValidationFinding?>? FindingNavigationRequested;

    public PolicyValidationFinding? SelectedFinding =>
        _selectedFindingIndex >= 0 && _selectedFindingIndex < Session.Findings.Count
            ? Session.Findings[_selectedFindingIndex]
            : null;
    public bool HasFindingSummary => Session.SyntaxError is not null || SelectedFinding is not null;
    public bool HasMultipleFindings => Session.SyntaxError is null && Session.Findings.Count > 1;
    public bool CanNavigateFinding =>
        !(Session.IsRawMode && Session.IsRawSyntaxPending);
    public string FindingCountText
    {
        get
        {
            if (Session.SyntaxError is not null)
                return CoreTools.Translate("1 error");
            int errors = Session.Findings.Count(finding =>
                finding.Severity == PolicyValidationSeverity.Error);
            int warnings = Session.Findings.Count(finding =>
                finding.Severity == PolicyValidationSeverity.Warning);
            return CoreTools.Translate(
                "{0} error(s), {1} warning(s)",
                errors,
                warnings);
        }
    }
    public string SelectedFindingMessage =>
        Session.SyntaxError is not null
            ? Session.SyntaxErrorMessage
            : SelectedFinding?.Message ?? "";

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
        _handledFindingNavigationGeneration = session.FindingNavigationGeneration;
        Document = new PolicyEditorDocumentUi(session);
        Session.PropertyChanged += OnSessionPropertyChanged;
        RebuildRules();
        SelectFirstFinding(navigate: false);
        RefreshStatus();
    }

    public string Title => Session.Session.Operation switch
    {
        PolicyEditorOperationKind.Update => CoreTools.Translate("Edit policy {0}", Session.Draft.Metadata.Id),
        PolicyEditorOperationKind.ReplaceIdentity => CoreTools.Translate("Replace active policy identity"),
        PolicyEditorOperationKind.Create => CoreTools.Translate("Create a new package broker policy"),
        _ => CoreTools.Translate("Package broker policy editor"),
    };

    public bool HasWriteFailure => Session.LastWriteFailureKind != PolicyWriteFailureKind.None
        || Session.LastErrorCode is not null;

    public string WriteFailureMessage => DescribeWriteFailure(
        Session.LastWriteFailureKind,
        Session.LastErrorCode,
        Session.LastWriteDiagnosticCode);

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
        for (int index = 0; index < Session.Rules.Count; index++)
        {
            Rules.Add(new PolicyEditorRuleUi(Session.Rules[index], index, Session));
        }
    }

    public void RefreshStructuredProjection()
    {
        Document.RefreshFromDraft();
        RebuildRules();
    }

    public void AnnounceRulePosition(PolicyEditorDraftRule rule)
    {
        int? index = Session.Rules
            .Select((candidate, candidateIndex) => (candidate, candidateIndex))
            .Where(item => ReferenceEquals(item.candidate, rule))
            .Select(item => (int?)item.candidateIndex)
            .FirstOrDefault();
        if (index is null) return;
        _announce(
            CoreTools.Translate(
                "Rule {0} is now position {1} of {2}.",
                rule.Id,
                index.Value + 1,
                Session.Rules.Count),
            AutomationLiveSetting.Polite);
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
            Document.RefreshFindings();
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

            SelectFirstFinding(navigate: false);
        }
        else if (e.PropertyName == nameof(PolicyEditorSessionViewModel.FindingNavigationGeneration)
                 && Session.FindingNavigationGeneration > _handledFindingNavigationGeneration)
        {
            _handledFindingNavigationGeneration = Session.FindingNavigationGeneration;
            SelectFirstFinding(navigate: true);
        }
        else if (e.PropertyName == nameof(PolicyEditorSessionViewModel.SyntaxError))
        {
            RefreshFindingSummary();
            if (Session.SyntaxError is not null)
                FindingNavigationRequested?.Invoke(this, null);
        }

        if (e.PropertyName is nameof(PolicyEditorSessionViewModel.LastWriteFailureKind)
            or nameof(PolicyEditorSessionViewModel.LastErrorCode)
            or nameof(PolicyEditorSessionViewModel.LastWriteDiagnosticCode))
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

        if (e.PropertyName is nameof(PolicyEditorSessionViewModel.IsRawMode)
            or nameof(PolicyEditorSessionViewModel.IsRawSyntaxPending))
        {
            OnPropertyChanged(nameof(CanNavigateFinding));
        }

        RefreshStatus();
    }

    public void SelectPreviousFinding()
    {
        if (Session.Findings.Count == 0) return;
        _selectedFindingIndex =
            (_selectedFindingIndex - 1 + Session.Findings.Count) % Session.Findings.Count;
        RefreshFindingSummary();
        RequestFindingNavigation(SelectedFinding);
    }

    public void SelectNextFinding()
    {
        if (Session.Findings.Count == 0) return;
        _selectedFindingIndex =
            (_selectedFindingIndex + 1) % Session.Findings.Count;
        RefreshFindingSummary();
        RequestFindingNavigation(SelectedFinding);
    }

    public void NavigateToSelectedFinding() =>
        RequestFindingNavigation(SelectedFinding);

    private void SelectFirstFinding(bool navigate)
    {
        PolicyValidationFinding? firstError = Session.Findings.FirstOrDefault(
            finding => finding.Severity == PolicyValidationSeverity.Error);
        PolicyValidationFinding? selected = firstError ?? Session.Findings.FirstOrDefault();
        _selectedFindingIndex = selected is null
            ? -1
            : Session.Findings
                .Select((finding, index) => (finding, index))
                .Where(item => ReferenceEquals(item.finding, selected))
                .Select(item => item.index)
                .FirstOrDefault();
        RefreshFindingSummary();
        if (navigate && selected is not null)
            RequestFindingNavigation(selected);
    }

    private void RequestFindingNavigation(PolicyValidationFinding? finding)
    {
        if (CanNavigateFinding)
            FindingNavigationRequested?.Invoke(this, finding);
    }

    private void RefreshFindingSummary()
    {
        OnPropertyChanged(nameof(SelectedFinding));
        OnPropertyChanged(nameof(HasFindingSummary));
        OnPropertyChanged(nameof(HasMultipleFindings));
        OnPropertyChanged(nameof(FindingCountText));
        OnPropertyChanged(nameof(SelectedFindingMessage));
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
                CoreTools.Translate("Correct the selected error or review the warning before saving."),
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
                DescribeWriteFailure(
                    completion.FailureKind,
                    completion.ErrorCode,
                    completion.DiagnosticCode),
                InfoBarSeverity.Error),
        };

        _announce(
            $"{title}. {message}",
            severity == InfoBarSeverity.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    internal static string DescribeWriteFailure(
        PolicyWriteFailureKind kind,
        ErrorCode? errorCode,
        string? diagnosticCode = null)
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
                DescribeUnknownWriteResult(diagnosticCode),
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

    private static string DescribeUnknownWriteResult(string? diagnosticCode)
    {
        string cause = diagnosticCode switch
        {
            nameof(Devolutions.Now.Policy.Client.BrokerClientErrorKind.BrokerUnavailable) =>
                CoreTools.Translate("Devolutions Agent was unavailable or closed the connection before responding."),
            nameof(Devolutions.Now.Policy.Client.BrokerClientErrorKind.Timeout) =>
                CoreTools.Translate("Communication with Devolutions Agent timed out."),
            nameof(Devolutions.Now.Policy.Client.BrokerClientErrorKind.EmptyResponse) =>
                CoreTools.Translate("Devolutions Agent closed the connection without a response."),
            nameof(Devolutions.Now.Policy.Client.BrokerClientErrorKind.InvalidResponse) =>
                CoreTools.Translate("Devolutions Agent returned an invalid policy response."),
            PolicyWriteDiagnosticCodes.PostCommitRefreshTimeout =>
                CoreTools.Translate("The policy was saved, but refreshing the current policy state timed out."),
            PolicyWriteDiagnosticCodes.PostCommitRefreshUnavailable =>
                CoreTools.Translate("The policy was saved, but the current policy state could not be refreshed."),
            _ => CoreTools.Translate("The policy write result could not be confirmed."),
        };
        return CoreTools.Translate(
            "{0} The policy write result is unknown. Refresh policy management state before retrying.",
            cause);
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
