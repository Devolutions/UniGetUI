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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<object, string> _localInputErrors = [];
    private CancellationTokenSource? _rawSyntaxCancellation;
    private Task _rawSyntaxAnalysis = Task.CompletedTask;
    private long _validationGeneration;
    private long _saveGeneration;
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
        PolicyEditorSyntaxErrorKind.UnsupportedSchema =>
            CoreTools.Translate("The policy draft uses an unsupported schema."),
        PolicyEditorSyntaxErrorKind.UnsupportedPolicyType =>
            CoreTools.Translate("The policy draft uses an unsupported policy type."),
        PolicyEditorSyntaxErrorKind.MissingEnforcement =>
            CoreTools.Translate("The policy draft is missing the Enforcement object."),
        PolicyEditorSyntaxErrorKind.UnsupportedRulePrecedence =>
            CoreTools.Translate("The policy draft uses an unsupported rule precedence."),
        PolicyEditorSyntaxErrorKind.MissingMetadata =>
            CoreTools.Translate("The policy draft is missing the Metadata object."),
        _ => CoreTools.Translate("The document does not match the policy draft format."),
    };
    public bool HasLocalInputErrors => _localInputErrors.Count > 0;
    public string LocalInputErrorSummary => string.Join(Environment.NewLine, _localInputErrors.Values);
    public bool CanValidateOrSave => CanStartRemoteOperation();
    public bool CanSwitchToRaw => CanStartStructuredOperation();

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

    public PolicyEditorSessionViewModel(
        PolicyEditorSession session,
        IPolicyValidationClient validationClient,
        IPolicyEditorConfirmationPrompt confirmationPrompt,
        IPolicyWriteClient writeClient,
        TimeSpan? rawSyntaxDebounce = null)
    {
        Session = session;
        _validationClient = validationClient;
        _confirmationPrompt = confirmationPrompt;
        _writeClient = writeClient;
        _rawSyntaxDebounce = rawSyntaxDebounce ?? TimeSpan.FromMilliseconds(300);
    }

    [RelayCommand(CanExecute = nameof(CanStartStructuredOperation))]
    private void SwitchToRaw()
    {
        Session.SwitchToRaw();
        CancelRawSyntaxAnalysis();
        SyntaxError = null;
        OnEditorStateChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRemoteOperation))]
    private async Task SwitchToStructuredAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linked.Token;
        if (!CanStartRemoteOperation()) return;

        string submitted = Session.RawBuffer;
        if (!TryGetDraftElement(
                submitted,
                out JsonElement draft,
                out PolicyEditorSyntaxError? syntaxError))
        {
            SyntaxError = syntaxError;
            return;
        }

        long generation = Interlocked.Increment(ref _validationGeneration);
        PolicyEditorValidationOutcome outcome =
            await ValidateCoreAsync(draft, cancellationToken);
        if (!CanApply(cancellationToken)
            || generation != Volatile.Read(ref _validationGeneration)
            || !string.Equals(Session.RawBuffer, submitted, StringComparison.Ordinal))
            return;

        if (outcome.Validation is not { IsValid: true, CanonicalDraft: not null })
        {
            if (outcome.Validation is not null)
                Session.ApplyValidationResult(
                    submitted,
                    outcome.Validation,
                    outcome.BoundedFindings,
                    outcome.OmittedFindingCount);
            LastErrorCode = outcome.ErrorCode;
            OnEditorStateChanged();
            return;
        }

        Session.AcceptValidatedRaw(submitted, outcome.Validation);
        SyntaxError = null;
        LastErrorCode = null;
        OnEditorStateChanged();
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
        OnEditorStateChanged();
    }

    public void NotifyLocalInputChanged()
    {
        Session.NotifyDraftChanged();
        OnEditorStateChanged();
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
        NotifyCommandStates();
    }

    [RelayCommand]
    private void AddRule()
    {
        Session.AddRule();
        OnEditorStateChanged();
    }

    [RelayCommand]
    private void DuplicateRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        Session.DuplicateRule(rule.Id);
        OnEditorStateChanged();
    }

    [RelayCommand]
    private void ToggleRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        Session.SetRuleEnabled(rule.Id, !rule.Enabled);
        OnEditorStateChanged();
    }

    [RelayCommand]
    private void DeleteRule(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        Session.DeleteRule(rule.Id);
        OnEditorStateChanged();
    }

    [RelayCommand]
    private void MoveRuleUp(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        int index = Session.Draft.Rules.IndexOf(rule);
        Session.MoveRule(rule.Id, index - 1);
        OnEditorStateChanged();
    }

    [RelayCommand]
    private void MoveRuleDown(PolicyEditorDraftRule? rule)
    {
        if (rule is null) return;
        int index = Session.Draft.Rules.IndexOf(rule);
        Session.MoveRule(rule.Id, index + 1);
        OnEditorStateChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRemoteOperation))]
    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linked.Token;
        if (!CanStartRemoteOperation()) return;

        string raw = Session.GetEffectiveRawJson();
        if (!TryGetDraftElement(raw, out JsonElement draft, out PolicyEditorSyntaxError? error))
        {
            SyntaxError = error;
            return;
        }

        long generation = Interlocked.Increment(ref _validationGeneration);
        PolicyEditorValidationOutcome outcome =
            await ValidateCoreAsync(draft, cancellationToken);
        if (!CanApply(cancellationToken)
            || generation != Volatile.Read(ref _validationGeneration)
            || !string.Equals(Session.GetEffectiveRawJson(), raw, StringComparison.Ordinal))
            return;

        if (outcome.Validation is not null)
            Session.ApplyValidationResult(
                raw,
                outcome.Validation,
                outcome.BoundedFindings,
                outcome.OmittedFindingCount);
        LastErrorCode = outcome.ErrorCode;
        SyntaxError = null;
        OnEditorStateChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartRemoteOperation))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        cancellationToken = linked.Token;
        if (!CanStartRemoteOperation()) return;

        await SaveCoreAsync(
            conflict: null,
            PolicyConflictHandling.Reject,
            cancellationToken);
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

        await SaveCoreAsync(
            conflict,
            PolicyConflictHandling.ConfirmOverwrite,
            cancellationToken);
    }

    public async Task<bool> ConfirmDiscardAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            StatusMessage = CoreTools.Translate(
                "Please wait for the current policy operation to finish before closing.");
            return false;
        }

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
        try
        {
            string submitted = Session.GetEffectiveRawJson();
            long attemptGeneration = Session.MutationGeneration;

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
                    return;
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

            if (validation.HasWarnings && !Session.HasCurrentWarningAcknowledgement)
            {
                bool acknowledged = await _confirmationPrompt.ConfirmAsync(
                    new PolicyEditorConfirmationRequest(
                        PolicyEditorConfirmationKind.Warnings,
                        operation,
                        validation.CanonicalDraft.Metadata.Id,
                        token,
                        state,
                        activePolicyId,
                        validation.Findings.All,
                        validation.WarningCount),
                    cancellationToken);
                if (!CanApply(cancellationToken)
                    || saveGeneration != Volatile.Read(ref _saveGeneration))
                    return;
                if (Session.MutationGeneration != attemptGeneration) return;
                if (!acknowledged)
                    return;
                Session.AcknowledgeWarnings();
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
                        PolicyReplacementOperation.Repair =>
                            PolicyEditorConfirmationKind.Repair,
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
                validation.Receipt,
                validation.HasWarnings && Session.HasCurrentWarningAcknowledgement);
            PolicyWriteOutcome write =
                await _writeClient.WriteAsync(request, cancellationToken);
            if (!CanApplyDispatchedWrite(saveGeneration)) return;

            if (write.FailureKind == PolicyWriteFailureKind.WriteResultUnknown)
            {
                LastWriteFailureKind = write.FailureKind;
                LastErrorCode = write.Error?.Code;
                RequiresManagementRefresh = true;
                OnEditorStateChanged();
            }

            if (Session.MutationGeneration != attemptGeneration)
            {
                if (write.Response is not null)
                {
                    Session.MarkSavedPreservingCurrentDraft(write.Response);
                    SavedWithNewerChanges = true;
                    LastSaveSucceeded = true;
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

    private async Task<PolicyEditorValidationOutcome> ValidateCoreAsync(
        JsonElement draft,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            return await _validationClient.ValidateAsync(draft, cancellationToken);
        }
        finally
        {
            StatusMessage = "";
            IsBusy = false;
        }
    }

    private bool CanStartRemoteOperation() =>
        Volatile.Read(ref _isDisposed) == 0
        && !IsBusy
        && !HasLocalInputErrors
        && !IsRawSyntaxPending
        && !RequiresManagementRefresh
        && SyntaxError is null;

    private bool CanStartStructuredOperation() =>
        Volatile.Read(ref _isDisposed) == 0
        && !IsBusy
        && !HasLocalInputErrors;

    private bool CanApply(CancellationToken cancellationToken) =>
        Volatile.Read(ref _isDisposed) == 0 && !cancellationToken.IsCancellationRequested;

    private bool CanApplyDispatchedWrite(long saveGeneration) =>
        Volatile.Read(ref _isDisposed) == 0
        && saveGeneration == Volatile.Read(ref _saveGeneration);

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
        SwitchToRawCommand.NotifyCanExecuteChanged();
        SwitchToStructuredCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ConfirmOverwriteCommand.NotifyCanExecuteChanged();
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
        PolicyEditorOperationKind.Repair => PolicyReplacementOperation.Repair,
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
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(Findings));
        OnPropertyChanged(nameof(IsRawSyntaxPending));
        NotifyCommandStates();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        Interlocked.Increment(ref _validationGeneration);
        Interlocked.Increment(ref _saveGeneration);
        CancelRawSyntaxAnalysis();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        NotifyCommandStates();
    }

    internal Task WaitForRawSyntaxAnalysisAsync() => _rawSyntaxAnalysis;

    private void ScheduleRawSyntaxAnalysis(string raw)
    {
        long mutationGeneration = Session.MutationGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        CancellationTokenSource? previous =
            Interlocked.Exchange(ref _rawSyntaxCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _rawSyntaxAnalysis = AnalyzeRawSyntaxAsync(
            raw,
            mutationGeneration,
            cancellation);
    }

    private async Task AnalyzeRawSyntaxAsync(
        string raw,
        long mutationGeneration,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_rawSyntaxDebounce, cancellation.Token);
            (
                PolicyEditorSyntaxError? Error,
                string? CanonicalRaw,
                string? DraftId,
                JsonElement? RawElement) result =
                await Task.Run<(
                    PolicyEditorSyntaxError? Error,
                    string? CanonicalRaw,
                    string? DraftId,
                    JsonElement? RawElement)>(
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
                                ? PolicyEditorRawSyntax.ToCanonicalRaw(draft)
                                : null,
                            parsed ? draft?.Metadata.Id : null,
                            parsed ? (JsonElement?)element : null);
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

            SyntaxError = result.Error;
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
}
