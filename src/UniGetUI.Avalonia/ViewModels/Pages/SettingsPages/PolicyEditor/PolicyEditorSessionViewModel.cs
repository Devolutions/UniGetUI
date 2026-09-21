using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

public partial class PolicyEditorSessionViewModel : ViewModelBase, IDisposable
{
    private readonly IPolicyValidationClient _validationClient;
    private readonly IPolicyEditorConfirmationPrompt _confirmationPrompt;
    private readonly IPolicyWriteClient _writeClient;
    private readonly TimeSpan _rawSyntaxDebounce;
    private readonly TimeSpan _structuredDirtyDebounce;
    private readonly Func<PolicyEditorDraftDocument, string> _structuredDraftSerializer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _discardConfirmationLock = new();
    private readonly Dictionary<object, string> _localInputErrors = [];
    private readonly HashSet<PolicyEditorDraftRule> _deferredBlankRules =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PolicyEditorDraftRule, HashSet<PolicyEditorAdvisories.PolicyEditorRisk>>
        _explicitMatchCharacteristics = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _rawSyntaxCancellation;
    private Task _rawSyntaxAnalysis = Task.CompletedTask;
    private CancellationTokenSource? _structuredDirtyCancellation;
    private Task _structuredDirtyAnalysis = Task.CompletedTask;
    private CancellationTokenSource? _authoritativeValidationCancellation;
    private Task _authoritativeValidation = Task.CompletedTask;
    private Task<bool>? _discardConfirmationTask;
    private long _saveGeneration;
    private long _findingNavigationGeneration;
    private bool _hasLocalSemanticErrors;
    private int _isDisposed;

    public PolicyEditorSession Session { get; }

    public PolicyEditorDraftDocument Draft => Session.Draft;
    public IReadOnlyList<PolicyEditorDraftRule> Rules => Session.Draft.Rules;
    public PolicyEditorOperationKind Operation => Session.Operation;
    public bool IsStructuredMode => Session.Mode == PolicyEditorMode.Structured;
    public bool IsRawMode => Session.Mode == PolicyEditorMode.Raw;
    public bool IsDirty => Session.IsDirty || HasLocalInputErrors;
    public bool IsIdentityLocked => Session.IsIdentityLocked;
    public bool HasFindings => Session.Findings.All.Count > 0;
    public bool HasConflict => Session.Conflict is not null;
    public IReadOnlyList<PolicyValidationFinding> Findings => Session.Findings.All;
    public bool IsRawSyntaxPending => Session.IsRawAnalysisPending;
    public string SyntaxErrorTitle => SyntaxError?.Kind switch
    {
        PolicyEditorSyntaxErrorKind.EmptyDocument or PolicyEditorSyntaxErrorKind.InvalidJson =>
            CoreTools.Translate("The document is not valid JSON"),
        _ => CoreTools.Translate("The document is not a valid policy draft"),
    };
    public string SyntaxErrorMessage => SyntaxError?.Kind switch
    {
        PolicyEditorSyntaxErrorKind.EmptyDocument =>
            CoreTools.Translate("The document is empty."),
        PolicyEditorSyntaxErrorKind.InvalidJson =>
            CoreTools.Translate("The JSON syntax is invalid."),
        PolicyEditorSyntaxErrorKind.LegacySchemaField =>
            CoreTools.Translate("The $schema field is obsolete. Remove it."),
        PolicyEditorSyntaxErrorKind.LegacyPolicyVersionField =>
            CoreTools.Translate("PolicyVersion is obsolete. Rename it to PolicyFormatVersion."),
        PolicyEditorSyntaxErrorKind.MissingPolicyFormatVersion =>
            CoreTools.Translate("The policy draft is missing PolicyFormatVersion."),
        PolicyEditorSyntaxErrorKind.InvalidPolicyFormatVersion =>
            CoreTools.Translate("PolicyFormatVersion must be a canonical three-part numeric version such as 1.0.0."),
        PolicyEditorSyntaxErrorKind.UnsupportedPolicyFormatVersion =>
            CoreTools.Translate("The policy draft uses an unsupported policy format version. This version supports major version 1."),
        PolicyEditorSyntaxErrorKind.MissingEnforcement =>
            CoreTools.Translate("The policy draft is missing the Enforcement object."),
        PolicyEditorSyntaxErrorKind.MissingMetadata =>
            CoreTools.Translate("The policy draft is missing the Metadata object."),
        _ => CoreTools.Translate("The document does not match the policy draft format."),
    };
    public bool HasLocalInputErrors => _localInputErrors.Count > 0;
    public bool HasLocalSemanticErrors => _hasLocalSemanticErrors;
    public string LocalInputErrorSummary => string.Join(Environment.NewLine, _localInputErrors.Values);
    public bool CanValidateOrSave => CanStartRemoteOperation();
    public bool CanSwitchToRaw => CanSwitchStructuredToRaw();
    public bool CanSwitchToStructured => CanProjectRawToStructured();
    public long FindingNavigationGeneration => _findingNavigationGeneration;

    public string RawBuffer
    {
        get => Session.RawBuffer;
        set
        {
            if (Session.Mode != PolicyEditorMode.Raw
                || string.Equals(Session.RawBuffer, value, StringComparison.Ordinal))
                return;

            Session.SetRawBuffer(value);
            SyntaxError = null;
            ScheduleRawSyntaxAnalysis(value);
            OnEditorStateChanged();
        }
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private PolicyEditorSyntaxError? _syntaxError;
    [ObservableProperty] private bool _lastSaveSucceeded;
    [ObservableProperty] private bool _savedWithNewerChanges;
    [ObservableProperty] private bool _savedThenSuperseded;
    [ObservableProperty] private bool _requiresManagementRefresh;
    [ObservableProperty] private ErrorCode? _lastErrorCode;
    [ObservableProperty] private PolicyWriteFailureKind _lastWriteFailureKind;
    [ObservableProperty] private string? _lastWriteDiagnosticCode;
    internal PolicyEditorWriteCompletion? LastWriteCompletion { get; private set; }

    public PolicyEditorSessionViewModel(
        PolicyEditorSession session,
        IPolicyValidationClient validationClient,
        IPolicyEditorConfirmationPrompt confirmationPrompt,
        IPolicyWriteClient writeClient,
        TimeSpan? rawSyntaxDebounce = null,
        TimeSpan? structuredDirtyDebounce = null,
        Func<PolicyEditorDraftDocument, string>? structuredDraftSerializer = null)
    {
        Session = session;
        _validationClient = validationClient;
        _confirmationPrompt = confirmationPrompt;
        _writeClient = writeClient;
        _rawSyntaxDebounce = rawSyntaxDebounce ?? TimeSpan.FromMilliseconds(300);
        _structuredDirtyDebounce = structuredDirtyDebounce ?? TimeSpan.FromMilliseconds(300);
        _structuredDraftSerializer =
            structuredDraftSerializer ?? PolicyEditorRawSyntax.ToCanonicalRaw;
        if (Session.Findings.All.Count == 0)
        {
            RefreshLocalSemanticValidation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchStructuredToRaw))]
    private void SwitchToRaw()
    {
        if (Session.Mode != PolicyEditorMode.Structured)
            return;

        Session.SwitchToRaw();
        RefreshLocalSemanticValidation();
        CancelStructuredDirtyAnalysis();
        CancelRawSyntaxAnalysis();
        SyntaxError = null;
        OnEditorStateChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanProjectRawToStructured))]
    private Task SwitchToStructuredAsync(CancellationToken cancellationToken)
    {
        if (Session.Mode != PolicyEditorMode.Raw || !CanProjectRawToStructured())
            return Task.CompletedTask;
        string submitted = Session.RawBuffer;
        if (!PolicyEditorRawSyntax.TryParseStrict(
                submitted,
                out PolicyEditorDraftDocument? draft,
                out PolicyEditorSyntaxError? syntaxError))
        {
            SyntaxError = syntaxError;
            return Task.CompletedTask;
        }

        CancelRawSyntaxAnalysis();
        Session.ProjectRawToStructured(submitted, draft!);
        RefreshLocalSemanticValidation();
        SyntaxError = null;
        LastErrorCode = null;
        OnEditorStateChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void NotifyDraftChanged()
    {
        if (Session.IsIdentityLocked
            && Session.OriginManagement.Policy is { } origin
            && !string.Equals(
                Session.Draft.Metadata.Id,
                origin.Metadata.Id,
                StringComparison.Ordinal))
        {
            Session.Draft.Metadata.Id = origin.Metadata.Id;
        }

        Session.NotifyDraftChanged();
        OnStructuredDraftChanged();
    }

    internal async Task ChangeRuleDecisionAsync(
        PolicyEditorRuleUi ruleUi,
        int selectedIndex,
        CancellationToken cancellationToken = default)
    {
        if (selectedIndex < 0
            || selectedIndex >= PolicyEditorEnumDisplay.Decisions.Length
            || Volatile.Read(ref _isDisposed) != 0
            || IsBusy)
        {
            ruleUi.RefreshDecisionPresentation();
            return;
        }

        Devolutions.Now.Policy.Model.Decision selected =
            PolicyEditorEnumDisplay.Decisions[selectedIndex];
        if (selected == ruleUi.Rule.Decision)
        {
            ruleUi.RefreshDecisionPresentation();
            return;
        }

        if (selected == Devolutions.Now.Policy.Model.Decision.Deny
            && PolicyEditorRuleSemantics.HasConfiguredSafetyLimits(ruleUi.Rule.Constraints))
        {
            using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
            IsBusy = true;
            bool confirmed;
            try
            {
                confirmed = await _confirmationPrompt.ConfirmAsync(
                    new PolicyEditorConfirmationRequest(
                        PolicyEditorConfirmationKind.RemoveAllowSafetyLimits,
                        GetInitialOperation(),
                        Session.Draft.Metadata.Id,
                        Session.OriginManagement.StoreToken,
                        Session.OriginManagement.State,
                        Session.OriginManagement.Policy?.Metadata.Id,
                        Findings,
                        RuleId: ruleUi.Rule.Id),
                    linked.Token);
            }
            finally
            {
                IsBusy = false;
            }

            if (!confirmed || !CanApply(linked.Token))
            {
                ruleUi.RefreshDecisionPresentation();
                return;
            }
        }

        if (ruleUi.Rule.Decision == Devolutions.Now.Policy.Model.Decision.Deny
            && selected == Devolutions.Now.Policy.Model.Decision.Allow)
        {
            ApplySafeAllowMatchDefaults(ruleUi.Rule);
        }

        ruleUi.ApplyDecision(selected);
        OnEditorStateChanged();
    }

    public void NotifyLocalInputChanged()
    {
        Session.NotifyDraftChanged();
        OnStructuredDraftChanged();
    }

    internal void MarkMatchCharacteristicConfigured(
        PolicyEditorDraftRule rule,
        PolicyEditorAdvisories.PolicyEditorRisk risk)
    {
        if (!_explicitMatchCharacteristics.TryGetValue(rule, out HashSet<PolicyEditorAdvisories.PolicyEditorRisk>? configured))
        {
            configured = [];
            _explicitMatchCharacteristics.Add(rule, configured);
        }

        configured.Add(risk);
    }

    public void SetLocalInputError(object key, string? message)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrEmpty(message))
            _localInputErrors.Remove(key);
        else
            _localInputErrors[key] = message;

        OnPropertyChanged(nameof(HasLocalInputErrors));
        OnPropertyChanged(nameof(LocalInputErrorSummary));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanValidateOrSave));
        OnPropertyChanged(nameof(CanSwitchToRaw));
        OnPropertyChanged(nameof(CanSwitchToStructured));
        NotifyCommandStates();
    }

    [RelayCommand]
    private void AddRule()
    {
        PolicyEditorDraftRule rule = Session.AddRule();
        _deferredBlankRules.Add(rule);
        OnStructuredDraftChanged();
    }

    [RelayCommand]
    private void DuplicateRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        Session.DuplicateRule(rule);
        OnStructuredDraftChanged();
    }

    [RelayCommand]
    private void ToggleRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        Session.SetRuleEnabled(rule, !rule.Enabled);
        OnStructuredDraftChanged();
    }

    [RelayCommand]
    private void DeleteRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        _deferredBlankRules.Remove(rule);
        Session.DeleteRule(rule);
        OnStructuredDraftChanged();
    }

    [RelayCommand]
    private void MoveRuleUp(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        int index = Session.Draft.Rules.IndexOf(rule);
        Session.MoveRule(rule, index - 1);
        OnStructuredDraftChanged();
    }

    [RelayCommand]
    private void MoveRuleDown(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        int index = Session.Draft.Rules.IndexOf(rule);
        Session.MoveRule(rule, index + 1);
        OnStructuredDraftChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRemoteOperation))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linked.Token;
        PromoteDeferredBlankRules();
        if (!CanStartRemoteOperation())
        {
            if (HasLocalSemanticErrors)
                RequestFindingNavigation();
            return;
        }

        try
        {
            await SaveCoreAsync(
                conflict: null,
                PolicyConflictHandling.Reject,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRemoteOperation))]
    private async Task ConfirmOverwriteAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linked.Token;
        if (!CanStartRemoteOperation()) return;

        PolicyEditorConflictSnapshot? conflict = Session.Conflict;
        if (conflict is null || !Session.IsConflictCurrent(conflict))
        {
            Session.ClearConflict();
            OnEditorStateChanged();
            return;
        }

        var confirmation = new PolicyEditorConfirmationRequest(
            PolicyEditorConfirmationKind.ConfirmOverwrite,
            conflict.RetryDecision.Operation,
            conflict.DraftId,
            conflict.RetryDecision.Token,
            conflict.RetryDecision.State,
            conflict.RetryDecision.ActivePolicyId,
            Findings);
        bool confirmed;
        IsBusy = true;
        try
        {
            confirmed = await _confirmationPrompt.ConfirmAsync(confirmation, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
        if (!confirmed || !CanApply(cancellationToken)) return;

        if (!CanApply(cancellationToken) || !Session.IsConflictCurrent(conflict))
        {
            Session.ClearConflict();
            OnEditorStateChanged();
            return;
        }

        try
        {
            await SaveCoreAsync(
                conflict,
                PolicyConflictHandling.ConfirmOverwrite,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task<bool> ConfirmDiscardAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<bool> confirmation;
        lock (_discardConfirmationLock)
        {
            if (_discardConfirmationTask is null)
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                confirmation = completion.Task;
                _discardConfirmationTask = confirmation;
                _ = CompleteDiscardConfirmationAsync(completion);
            }
            else
            {
                confirmation = _discardConfirmationTask;
            }
        }

        return await confirmation.WaitAsync(cancellationToken);
    }

    private async Task CompleteDiscardConfirmationAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            bool result = await ConfirmDiscardCoreAsync(_lifetimeCancellation.Token);
            lock (_discardConfirmationLock)
            {
                completion.TrySetResult(result);
                ClearDiscardConfirmation(completion.Task);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (_discardConfirmationLock)
            {
                completion.TrySetCanceled(ex.CancellationToken);
                ClearDiscardConfirmation(completion.Task);
            }
        }
        catch (Exception ex)
        {
            lock (_discardConfirmationLock)
            {
                completion.TrySetException(ex);
                ClearDiscardConfirmation(completion.Task);
            }
        }
    }

    private void ClearDiscardConfirmation(Task<bool> confirmation)
    {
        if (ReferenceEquals(_discardConfirmationTask, confirmation))
            _discardConfirmationTask = null;
    }

    private async Task<bool> ConfirmDiscardCoreAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            StatusMessage = CoreTools.Translate(
                "Please wait for the current policy operation to finish before closing.");
            return false;
        }

        if (Session.IsDirty)
            ReconcileDirtyAtBoundary(Session.GetEffectiveRawJson());
        if (!IsDirty)
            return true;

        PolicyReplacementOperation operation = GetInitialOperation();
        return await _confirmationPrompt.ConfirmAsync(
            new PolicyEditorConfirmationRequest(
                PolicyEditorConfirmationKind.DiscardChanges,
                operation,
                Session.Draft.Metadata.Id,
                Session.OriginManagement.StoreToken,
                Session.OriginManagement.State,
                Session.OriginManagement.Policy?.Metadata.Id,
                Findings),
            cancellationToken);
    }

    private async Task SaveCoreAsync(
        PolicyEditorConflictSnapshot? conflict,
        PolicyConflictHandling conflictHandling,
        CancellationToken cancellationToken)
    {
        if (!CanStartRemoteOperation()) return;

        long saveGeneration = Interlocked.Increment(ref _saveGeneration);
        IsBusy = true;
        LastSaveSucceeded = false;
        SavedWithNewerChanges = false;
        SavedThenSuperseded = false;
        LastErrorCode = null;
        LastWriteFailureKind = PolicyWriteFailureKind.None;
        LastWriteDiagnosticCode = null;
        try
        {
            string submitted = Session.GetEffectiveRawJson();
            long attemptGeneration = Session.MutationGeneration;
            ReconcileDirtyAtBoundary(submitted);

            // Correction #14: reuse the exact current validation (same receipt/CanonicalDraft) when
            // it still matches the unchanged draft/raw, instead of re-validating on every Save. A
            // stale-token retry (ConfirmOverwrite) always revalidates to obtain a current receipt
            // per correction #16, since the previously submitted receipt was already rejected by the
            // write that produced the conflict.
            PolicyEditorValidationState? validation =
                conflictHandling != PolicyConflictHandling.ConfirmOverwrite && Session.IsValidationCurrent
                    ? Session.Validation
                    : null;

            if (validation is null)
            {
                if (!TryGetDraftElement(
                        submitted,
                        out JsonElement submittedElement,
                        out PolicyEditorSyntaxError? error))
                {
                    SyntaxError = error;
                    return;
                }

                PolicyEditorValidationOutcome validationOutcome =
                    await _validationClient.ValidateAsync(submittedElement, cancellationToken);
                if (!CanApply(cancellationToken)
                    || saveGeneration != Volatile.Read(ref _saveGeneration)
                    || Session.MutationGeneration != attemptGeneration)
                    return;
                if (validationOutcome.Validation is null)
                {
                    LastErrorCode = validationOutcome.ErrorCode;
                    return;
                }

                Session.ApplyValidationResult(
                    submitted,
                    validationOutcome.Validation,
                    validationOutcome.BoundedFindings,
                    validationOutcome.OmittedFindingCount);
                OnEditorStateChanged();
                validation = Session.Validation;
                if (validation is null)
                {
                    if (Session.Findings.All.Any(finding => finding.IsError))
                        RequestFindingNavigation();
                    return;
                }
            }

            string canonicalRaw = PolicySerializer.Serialize(validation.CanonicalDraft);

            PolicyReplacementOperation operation;
            string token;
            PolicyManagementState state;
            string? activePolicyId;
            if (conflictHandling == PolicyConflictHandling.ConfirmOverwrite)
            {
                if (conflict is null
                    || !Session.IsConflictCurrent(conflict)
                    || !string.Equals(
                        canonicalRaw,
                        conflict.SubmittedCanonicalRawJson,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        validation.CanonicalDraft.Metadata.Id,
                        conflict.DraftId,
                        StringComparison.Ordinal))
                {
                    Session.ClearConflict();
                    return;
                }
                PolicyEditorRetryDecision decision = conflict.RetryDecision;
                operation = decision.Operation;
                token = decision.Token;
                state = decision.State;
                activePolicyId = decision.ActivePolicyId;
            }
            else
            {
                operation = ToReplacementOperation(
                    Session.ResolveOperationForDraftId(validation.CanonicalDraft.Metadata.Id));
                token = Session.OriginManagement.StoreToken;
                state = Session.OriginManagement.State;
                activePolicyId = Session.OriginManagement.Policy?.Metadata.Id;
            }

            PolicyEditorDraftDocument canonicalDraft =
                PolicyEditorMapper.ToDraft(validation.CanonicalDraft);
            bool loadedAuditMode =
                Session.OriginManagement.Policy?.Enforcement.AuditMode is true;
            if (!loadedAuditMode && canonicalDraft.Enforcement.AuditMode is true)
            {
                bool acknowledged = await _confirmationPrompt.ConfirmAsync(
                    new PolicyEditorConfirmationRequest(
                        PolicyEditorConfirmationKind.EnableAuditMode,
                        operation,
                        validation.CanonicalDraft.Metadata.Id,
                        token,
                        state,
                        activePolicyId,
                        validation.Findings.All),
                    cancellationToken);
                if (!CanApply(cancellationToken)
                    || saveGeneration != Volatile.Read(ref _saveGeneration)
                    || Session.MutationGeneration != attemptGeneration)
                {
                    return;
                }
                if (!acknowledged)
                    return;
            }

            bool loadedDefaultAllow =
                Session.OriginManagement.Policy?.Enforcement.DefaultDecision
                == Devolutions.Now.Policy.Model.Decision.Allow;
            if (!loadedDefaultAllow
                && canonicalDraft.Enforcement.DefaultDecision
                    == Devolutions.Now.Policy.Model.Decision.Allow)
            {
                bool acknowledged = await _confirmationPrompt.ConfirmAsync(
                    new PolicyEditorConfirmationRequest(
                        PolicyEditorConfirmationKind.EnableDefaultAllow,
                        operation,
                        validation.CanonicalDraft.Metadata.Id,
                        token,
                        state,
                        activePolicyId,
                        validation.Findings.All),
                    cancellationToken);
                if (!CanApply(cancellationToken)
                    || saveGeneration != Volatile.Read(ref _saveGeneration)
                    || Session.MutationGeneration != attemptGeneration)
                {
                    return;
                }
                if (!acknowledged)
                    return;
            }

            PolicyEditorConfirmationKind? operationConfirmation =
                conflictHandling == PolicyConflictHandling.ConfirmOverwrite
                    ? null
                    : operation switch
                    {
                        PolicyReplacementOperation.ReplaceIdentity =>
                            PolicyEditorConfirmationKind.ReplaceIdentity,
                        PolicyReplacementOperation.Create =>
                            PolicyEditorConfirmationKind.Create,
                        _ => null,
                    };
            if (operationConfirmation is { } kind
                && !await _confirmationPrompt.ConfirmAsync(
                    new PolicyEditorConfirmationRequest(
                        kind,
                        operation,
                        validation.CanonicalDraft.Metadata.Id,
                        token,
                        state,
                        activePolicyId,
                        validation.Findings.All),
                    cancellationToken))
                return;
            if (!CanApply(cancellationToken)
                || saveGeneration != Volatile.Read(ref _saveGeneration))
                return;
            if (Session.MutationGeneration != attemptGeneration) return;

            using JsonDocument canonicalDocument = JsonDocument.Parse(canonicalRaw);
            var request = new PolicyEditorWriteRequest(
                operation,
                conflictHandling,
                token,
                canonicalDocument.RootElement.Clone(),
                validation.Receipt);
            PolicyWriteOutcome write =
                await _writeClient.WriteAsync(request, cancellationToken);
            if (!CanApplyDispatchedWrite(saveGeneration)) return;
            PublishWriteCompletion(
                saveGeneration,
                write,
                Session.MutationGeneration != attemptGeneration);

            if (write.FailureKind == PolicyWriteFailureKind.WriteResultUnknown)
            {
                LastWriteFailureKind = write.FailureKind;
                LastErrorCode = write.Error?.Code;
                LastWriteDiagnosticCode = write.DiagnosticCode;
                RequiresManagementRefresh = true;
                OnEditorStateChanged();
            }

            if (Session.MutationGeneration != attemptGeneration)
            {
                if (write.Response is not null)
                {
                    Session.MarkSavedPreservingCurrentDraft(write.Response, attemptGeneration);
                    SavedWithNewerChanges = true;
                    LastSaveSucceeded = true;
                    ScheduleCurrentModeDirtyAnalysis();
                    OnEditorStateChanged();
                }

                return;
            }

            if (conflictHandling == PolicyConflictHandling.ConfirmOverwrite
                && (conflict is null || !Session.IsConflictCurrent(conflict)))
            {
                Session.ClearConflict();
                return;
            }

            if (write.Response is not null)
            {
                Session.MarkSaved(write.Response);
                SavedWithNewerChanges = false;
                SavedThenSuperseded = write.SavedThenSuperseded;
                LastSaveSucceeded = true;
                StatusMessage = "";
                OnEditorStateChanged();
                return;
            }

            LastWriteFailureKind = write.FailureKind;
            LastErrorCode = write.Error?.Code;
            LastWriteDiagnosticCode = write.DiagnosticCode;
            RequiresManagementRefresh =
                write.FailureKind == PolicyWriteFailureKind.WriteResultUnknown;
            if (write.ConflictDecision is { } conflictDecision)
            {
                Session.CaptureConflict(
                    conflictDecision,
                    validation.CanonicalDraft,
                    validation.Receipt,
                    validation.CanonicalDraft.Metadata.Id);
            }
            else if (write.Error is
            {
                Code: ErrorCode.StalePolicyStoreToken,
                Management: not null,
            })
            {
                Session.CaptureConflict(
                    write.Error.Management,
                    validation.CanonicalDraft,
                    validation.Receipt,
                    validation.CanonicalDraft.Metadata.Id);
            }
            OnEditorStateChanged();
        }
        finally
        {
            if (Volatile.Read(ref _isDisposed) != 0
                || saveGeneration == Volatile.Read(ref _saveGeneration))
            {
                StatusMessage = "";
                IsBusy = false;
            }
        }
    }

    private bool CanStartRemoteOperation() =>
        Volatile.Read(ref _isDisposed) == 0
        && !IsBusy
        && !HasLocalInputErrors
        && !HasLocalSemanticErrors
        && !IsRawSyntaxPending
        && !RequiresManagementRefresh
        && SyntaxError is null;

    private bool CanStartStructuredOperation() =>
        Volatile.Read(ref _isDisposed) == 0
        && !IsBusy
        && !HasLocalInputErrors;

    private bool CanSwitchStructuredToRaw() =>
        CanStartStructuredOperation()
        && Session.Mode == PolicyEditorMode.Structured
        && !Session.Draft.Rules.Any(rule =>
            PolicyEditorRuleSemantics.IsCatchAll(rule.Match));

    private bool CanProjectRawToStructured() =>
        CanStartStructuredOperation()
        && Session.Mode == PolicyEditorMode.Raw
        && !IsRawSyntaxPending;

    private bool CanApply(CancellationToken cancellationToken) =>
        Volatile.Read(ref _isDisposed) == 0 && !cancellationToken.IsCancellationRequested;

    private bool CanApplyDispatchedWrite(long saveGeneration) =>
        Volatile.Read(ref _isDisposed) == 0
        && saveGeneration == Volatile.Read(ref _saveGeneration);

    private void PublishWriteCompletion(
        long generation,
        PolicyWriteOutcome write,
        bool hasNewerChanges)
    {
        PolicyEditorWriteCompletionKind kind;
        if (write.Response is not null)
        {
            kind = hasNewerChanges
                ? PolicyEditorWriteCompletionKind.SavedWithNewerChanges
                : write.SavedThenSuperseded
                    ? PolicyEditorWriteCompletionKind.SavedThenSuperseded
                    : PolicyEditorWriteCompletionKind.Saved;
        }
        else if (write.FailureKind != PolicyWriteFailureKind.WriteResultUnknown
                 && (write.ConflictDecision is not null
                     || write.Error is
                     {
                         Code: ErrorCode.StalePolicyStoreToken,
                         Management: not null,
                     }))
        {
            kind = PolicyEditorWriteCompletionKind.Conflict;
        }
        else
        {
            kind = PolicyEditorWriteCompletionKind.Failed;
        }

        LastWriteCompletion = new(
            generation,
            kind,
            write.FailureKind,
            write.Error?.Code,
            write.DiagnosticCode);
        OnPropertyChanged(nameof(LastWriteCompletion));
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    partial void OnSyntaxErrorChanged(PolicyEditorSyntaxError? value)
    {
        OnPropertyChanged(nameof(SyntaxErrorTitle));
        OnPropertyChanged(nameof(SyntaxErrorMessage));
        NotifyCommandStates();
    }

    partial void OnRequiresManagementRefreshChanged(bool value) => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanValidateOrSave));
        OnPropertyChanged(nameof(CanSwitchToRaw));
        OnPropertyChanged(nameof(CanSwitchToStructured));
        SwitchToRawCommand.NotifyCanExecuteChanged();
        SwitchToStructuredCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ConfirmOverwriteCommand.NotifyCanExecuteChanged();
    }

    private void RequestFindingNavigation()
    {
        _findingNavigationGeneration++;
        OnPropertyChanged(nameof(FindingNavigationGeneration));
    }

    private bool TryGetDraftElement(
        string raw,
        out JsonElement element,
        out PolicyEditorSyntaxError? error)
    {
        element = default;
        if (Session.Mode == PolicyEditorMode.Raw
            && string.Equals(raw, Session.RawBuffer, StringComparison.Ordinal)
            && Session.TryGetAnalyzedRawElement(out element))
        {
            error = null;
            return true;
        }

        return PolicyEditorRawSyntax.TryParseStrictWithElement(
            raw,
            out _,
            out element,
            out error);
    }

    private PolicyReplacementOperation GetInitialOperation() =>
        ToReplacementOperation(Session.Operation);

    private static PolicyReplacementOperation ToReplacementOperation(
        PolicyEditorOperationKind operation) => operation switch
        {
            PolicyEditorOperationKind.Update => PolicyReplacementOperation.Update,
            PolicyEditorOperationKind.ReplaceIdentity => PolicyReplacementOperation.ReplaceIdentity,
            PolicyEditorOperationKind.Create => PolicyReplacementOperation.Create,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

    private void OnEditorStateChanged()
    {
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(nameof(Rules));
        OnPropertyChanged(nameof(Operation));
        OnPropertyChanged(nameof(RawBuffer));
        OnPropertyChanged(nameof(IsStructuredMode));
        OnPropertyChanged(nameof(IsRawMode));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsIdentityLocked));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasLocalSemanticErrors));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(IsRawSyntaxPending));
        NotifyCommandStates();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        Interlocked.Increment(ref _saveGeneration);
        CancelRawSyntaxAnalysis();
        CancelStructuredDirtyAnalysis();
        CancelAuthoritativeValidation();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        NotifyCommandStates();
    }

    internal Task WaitForRawSyntaxAnalysisAsync() => _rawSyntaxAnalysis;

    internal Task WaitForStructuredDirtyAnalysisAsync() => _structuredDirtyAnalysis;

    internal Task WaitForAuthoritativeValidationAsync() => _authoritativeValidation;

    private void OnStructuredDraftChanged()
    {
        RefreshLocalSemanticValidation();
        ScheduleAuthoritativeValidation();
        ScheduleStructuredDirtyAnalysis();
        OnEditorStateChanged();
    }

    private void ApplySafeAllowMatchDefaults(PolicyEditorDraftRule rule)
    {
        if (rule.Match.SkipHashCheck == TriState.Omitted
            && !IsExplicitlyConfigured(rule, PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck))
            rule.Match.SkipHashCheck = TriState.False;
        if (rule.Match.HasCustomParameters == TriState.Omitted
            && !IsExplicitlyConfigured(rule, PolicyEditorAdvisories.PolicyEditorRisk.CustomParameters))
            rule.Match.HasCustomParameters = TriState.False;
        if (rule.Match.HasCustomInstallLocation == TriState.Omitted
            && !IsExplicitlyConfigured(rule, PolicyEditorAdvisories.PolicyEditorRisk.CustomInstallLocation))
            rule.Match.HasCustomInstallLocation = TriState.False;
        if (rule.Match.HasPrePostCommands == TriState.Omitted
            && !IsExplicitlyConfigured(rule, PolicyEditorAdvisories.PolicyEditorRisk.PrePostCommands))
            rule.Match.HasPrePostCommands = TriState.False;
    }

    private bool IsExplicitlyConfigured(
        PolicyEditorDraftRule rule,
        PolicyEditorAdvisories.PolicyEditorRisk risk) =>
        _explicitMatchCharacteristics.TryGetValue(rule, out HashSet<PolicyEditorAdvisories.PolicyEditorRisk>? configured)
        && configured.Contains(risk);

    private void RefreshLocalSemanticValidation()
    {
        IReadOnlyList<PolicyValidationFinding> findings =
            PolicyEditorLocalValidation.ValidateDraft(Session.Draft)
                .Where(finding => !IsDeferredBlankFinding(finding))
                .ToArray();
        _hasLocalSemanticErrors = findings.Any(
            finding => finding.Severity == PolicyValidationSeverity.Error);
        Session.SetLocalFindings(findings);
    }

    internal bool IsDeferredBlankRule(PolicyEditorDraftRule rule) =>
        _deferredBlankRules.Contains(rule)
        && PolicyEditorRuleSemantics.IsCatchAll(rule.Match);

    private bool IsDeferredBlankFinding(PolicyValidationFinding finding)
    {
        if (!finding.Pointer.EndsWith("/Match", StringComparison.Ordinal)
            || !TryGetRuleIndex(finding.Pointer, out int index)
            || index < 0
            || index >= Session.Draft.Rules.Count)
        {
            return false;
        }

        return IsDeferredBlankRule(Session.Draft.Rules[index]);
    }

    private static bool TryGetRuleIndex(string pointer, out int index)
    {
        index = -1;
        string[] segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && segments[0].Equals("Rules", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], out index);
    }

    private void PromoteDeferredBlankRules()
    {
        if (_deferredBlankRules.Count == 0) return;
        _deferredBlankRules.Clear();
        RefreshLocalSemanticValidation();
        OnEditorStateChanged();
    }

    private void ReconcileDirtyAtBoundary(string effectiveRawJson)
    {
        PolicyEditorDirtyComparisonSnapshot snapshot = Session.CaptureDirtyComparison();
        ApplyDirtyComparison(
            snapshot,
            !string.Equals(
                effectiveRawJson,
                snapshot.BaselineRawJson,
                StringComparison.Ordinal));
    }

    private void ScheduleStructuredDirtyAnalysis()
    {
        if (Session.Mode != PolicyEditorMode.Structured)
            return;

        PolicyEditorDirtyComparisonSnapshot snapshot = Session.CaptureDirtyComparison();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _structuredDirtyCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _structuredDirtyAnalysis = AnalyzeStructuredDirtyAsync(snapshot, cancellation);
    }

    private void ScheduleCurrentModeDirtyAnalysis()
    {
        if (Session.Mode == PolicyEditorMode.Raw)
            ScheduleRawSyntaxAnalysis(Session.RawBuffer);
        else
            ScheduleStructuredDirtyAnalysis();
    }

    private bool ApplyDirtyComparison(
        PolicyEditorDirtyComparisonSnapshot snapshot,
        bool isDirty)
    {
        if (!Session.TryApplyDirtyComparison(snapshot, isDirty))
            return false;

        if (!isDirty && !HasLocalInputErrors)
            SavedWithNewerChanges = false;
        return true;
    }

    private async Task AnalyzeStructuredDirtyAsync(
        PolicyEditorDirtyComparisonSnapshot snapshot,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_structuredDirtyDebounce, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            PolicyEditorDraftDocument draftSnapshot = Session.Draft.Clone();
            bool isDirty = await Task.Run(
                () =>
                {
                    try
                    {
                        return !string.Equals(
                            _structuredDraftSerializer(draftSnapshot),
                            snapshot.BaselineRawJson,
                            StringComparison.Ordinal);
                    }
                    catch (JsonException)
                    {
                        return true;
                    }
                },
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            if (!ApplyDirtyComparison(snapshot, isDirty))
            {
                return;
            }

            // This continuation intentionally resumes on the UI context captured by the edit.
            OnPropertyChanged(nameof(IsDirty));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException) when (
            cancellation.IsCancellationRequested
            || snapshot.MutationGeneration != Session.MutationGeneration)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _structuredDirtyCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }

    }

    private void CancelStructuredDirtyAnalysis()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _structuredDirtyCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        _structuredDirtyAnalysis = Task.CompletedTask;
    }

    private void ScheduleRawSyntaxAnalysis(string raw)
    {
        long mutationGeneration = Session.MutationGeneration;
        PolicyEditorDirtyComparisonSnapshot dirtySnapshot = Session.CaptureDirtyComparison();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _rawSyntaxCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _rawSyntaxAnalysis = AnalyzeRawSyntaxAsync(
            raw,
            mutationGeneration,
            dirtySnapshot,
            cancellation);
    }

    private async Task AnalyzeRawSyntaxAsync(
        string raw,
        long mutationGeneration,
        PolicyEditorDirtyComparisonSnapshot dirtySnapshot,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_rawSyntaxDebounce, cancellation.Token);
            (
                PolicyEditorSyntaxError? Error,
                string? CanonicalRaw,
                string? DraftId,
                JsonElement? RawElement,
                IReadOnlyList<PolicyValidationFinding> LocalFindings,
                bool IsDirty) result =
                await Task.Run<(
                   PolicyEditorSyntaxError? Error,
                   string? CanonicalRaw,
                   string? DraftId,
                   JsonElement? RawElement,
                   IReadOnlyList<PolicyValidationFinding> LocalFindings,
                   bool IsDirty)>(
                   () =>
                   {
                       bool parsed = PolicyEditorRawSyntax.TryParseStrictWithElement(
                           raw,
                           out PolicyEditorDraftDocument? draft,
                           out JsonElement element,
                           out PolicyEditorSyntaxError? error);
                       return (
                           error,
                           parsed && draft is not null
                               ? PolicyEditorRawSyntax.ToCanonicalRawPreservingPriorities(draft)
                               : null,
                           parsed ? draft?.Metadata.Id : null,
                           parsed ? (JsonElement?)element : null,
                           parsed && draft is not null
                               ? PolicyEditorLocalValidation.ValidateDraft(draft)
                               : [],
                           !string.Equals(
                               raw,
                               dirtySnapshot.BaselineRawJson,
                               StringComparison.Ordinal));
                   },
                    cancellation.Token);
            if (cancellation.IsCancellationRequested
                || Volatile.Read(ref _isDisposed) != 0
                || !Session.CompleteRawAnalysis(
                    raw,
                    mutationGeneration,
                    result.CanonicalRaw,
                    result.DraftId,
                    result.RawElement))
            {
                return;
            }

            ApplyDirtyComparison(dirtySnapshot, result.IsDirty);
            if (!Session.LastRawAnalysisWasFormattingOnly)
            {
                _hasLocalSemanticErrors = result.LocalFindings.Any(
                    finding => finding.Severity == PolicyValidationSeverity.Error);
                Session.SetLocalFindings(result.LocalFindings);
            }
            else
            {
                _hasLocalSemanticErrors = Session.Findings.All.Any(
                    finding => finding.Severity == PolicyValidationSeverity.Error);
            }
            SyntaxError = result.Error;
            if (result.Error is null && !_hasLocalSemanticErrors && result.RawElement is { } element)
            {
                ScheduleAuthoritativeValidation(
                    raw,
                    element,
                    mutationGeneration);
            }
            OnEditorStateChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _rawSyntaxCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelRawSyntaxAnalysis()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _rawSyntaxCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void ScheduleAuthoritativeValidation()
    {
        if (Session.Mode != PolicyEditorMode.Structured
            || HasLocalInputErrors
            || _hasLocalSemanticErrors)
        {
            CancelAuthoritativeValidation();
            return;
        }

        string raw;
        try
        {
            raw = Session.GetEffectiveRawJson();
        }
        catch (JsonException)
        {
            return;
        }
        if (!TryGetDraftElement(
                raw,
                out JsonElement element,
                out _))
        {
            return;
        }

        ScheduleAuthoritativeValidation(raw, element, Session.MutationGeneration);
    }

    private void ScheduleAuthoritativeValidation(
        string raw,
        JsonElement draft,
        long mutationGeneration)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _authoritativeValidationCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _authoritativeValidation = ValidateAuthoritativeAsync(
            raw,
            draft.Clone(),
            mutationGeneration,
            cancellation);
    }

    private async Task ValidateAuthoritativeAsync(
        string raw,
        JsonElement draft,
        long mutationGeneration,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_structuredDirtyDebounce, cancellation.Token);
            PolicyEditorValidationOutcome outcome =
                await _validationClient.ValidateAsync(draft, cancellation.Token);
            if (cancellation.IsCancellationRequested
                || Volatile.Read(ref _isDisposed) != 0
                || mutationGeneration != Session.MutationGeneration
                || !string.Equals(raw, Session.GetEffectiveRawJson(), StringComparison.Ordinal)
                || outcome.Validation is null)
            {
                return;
            }

            Session.ApplyValidationResult(
                raw,
                outcome.Validation,
                outcome.BoundedFindings,
                outcome.OmittedFindingCount);
            _hasLocalSemanticErrors = Session.Findings.All.Any(
                finding => finding.Severity == PolicyValidationSeverity.Error);
            SyntaxError = null;
            OnEditorStateChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _authoritativeValidationCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelAuthoritativeValidation()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _authoritativeValidationCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        _authoritativeValidation = Task.CompletedTask;
    }
}

internal enum PolicyEditorWriteCompletionKind
{
    Saved,
    SavedWithNewerChanges,
    SavedThenSuperseded,
    Conflict,
    Failed,
}

internal sealed record PolicyEditorWriteCompletion(
    long Generation,
    PolicyEditorWriteCompletionKind Kind,
    PolicyWriteFailureKind FailureKind,
    ErrorCode? ErrorCode,
    string? DiagnosticCode);
