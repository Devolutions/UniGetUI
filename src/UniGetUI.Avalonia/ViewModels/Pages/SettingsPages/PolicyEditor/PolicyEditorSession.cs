using System.Text.Json;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

public sealed record PolicyEditorValidationState(
    string SubmittedRawJson,
    PolicyDraftDocument CanonicalDraft,
    string Receipt,
    PolicyEditorFindingIndex Findings,
    int WarningCount)
{
    public bool HasWarnings => WarningCount > 0;
}

public sealed record PolicyEditorWarningAcknowledgement(
    string CanonicalRawJson,
    string Receipt,
    int WarningCount,
    IReadOnlyList<string> WarningKeys);

public sealed record PolicyEditorConflictSnapshot(
    string SubmittedCanonicalRawJson,
    string ValidationReceipt,
    string DraftId,
    long MutationGeneration,
    PolicyEditorRetryDecision RetryDecision,
    DateTimeOffset DetectedAt);

public sealed class PolicyEditorSession
{
    private string _baselineRawJson;
    private string _lastAnalyzedRawJson;
    private string _lastAnalyzedCanonicalRawJson;
    private string _lastAnalyzedDraftId;
    private JsonElement _lastAnalyzedRawElement;
    private bool _hasLastAnalyzedRawElement;
    private long _lastAnalyzedMutationGeneration;
    private long _mutationGeneration;

    public PolicyEditorOperationKind Operation { get; private set; }

    public PolicyManagementSnapshot OriginManagement { get; private set; }

    public PolicyEditorDraftDocument Draft { get; private set; }

    public string RawBuffer { get; private set; }

    public PolicyEditorMode Mode { get; private set; }

    public PolicyEditorValidationState? Validation { get; private set; }

    public PolicyEditorFindingIndex Findings { get; private set; } =
        PolicyEditorFindingIndex.Build([]);

    public PolicyEditorWarningAcknowledgement? WarningAcknowledgement { get; private set; }

    public PolicyEditorConflictSnapshot? Conflict { get; private set; }

    public long MutationGeneration => _mutationGeneration;

    public bool IsRawAnalysisPending { get; private set; }

    public bool IsIdentityLocked => Operation == PolicyEditorOperationKind.Update;

    public bool IsDirty =>
        !string.Equals(GetEffectiveRawJson(), _baselineRawJson, StringComparison.Ordinal);

    public bool IsValidationCurrent =>
        Validation is not null
        && !IsRawAnalysisPending
        && string.Equals(
            Validation.SubmittedRawJson,
            GetEffectiveRawJson(),
            StringComparison.Ordinal);

    public bool HasCurrentWarningAcknowledgement
    {
        get
        {
            if (Validation is null || WarningAcknowledgement is null)
                return false;

            string canonical = PolicySerializer.Serialize(Validation.CanonicalDraft);
            return string.Equals(
                    WarningAcknowledgement.CanonicalRawJson,
                    canonical,
                    StringComparison.Ordinal)
                && string.Equals(
                    WarningAcknowledgement.Receipt,
                    Validation.Receipt,
                    StringComparison.Ordinal)
                && WarningAcknowledgement.WarningCount == Validation.WarningCount
                && WarningAcknowledgement.WarningKeys.SequenceEqual(
                    GetWarningKeys(Validation.Findings),
                    StringComparer.Ordinal);
        }
    }

    private PolicyEditorSession(
        PolicyEditorOperationKind operation,
        PolicyManagementSnapshot originManagement,
        PolicyEditorDraftDocument draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originManagement.StoreToken);
        Operation = operation;
        OriginManagement = PolicyEditorMapper.CloneManagementSnapshot(originManagement);
        Draft = draft.Clone();
        Mode = PolicyEditorMode.Structured;
        RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(Draft);
        _baselineRawJson = RawBuffer;
        _lastAnalyzedRawJson = RawBuffer;
        _lastAnalyzedCanonicalRawJson = RawBuffer;
        _lastAnalyzedDraftId = Draft.Metadata.Id;
        _lastAnalyzedMutationGeneration = _mutationGeneration;
    }

    public static PolicyEditorSession StartUpdate(PolicyManagementSnapshot management)
    {
        RequireState(management, PolicyManagementState.Active);
        return new(
            PolicyEditorOperationKind.Update,
            management,
            PolicyEditorMapper.ToDraft(management.Policy!));
    }

    public static PolicyEditorSession StartReplaceIdentity(
        PolicyManagementSnapshot management,
        PolicyEditorDraftDocument draft)
    {
        RequireState(management, PolicyManagementState.Active);
        if (string.Equals(
                management.Policy!.Metadata.Id,
                draft.Metadata.Id,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A replacement policy must use a different identity.",
                nameof(draft));
        }

        return new(PolicyEditorOperationKind.ReplaceIdentity, management, draft);
    }

    public static PolicyEditorSession StartCreate(
        PolicyManagementSnapshot management,
        PolicyEditorDraftDocument draft)
    {
        RequireState(management, PolicyManagementState.Missing);
        return new(PolicyEditorOperationKind.Create, management, draft);
    }

    public static PolicyEditorSession StartRepair(
        PolicyManagementSnapshot management,
        PolicyEditorDraftDocument draft)
    {
        RequireState(management, PolicyManagementState.Invalid);
        return new(PolicyEditorOperationKind.Repair, management, draft);
    }

    public void SwitchToRaw()
    {
        RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(Draft);
        Mode = PolicyEditorMode.Raw;
        _lastAnalyzedRawJson = RawBuffer;
        _lastAnalyzedCanonicalRawJson = RawBuffer;
        _lastAnalyzedDraftId = Draft.Metadata.Id;
        IsRawAnalysisPending = false;
        InvalidateContentState();
        _lastAnalyzedMutationGeneration = _mutationGeneration;
    }

    public void SetRawBuffer(string rawText)
    {
        if (Mode != PolicyEditorMode.Raw)
            throw new InvalidOperationException("The session is not in raw mode.");

        RawBuffer = rawText ?? "";
        _mutationGeneration++;
        IsRawAnalysisPending = true;
    }

    public bool CompleteRawAnalysis(
        string analyzedRawJson,
        long analyzedMutationGeneration,
        string? canonicalRawJson,
        string? draftId,
        JsonElement? rawElement)
    {
        if (Mode != PolicyEditorMode.Raw
            || analyzedMutationGeneration != _mutationGeneration
            || !string.Equals(RawBuffer, analyzedRawJson, StringComparison.Ordinal))
        {
            return false;
        }

        IsRawAnalysisPending = false;
        _hasLastAnalyzedRawElement = rawElement.HasValue;
        _lastAnalyzedRawElement = rawElement?.Clone() ?? default;
        _lastAnalyzedMutationGeneration = analyzedMutationGeneration;
        bool formattingOnly =
            canonicalRawJson is not null
            && string.Equals(
                _lastAnalyzedCanonicalRawJson,
                canonicalRawJson,
                StringComparison.Ordinal);
        if (formattingOnly)
        {
            if (Validation is not null
                && string.Equals(
                    Validation.SubmittedRawJson,
                    _lastAnalyzedRawJson,
                    StringComparison.Ordinal))
            {
                Validation = Validation with { SubmittedRawJson = RawBuffer };
            }
            if (Conflict is not null)
            {
                Conflict = Conflict with { MutationGeneration = _mutationGeneration };
            }
            _lastAnalyzedRawJson = RawBuffer;
            return true;
        }

        _lastAnalyzedRawJson = RawBuffer;
        _lastAnalyzedCanonicalRawJson = canonicalRawJson ?? "";
        _lastAnalyzedDraftId = draftId ?? "";
        ClearContentState();
        return true;
    }

    public bool TryGetAnalyzedRawElement(out JsonElement element)
    {
        if (Mode == PolicyEditorMode.Raw
            && !IsRawAnalysisPending
            && _hasLastAnalyzedRawElement
            && _lastAnalyzedMutationGeneration == _mutationGeneration
            && string.Equals(
                _lastAnalyzedRawJson,
                RawBuffer,
                StringComparison.Ordinal))
        {
            element = _lastAnalyzedRawElement.Clone();
            return true;
        }

        element = default;
        return false;
    }

    public bool TryParseRaw(
        out PolicyEditorDraftDocument? parsed,
        out PolicyEditorSyntaxError? error) =>
        PolicyEditorRawSyntax.TryParseStrict(RawBuffer, out parsed, out error);

    public void AcceptValidatedRaw(
        string submittedRawJson,
        PolicyValidationResult validation)
    {
        ApplyValidationResult(submittedRawJson, validation);
        if (Validation is null)
            throw new InvalidOperationException(
                "Only an authoritative valid result can enter structured mode.");

        Draft = PolicyEditorMapper.ToDraft(Validation.CanonicalDraft);
        RawBuffer = PolicySerializer.Serialize(Validation.CanonicalDraft);
        Mode = PolicyEditorMode.Structured;
        _lastAnalyzedRawJson = RawBuffer;
        _lastAnalyzedCanonicalRawJson = RawBuffer;
        _lastAnalyzedDraftId = Draft.Metadata.Id;
        _lastAnalyzedMutationGeneration = _mutationGeneration;
        _hasLastAnalyzedRawElement = false;
        IsRawAnalysisPending = false;
    }

    public string GetEffectiveRawJson() =>
        Mode == PolicyEditorMode.Raw
            ? RawBuffer
            : PolicyEditorRawSyntax.ToCanonicalRaw(Draft);

    public void NotifyDraftChanged() => InvalidateContentState();

    public PolicyEditorDraftRule AddRule(PolicyEditorDraftRule? rule = null)
    {
        EnsureStructuredMode();
        PolicyEditorDraftRule newRule = rule ?? PolicyRuleFactory.CreateBlank();
        PolicyRuleListOperations.Add(Draft.Rules, newRule);
        InvalidateContentState();
        return newRule;
    }

    public void EditRule(string id, Action<PolicyEditorDraftRule> mutate)
    {
        EnsureStructuredMode();
        PolicyRuleListOperations.Edit(Draft.Rules, id, mutate);
        InvalidateContentState();
    }

    public string DuplicateRule(string id, string? newId = null)
    {
        EnsureStructuredMode();
        string result = PolicyRuleListOperations.Duplicate(Draft.Rules, id, newId);
        InvalidateContentState();
        return result;
    }

    public void SetRuleEnabled(string id, bool enabled)
    {
        EnsureStructuredMode();
        PolicyRuleListOperations.SetEnabled(Draft.Rules, id, enabled);
        InvalidateContentState();
    }

    public void DeleteRule(string id)
    {
        EnsureStructuredMode();
        PolicyRuleListOperations.Delete(Draft.Rules, id);
        InvalidateContentState();
    }

    public void MoveRule(string id, int newIndex)
    {
        EnsureStructuredMode();
        PolicyRuleListOperations.Move(Draft.Rules, id, newIndex);
        InvalidateContentState();
    }

    public void SetRulePriority(string id, uint priority)
    {
        EnsureStructuredMode();
        PolicyRuleListOperations.SetPriority(Draft.Rules, id, priority);
        InvalidateContentState();
    }

    public void ApplyValidationResult(
        string submittedRawJson,
        PolicyValidationResult validation,
        IReadOnlyList<PolicyValidationFinding>? boundedFindings = null,
        int omittedFindingCount = 0)
    {
        ArgumentNullException.ThrowIfNull(submittedRawJson);
        ArgumentNullException.ThrowIfNull(validation);

        if (boundedFindings is not null)
        {
            Findings = PolicyEditorFindingIndex.Build(boundedFindings, omittedFindingCount);
        }
        else
        {
            int take = Math.Min(
                validation.Findings.Count,
                PolicyEditorFindingIndex.MaxDisplayedFindings);
            var sanitized = new List<PolicyValidationFinding>(take);
            for (int index = 0; index < take; index++)
            {
                sanitized.Add(PolicyValidationFinding.FromShared(validation.Findings[index]));
            }

            Findings = PolicyEditorFindingIndex.Build(
                sanitized,
                validation.Findings.Count - take);
        }
        WarningAcknowledgement = null;

        if (!validation.IsValid
            || validation.CanonicalDraft is null
            || string.IsNullOrWhiteSpace(validation.ValidationReceipt))
        {
            Validation = null;
            return;
        }

        Validation = new PolicyEditorValidationState(
            submittedRawJson,
            PolicyEditorMapper.CloneDraftDocument(validation.CanonicalDraft),
            validation.ValidationReceipt,
            Findings,
            validation.Findings.Count(
                finding => finding.Severity == PolicyFindingSeverity.Warning));
        Operation = ResolveOperationForDraftId(validation.CanonicalDraft.Metadata.Id);
    }

    public void AcknowledgeWarnings()
    {
        if (Validation is null || !Validation.HasWarnings)
            throw new InvalidOperationException("There are no current validated warnings.");

        WarningAcknowledgement = new PolicyEditorWarningAcknowledgement(
            PolicySerializer.Serialize(Validation.CanonicalDraft),
            Validation.Receipt,
            Validation.WarningCount,
            GetWarningKeys(Validation.Findings));
    }

    public void CaptureConflict(
        PolicyManagementSnapshot management,
        PolicyDraftDocument submittedCanonicalDraft,
        string validationReceipt,
        string draftId)
    {
        ArgumentNullException.ThrowIfNull(submittedCanonicalDraft);
        ArgumentException.ThrowIfNullOrWhiteSpace(validationReceipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        string submittedCanonicalRawJson = PolicySerializer.Serialize(submittedCanonicalDraft);
        PolicyEditorRetryDecision decision =
            PolicyEditorRetryResolver.Resolve(draftId, management);
        Conflict = new PolicyEditorConflictSnapshot(
            submittedCanonicalRawJson,
            validationReceipt,
            draftId,
            _mutationGeneration,
            decision,
            DateTimeOffset.UtcNow);
    }

    public void CaptureConflict(
        PolicyEditorRetryDecision decision,
        PolicyDraftDocument submittedCanonicalDraft,
        string validationReceipt,
        string draftId)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(submittedCanonicalDraft);
        ArgumentException.ThrowIfNullOrWhiteSpace(validationReceipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        Conflict = new PolicyEditorConflictSnapshot(
            PolicySerializer.Serialize(submittedCanonicalDraft),
            validationReceipt,
            draftId,
            _mutationGeneration,
            decision,
            DateTimeOffset.UtcNow);
    }

    public void ClearConflict() => Conflict = null;

    public bool IsConflictCurrent(PolicyEditorConflictSnapshot conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (!ReferenceEquals(Conflict, conflict)
            || conflict.MutationGeneration != _mutationGeneration)
        {
            return false;
        }

        return TryGetCanonicalEffectiveRaw(out string? canonical, out string? draftId)
            && string.Equals(
                canonical,
                conflict.SubmittedCanonicalRawJson,
                StringComparison.Ordinal)
            && string.Equals(draftId, conflict.DraftId, StringComparison.Ordinal);
    }

    public void MarkSaved(PolicyReplacementResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Management.State != PolicyManagementState.Active
            || response.Management.Policy is null
            || string.IsNullOrWhiteSpace(response.Management.StoreToken))
        {
            throw new InvalidDataException(
                "A successful replacement did not return an active management snapshot.");
        }

        PolicyDocument authoritative = PolicyEditorMapper.CloneDocument(response.Policy);
        OriginManagement = PolicyEditorMapper.CloneManagementSnapshot(response.Management);
        Operation = PolicyEditorOperationKind.Update;
        Draft = PolicyEditorMapper.ToDraft(authoritative);
        RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(Draft);
        Mode = PolicyEditorMode.Structured;
        _baselineRawJson = RawBuffer;
        _lastAnalyzedRawJson = RawBuffer;
        _lastAnalyzedCanonicalRawJson = RawBuffer;
        _lastAnalyzedDraftId = Draft.Metadata.Id;
        _lastAnalyzedMutationGeneration = _mutationGeneration;
        _hasLastAnalyzedRawElement = false;
        IsRawAnalysisPending = false;
        Validation = null;
        Findings = PolicyEditorFindingIndex.Build([]);
        WarningAcknowledgement = null;
        Conflict = null;
    }

    public void MarkSavedPreservingCurrentDraft(PolicyReplacementResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Management.State != PolicyManagementState.Active
            || response.Management.Policy is null
            || string.IsNullOrWhiteSpace(response.Management.StoreToken))
        {
            throw new InvalidDataException(
                "A successful replacement did not return an active management snapshot.");
        }

        OriginManagement = PolicyEditorMapper.CloneManagementSnapshot(response.Management);
        Operation = TryGetCanonicalEffectiveRaw(out _, out string? currentDraftId)
            ? ResolveOperationForDraftId(currentDraftId!)
            : PolicyEditorOperationKind.Update;
        _baselineRawJson = PolicyEditorRawSyntax.ToCanonicalRaw(
            PolicyEditorMapper.ToDraft(response.Policy));
        Validation = null;
        Findings = PolicyEditorFindingIndex.Build([]);
        WarningAcknowledgement = null;
        Conflict = null;
    }

    public PolicyEditorOperationKind ResolveOperationForDraftId(string draftId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        return OriginManagement.State switch
        {
            PolicyManagementState.Active
                when string.Equals(
                    OriginManagement.Policy!.Metadata.Id,
                    draftId,
                    StringComparison.Ordinal) =>
                PolicyEditorOperationKind.Update,
            PolicyManagementState.Active => PolicyEditorOperationKind.ReplaceIdentity,
            PolicyManagementState.Missing => PolicyEditorOperationKind.Create,
            PolicyManagementState.Invalid => PolicyEditorOperationKind.Repair,
            _ => throw new InvalidDataException("The policy management state is not supported."),
        };
    }

    private void InvalidateContentState()
    {
        _mutationGeneration++;
        ClearContentState();
    }

    private void ClearContentState()
    {
        Validation = null;
        Findings = PolicyEditorFindingIndex.Build([]);
        WarningAcknowledgement = null;
        Conflict = null;
    }

    private bool TryGetCanonicalEffectiveRaw(
        out string? canonicalRawJson,
        out string? draftId)
    {
        if (Mode == PolicyEditorMode.Raw)
        {
            if (IsRawAnalysisPending
                || _lastAnalyzedMutationGeneration != _mutationGeneration
                || !string.Equals(
                    _lastAnalyzedRawJson,
                    RawBuffer,
                    StringComparison.Ordinal)
                || string.IsNullOrEmpty(_lastAnalyzedCanonicalRawJson))
            {
                canonicalRawJson = null;
                draftId = null;
                return false;
            }

            canonicalRawJson = _lastAnalyzedCanonicalRawJson;
            draftId = _lastAnalyzedDraftId;
            return true;
        }

        string effectiveRawJson = GetEffectiveRawJson();
        if (Validation is not null
            && string.Equals(
                Validation.SubmittedRawJson,
                effectiveRawJson,
                StringComparison.Ordinal))
        {
            canonicalRawJson = PolicySerializer.Serialize(Validation.CanonicalDraft);
            draftId = Validation.CanonicalDraft.Metadata.Id;
            return true;
        }

        return TryCanonicalizeRaw(effectiveRawJson, out canonicalRawJson, out draftId);
    }

    private static bool TryCanonicalizeRaw(
        string rawJson,
        out string? canonicalRawJson,
        out string? draftId)
    {
        canonicalRawJson = null;
        draftId = null;
        if (!PolicyEditorRawSyntax.TryParseStrict(
                rawJson,
                out PolicyEditorDraftDocument? parsed,
                out _)
            || parsed is null)
        {
            return false;
        }

        PolicyDraftDocument shared = PolicyEditorMapper.ToSharedDraft(parsed);
        canonicalRawJson = PolicySerializer.Serialize(shared);
        draftId = shared.Metadata.Id;
        return true;
    }

    private void EnsureStructuredMode()
    {
        if (Mode != PolicyEditorMode.Structured)
            throw new InvalidOperationException("Rule edits require structured mode.");
    }

    private static IReadOnlyList<string> GetWarningKeys(
        PolicyEditorFindingIndex findings) =>
        findings.All
            .Where(finding => finding.Severity == PolicyValidationSeverity.Warning)
            .Select(finding =>
                $"{finding.Code}\u001f{finding.Pointer}\u001f{finding.RuleId}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void RequireState(
        PolicyManagementSnapshot management,
        PolicyManagementState expected)
    {
        ArgumentNullException.ThrowIfNull(management);
        ArgumentException.ThrowIfNullOrWhiteSpace(management.StoreToken);
        if (management.State != expected)
            throw new ArgumentException(
                $"Expected a {expected} management snapshot.",
                nameof(management));
        if (expected == PolicyManagementState.Active && management.Policy is null)
            throw new ArgumentException(
                "An active management snapshot requires a policy.",
                nameof(management));
    }
}
