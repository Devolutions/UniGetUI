using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorSessionTests
{
    private static PolicyEditorDraftDocument NewDraft(string id = "id-1", string publisher = "Contoso") =>
        PolicyEditorTemplates.CreateNew(id, publisher);

    // ---- StartCreate ------------------------------------------------------------------------

    [Fact]
    public void StartCreate_BeginsWithStructuredModeAndCapturesMissingOrigin()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildMissingManagement("token-missing");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(management, NewDraft());

        Assert.Equal(PolicyEditorOperationKind.Create, session.Operation);
        Assert.Equal(PolicyManagementState.Missing, session.OriginManagement.State);
        Assert.Equal("token-missing", session.OriginManagement.StoreToken);
        Assert.Equal(PolicyEditorMode.Structured, session.Mode);
        Assert.False(session.IsDirty);
        Assert.False(session.IsIdentityLocked);
    }

    [Fact]
    public void StartCreate_RejectsNonMissingManagement()
    {
        PolicyManagementSnapshot active = PolicyEditorTestFixtures.BuildActiveManagement();
        Assert.Throws<ArgumentException>(() => PolicyEditorSession.StartCreate(active, NewDraft()));

        PolicyManagementSnapshot invalid = PolicyEditorTestFixtures.BuildInvalidManagement();
        Assert.Throws<ArgumentException>(() => PolicyEditorSession.StartCreate(invalid, NewDraft()));
    }

    [Fact]
    public void StartCreate_ClonesTheSuppliedDraft_MutatingCallerCopyDoesNotAffectSession()
    {
        PolicyEditorDraftDocument template = NewDraft();
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), template);

        template.Metadata.Description = "mutated after session start";

        Assert.NotEqual("mutated after session start", session.Draft.Metadata.Description);
    }

    // ---- StartUpdate --------------------------------------------------------------------------

    [Fact]
    public void StartUpdate_CapturesExactOriginTokenAndSnapshot()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument origin = PolicyEditorTestFixtures.BuildDocument(rules: rule);
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(origin, "origin-token-123");

        PolicyEditorSession session = PolicyEditorSession.StartUpdate(management);

        Assert.Equal(PolicyEditorOperationKind.Update, session.Operation);
        Assert.Equal("origin-token-123", session.OriginManagement.StoreToken);
        Assert.Equal(origin.Metadata.Id, session.OriginManagement.Policy!.Metadata.Id);
        Assert.Equal(origin.Metadata.Id, session.Draft.Metadata.Id);
        Assert.False(session.IsDirty);
        Assert.True(session.IsIdentityLocked);
    }

    [Fact]
    public void StartUpdate_SnapshotIsIndependentOfCallerDocument()
    {
        PolicyDocument origin = PolicyEditorTestFixtures.BuildDocument();
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(origin, "token");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(management);

        origin.Metadata.Description = "mutated after session start";
        management.Policy!.Metadata.Description = "mutated after session start (via snapshot too)";

        Assert.NotEqual("mutated after session start", session.OriginManagement.Policy!.Metadata.Description);
    }

    [Fact]
    public void StartUpdate_RejectsNonActiveManagement()
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorSession.StartUpdate(PolicyEditorTestFixtures.BuildMissingManagement()));
        Assert.Throws<ArgumentException>(() => PolicyEditorSession.StartUpdate(PolicyEditorTestFixtures.BuildInvalidManagement()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StartUpdate_RejectsEmptyToken(string token)
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(storeToken: token);
        Assert.ThrowsAny<ArgumentException>(() => PolicyEditorSession.StartUpdate(management));
    }

    [Fact]
    public void StartUpdate_RejectsNullManagement()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyEditorSession.StartUpdate(null!));
    }

    // ---- StartReplaceIdentity -------------------------------------------------------------------

    [Fact]
    public void StartReplaceIdentity_RequiresDifferentIdentityFromActivePolicy()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "existing-id"));

        Assert.Throws<ArgumentException>(() =>
            PolicyEditorSession.StartReplaceIdentity(management, NewDraft("existing-id")));
    }

    [Fact]
    public void StartReplaceIdentity_AcceptsDifferentIdentity_OperationIsReplaceIdentity()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "existing-id"));

        PolicyEditorSession session = PolicyEditorSession.StartReplaceIdentity(management, NewDraft("new-id"));

        Assert.Equal(PolicyEditorOperationKind.ReplaceIdentity, session.Operation);
        Assert.Equal("new-id", session.Draft.Metadata.Id);
        Assert.False(session.IsIdentityLocked); // identity is only locked for Update, not ReplaceIdentity
    }

    [Fact]
    public void StartReplaceIdentity_RejectsNonActiveManagement()
    {
        Assert.Throws<ArgumentException>(() =>
            PolicyEditorSession.StartReplaceIdentity(PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft()));
    }

    // ---- StartRepair --------------------------------------------------------------------------

    [Fact]
    public void StartRepair_RequiresInvalidManagement()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildInvalidManagement("token-invalid");

        PolicyEditorSession session = PolicyEditorSession.StartRepair(management, NewDraft());

        Assert.Equal(PolicyEditorOperationKind.Repair, session.Operation);
        Assert.Equal("token-invalid", session.OriginManagement.StoreToken);
    }

    [Fact]
    public void StartRepair_RejectsNonInvalidManagement()
    {
        Assert.Throws<ArgumentException>(() =>
            PolicyEditorSession.StartRepair(PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft()));
        Assert.Throws<ArgumentException>(() =>
            PolicyEditorSession.StartRepair(PolicyEditorTestFixtures.BuildActiveManagement(), NewDraft()));
    }

    // ---- Dirty tracking -------------------------------------------------------------------

    [Fact]
    public void IsDirty_UsesCachedResultUntilExactContentComparisonIsApplied()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());

        session.Draft.Metadata.Description = "changed";
        session.NotifyDraftChanged();
        Assert.True(session.IsDirty);

        session.Draft.Metadata.Description = null;
        session.NotifyDraftChanged();

        Assert.True(session.IsDirty);
        PolicyEditorDirtyComparisonSnapshot snapshot = session.CaptureDirtyComparison();
        string effectiveRaw = session.GetEffectiveRawJson();
        session.TryApplyDirtyComparison(
            snapshot,
            !string.Equals(
                effectiveRaw,
                snapshot.BaselineRawJson,
                StringComparison.Ordinal));

        Assert.False(session.IsDirty);
    }

    [Fact]
    public void IsDirty_TracksRuleListChanges()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        Assert.False(session.IsDirty);

        PolicyEditorDraftRule added = session.AddRule(PolicyRuleFactory.CreateBlank("rule-a"));
        Assert.True(session.IsDirty);

        session.DeleteRule(added.Id);

        Assert.True(session.IsDirty);
        PolicyEditorDirtyComparisonSnapshot snapshot = session.CaptureDirtyComparison();
        string effectiveRaw = session.GetEffectiveRawJson();
        session.TryApplyDirtyComparison(
            snapshot,
            !string.Equals(
                effectiveRaw,
                snapshot.BaselineRawJson,
                StringComparison.Ordinal));

        Assert.False(session.IsDirty);
    }

    // ---- Mode switching / raw buffer -------------------------------------------------------

    [Fact]
    public void SwitchToRaw_RegeneratesRawBufferFromCurrentDraft()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.Draft.Metadata.Description = "custom description";

        session.SwitchToRaw();

        Assert.Equal(PolicyEditorMode.Raw, session.Mode);
        Assert.Contains("custom description", session.RawBuffer, StringComparison.Ordinal);
    }

    [Fact]
    public void SetRawBuffer_OutsideRawMode_Throws()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());

        Assert.Throws<InvalidOperationException>(() => session.SetRawBuffer("{}"));
    }

    [Fact]
    public void GetEffectiveRawJson_ReflectsRawBufferInRawMode_AndCanonicalDraftInStructuredMode()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.Draft.Metadata.Description = "structured description";

        string structuredRaw = session.GetEffectiveRawJson();
        Assert.Contains("structured description", structuredRaw, StringComparison.Ordinal);

        session.SwitchToRaw();
        session.SetRawBuffer("raw override text (not valid json, but that's fine here)");
        Assert.Equal("raw override text (not valid json, but that's fine here)", session.GetEffectiveRawJson());
    }

    // ---- Correction #3: raw->structured splits local syntax parse from authoritative validation ----

    [Fact]
    public void TryParseRaw_InvalidText_ReturnsFalseAndDoesNotMutateSessionState()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        session.SetRawBuffer("{ not valid json ");

        bool ok = session.TryParseRaw(out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
        Assert.Equal("{ not valid json ", session.RawBuffer);
        Assert.Equal(PolicyEditorMode.Raw, session.Mode);
        Assert.Equal("id-1", session.Draft.Metadata.Id); // structured draft is untouched by a local parse
    }

    [Fact]
    public void TryParseRaw_ValidText_ReturnsTrueButStillDoesNotMutateSessionState()
    {
        // This is the crux of correction #3: a syntactically-valid local parse is NOT enough to
        // advance the session into structured mode. Only AcceptValidatedRaw (fed by an authoritative
        // validation result) may do that.
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        PolicyEditorDraftDocument edited = NewDraft();
        edited.Metadata.Description = "edited via raw text";
        session.SetRawBuffer(PolicyEditorRawSyntax.ToCanonicalRaw(edited));

        bool ok = session.TryParseRaw(out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("edited via raw text", parsed!.Metadata.Description);

        // The session itself is untouched: still Raw mode, structured Draft still reflects the
        // pre-edit content.
        Assert.Equal(PolicyEditorMode.Raw, session.Mode);
        Assert.Null(session.Draft.Metadata.Description);
    }

    [Fact]
    public void ApplyValidationResult_InvalidResult_DoesNotAdvanceSessionButRecordsFindings()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        string submitted = session.GetEffectiveRawJson();
        var invalid = new PolicyValidationResult
        {
            IsValid = false,
            CanonicalDraft = null,
            ValidationReceipt = null,
            Findings =
            [
                new PolicyFinding { Path = "/rules", Severity = PolicyFindingSeverity.Error, Message = "bad rule" },
            ],
        };

        session.ApplyValidationResult(submitted, invalid);

        Assert.Null(session.Validation);
        Assert.Single(session.Findings.All);
        Assert.Equal(PolicyEditorMode.Structured, session.Mode); // never switched into raw/structured by this alone
    }

    [Fact]
    public void AcceptValidatedRaw_OnNonAuthoritativeResult_Throws_AndLeavesRawModeAndTextUntouched()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        string submitted = session.RawBuffer;
        var invalid = new PolicyValidationResult { IsValid = false, Findings = [] };

        Assert.Throws<InvalidOperationException>(() => session.AcceptValidatedRaw(submitted, invalid));
        Assert.Equal(PolicyEditorMode.Raw, session.Mode);
        Assert.Equal(submitted, session.RawBuffer);
    }

    [Fact]
    public void AcceptValidatedRaw_OnAuthoritativeValidResult_SwitchesToStructuredUsingCanonicalDraft()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        string submitted = session.RawBuffer;

        var packageCanonical = PolicyEditorMapper.ToSharedDraft(NewDraft());
        packageCanonical.Metadata.Description = "server-canonicalized content";
        var valid = new PolicyValidationResult
        {
            IsValid = true,
            CanonicalDraft = packageCanonical,
            ValidationReceipt = "receipt-1",
            Findings = [],
        };

        session.AcceptValidatedRaw(submitted, valid);

        Assert.Equal(PolicyEditorMode.Structured, session.Mode);
        Assert.Equal("server-canonicalized content", session.Draft.Metadata.Description);
    }

    // ---- Warning acknowledgement tied to exact validated content ---------------------------------

    [Fact]
    public void AcknowledgeWarnings_RequiresACurrentValidationWithWarnings()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());

        Assert.Throws<InvalidOperationException>(session.AcknowledgeWarnings);
    }

    private static void ApplyWarningValidation(PolicyEditorSession session)
    {
        string submitted = session.GetEffectiveRawJson();
        var valid = new PolicyValidationResult
        {
            IsValid = true,
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(session.Draft),
            ValidationReceipt = "receipt-1",
            Findings =
            [
                new PolicyFinding
                {
                    Path = "/rules",
                    Severity = PolicyFindingSeverity.Warning,
                    Message = "example warning",
                },
            ],
        };
        session.ApplyValidationResult(submitted, valid);
    }

    [Fact]
    public void AcknowledgeWarnings_SetsHasCurrentWarningAcknowledgement()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        ApplyWarningValidation(session);

        session.AcknowledgeWarnings();

        Assert.True(session.HasCurrentWarningAcknowledgement);
        Assert.NotNull(session.WarningAcknowledgement);
    }

    [Fact]
    public void AcknowledgeWarnings_IsInvalidatedByAnySubsequentEdit_EvenIfContentIsLaterReverted()
    {
        // Unlike a fingerprint-only scheme, any edit clears the whole validation state (correction #3's
        // "no stale reuse"): reverting to the exact same content does NOT restore the prior
        // acknowledgement, because there is no longer a current Validation to check it against.
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        ApplyWarningValidation(session);
        session.AcknowledgeWarnings();

        session.Draft.Metadata.Description = "temporary change";
        session.NotifyDraftChanged();
        Assert.False(session.HasCurrentWarningAcknowledgement);
        Assert.Null(session.Validation);

        session.Draft.Metadata.Description = null; // revert to the exact content acknowledged before
        session.NotifyDraftChanged();
        Assert.False(session.HasCurrentWarningAcknowledgement); // still false: no current Validation at all
    }

    [Fact]
    public void ClearWarningAcknowledgement_IsImpliedByAnyInvalidatingOperation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        ApplyWarningValidation(session);
        session.AcknowledgeWarnings();

        session.NotifyDraftChanged();

        Assert.False(session.HasCurrentWarningAcknowledgement);
        Assert.Null(session.WarningAcknowledgement);
    }

    // ---- Validation currency ----------------------------------------------------------------

    [Fact]
    public void ApplyValidationResult_IsCurrentWhileDraftUnchanged_AndStaleAfterEdit()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        string rawJson = session.GetEffectiveRawJson();
        var valid = new PolicyValidationResult
        {
            IsValid = true,
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(session.Draft),
            ValidationReceipt = "receipt-1",
            Findings = [],
        };

        session.ApplyValidationResult(rawJson, valid);
        Assert.True(session.IsValidationCurrent);

        session.Draft.Metadata.Description = "invalidates prior validation";
        session.NotifyDraftChanged();
        Assert.False(session.IsValidationCurrent);
    }

    // ---- Conflict snapshot / generation-based staleness suppression (correction #3) --------------

    [Fact]
    public void CaptureConflict_SnapshotsManagementIndependentlyAndResolvesRetryDecision()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        PolicyManagementSnapshot remote = PolicyEditorTestFixtures.BuildMissingManagement("remote-token");

        session.CaptureConflict(
            remote,
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");

        Assert.NotNull(session.Conflict);
        Assert.Equal("remote-token", session.Conflict!.RetryDecision.Token);
        Assert.Equal(PolicyReplacementOperation.Create, session.Conflict.RetryDecision.Operation);

        // Independence: mutating the caller's snapshot after capture must not affect the stored one.
        remote.StoreToken = "mutated-after-capture";
        Assert.Equal("remote-token", session.Conflict.RetryDecision.Token);
    }

    [Fact]
    public void ClearConflict_RemovesIt()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");

        session.ClearConflict();

        Assert.Null(session.Conflict);
    }

    [Fact]
    public void IsConflictCurrent_TrueImmediatelyAfterCapture()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");

        Assert.True(session.IsConflictCurrent(session.Conflict!));
    }

    [Fact]
    public void IsConflictCurrent_FalseAfterDraftMutation_NoStaleRetryAllowed()
    {
        // This is the "no blind force" / generation-suppression guarantee: any semantic draft mutation
        // advances the mutation generation and invalidates the captured overwrite authorization.
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");
        PolicyEditorConflictSnapshot conflict = session.Conflict!;

        session.NotifyDraftChanged();

        Assert.False(session.IsConflictCurrent(conflict));
    }

    [Fact]
    public void IsConflictCurrent_RawFormattingOnlyChangeRetainsConflict()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");
        PolicyEditorConflictSnapshot conflict = session.Conflict!;

        string formatted = $" \r\n{session.RawBuffer}\r\n";
        session.SetRawBuffer(formatted);
        Assert.False(session.IsConflictCurrent(conflict));
        Assert.True(session.CompleteRawAnalysis(
            formatted,
            session.MutationGeneration,
            PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft),
            session.Draft.Metadata.Id,
            null));

        Assert.NotNull(session.Conflict);
        Assert.True(session.IsConflictCurrent(session.Conflict));
    }

    [Fact]
    public void IsConflictCurrent_RawSemanticChangeClearsConflict()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.SwitchToRaw();
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");

        string changed =
            session.RawBuffer.Replace("Contoso", "Other publisher", StringComparison.Ordinal);
        session.SetRawBuffer(changed);
        PolicyEditorDraftDocument parsed = NewDraft();
        parsed.Metadata.Publisher = "Other publisher";
        session.CompleteRawAnalysis(
            changed,
            session.MutationGeneration,
            PolicyEditorRawSyntax.ToCanonicalRaw(parsed),
            parsed.Metadata.Id,
            null);

        Assert.Null(session.Conflict);
    }

    [Fact]
    public void IsConflictCurrent_UsesAuthoritativeCanonicalDraftFromValidation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        string submitted = session.GetEffectiveRawJson();
        PolicyDraftDocument canonical = PolicyEditorMapper.ToSharedDraft(session.Draft);
        canonical.Metadata.Description = "canonicalized by Agent";
        session.ApplyValidationResult(
            submitted,
            new PolicyValidationResult
            {
                IsValid = true,
                CanonicalDraft = canonical,
                ValidationReceipt = "receipt-1",
            });
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            canonical,
            "receipt-1",
            "id-1");

        Assert.True(session.IsConflictCurrent(session.Conflict!));
    }

    [Fact]
    public void IsConflictCurrent_FalseForADifferentConflictInstance()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");
        PolicyEditorConflictSnapshot first = session.Conflict!;

        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement("other"),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "receipt-1",
            "id-1");

        Assert.False(session.IsConflictCurrent(first));
        Assert.True(session.IsConflictCurrent(session.Conflict!));
    }

    // ---- MarkSaved rebasing (correction #2) --------------------------------------------------

    [Fact]
    public void MarkSaved_Create_RebasesOriginFromAuthoritativeResponseAndClearsTransientState()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        session.Draft.Metadata.Description = "unsaved edit";
        session.NotifyDraftChanged();
        ApplyWarningValidation(session);
        session.AcknowledgeWarnings();
        session.CaptureConflict(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorMapper.ToSharedDraft(session.Draft),
            "r",
            "id-1");
        Assert.True(session.IsDirty);

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        authoritative.Metadata.Description = "authoritative saved content";
        PolicyReplacementResponse response = PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "new-token-after-save");

        session.MarkSaved(response);

        Assert.False(session.IsDirty);
        Assert.Equal("new-token-after-save", session.OriginManagement.StoreToken);
        Assert.Equal(PolicyManagementState.Active, session.OriginManagement.State);
        Assert.Equal(PolicyEditorOperationKind.Update, session.Operation);
        Assert.True(session.IsIdentityLocked);
        Assert.Equal("authoritative saved content", session.Draft.Metadata.Description);
        Assert.Null(session.Validation);
        Assert.Null(session.WarningAcknowledgement);
        Assert.Null(session.Conflict);
        Assert.Equal(PolicyEditorMode.Structured, session.Mode);
    }

    [Fact]
    public void MarkSaved_Update_RebasesBaselineSoFurtherEditsAreDirtyAgainRelativeToNewBaseline()
    {
        PolicyDocument origin = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(origin, "token-1"));
        session.Draft.Metadata.Description = "saved content";
        session.NotifyDraftChanged();

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        authoritative.Metadata.Description = "saved content";
        session.MarkSaved(PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "token-2"));
        Assert.False(session.IsDirty);

        session.Draft.Metadata.Description = "another edit after save";
        session.NotifyDraftChanged();
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void MarkSaved_ReplaceIdentity_RebasesToTheNewIdentity()
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "old-id"), "token-1");
        PolicyEditorSession session = PolicyEditorSession.StartReplaceIdentity(management, NewDraft("new-id"));

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "new-id");
        session.MarkSaved(PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "token-2"));

        Assert.Equal("new-id", session.Draft.Metadata.Id);
        Assert.Equal("new-id", session.OriginManagement.Policy!.Metadata.Id);
        Assert.Equal(PolicyEditorOperationKind.Update, session.Operation);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MarkSaved_Repair_RebasesFromInvalidToActive()
    {
        PolicyEditorSession session = PolicyEditorSession.StartRepair(
            PolicyEditorTestFixtures.BuildInvalidManagement("token-invalid"), NewDraft("id-1"));

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        session.MarkSaved(PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "token-repaired"));

        Assert.Equal(PolicyManagementState.Active, session.OriginManagement.State);
        Assert.Equal("token-repaired", session.OriginManagement.StoreToken);
        Assert.Equal(PolicyEditorOperationKind.Update, session.Operation);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MarkSaved_NeverSynthesizesRevisionOrPublishedAt()
    {
        // The draft model has no Revision/PublishedAt members at all, so there is nothing for
        // MarkSaved to synthesize: this documents that guarantee at the type-surface level.
        System.Reflection.PropertyInfo[] props = typeof(PolicyEditorDraftMetadata).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
    }

    [Fact]
    public void MarkSaved_DeepCopiesAuthoritativePolicy_MutatingResponseAfterwardsDoesNotAffectSession()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        PolicyReplacementResponse response = PolicyEditorTestFixtures.BuildReplacementResponse(authoritative);

        session.MarkSaved(response);
        authoritative.Metadata.Description = "mutated after save";
        response.Management.Policy!.Metadata.Description = "mutated after save (management side)";

        Assert.NotEqual("mutated after save", session.Draft.Metadata.Description);
    }

    [Fact]
    public void MarkSaved_RejectsResponseWhoseManagementIsNotActive()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());
        var response = new PolicyReplacementResponse
        {
            Policy = PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
            Management = PolicyEditorTestFixtures.BuildMissingManagement(),
        };

        Assert.Throws<InvalidDataException>(() => session.MarkSaved(response));
    }

    [Fact]
    public void MarkSaved_RejectsNullResponse()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(), NewDraft());

        Assert.Throws<ArgumentNullException>(() => session.MarkSaved(null!));
    }
}
