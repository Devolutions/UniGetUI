using System.Diagnostics;
using System.Text.Json.Nodes;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorSessionViewModelTests
{
    private static (PolicyEditorSessionViewModel ViewModel, FakeValidationClient Validation, FakeConfirmationPrompt Prompt, FakeWriteClient Writer)
        CreateForCreateSession(string id = "id-1", string publisher = "Contoso")
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew(id, publisher));
        var validation = new FakeValidationClient();
        var prompt = new FakeConfirmationPrompt();
        var writer = new FakeWriteClient();
        var vm = new PolicyEditorSessionViewModel(session, validation, prompt, writer);
        return (vm, validation, prompt, writer);
    }

    private static (PolicyEditorSessionViewModel ViewModel, FakeValidationClient Validation, FakeConfirmationPrompt Prompt, FakeWriteClient Writer)
        CreateForUpdateSession(string id = "id-1")
    {
        PolicyManagementSnapshot management = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: id), "token-1");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(management);
        var validation = new FakeValidationClient();
        var prompt = new FakeConfirmationPrompt();
        var writer = new FakeWriteClient();
        var vm = new PolicyEditorSessionViewModel(session, validation, prompt, writer);
        return (vm, validation, prompt, writer);
    }

    private static (PolicyEditorSessionViewModel ViewModel, FakeValidationClient Validation, FakeConfirmationPrompt Prompt, FakeWriteClient Writer)
        CreateForOperation(PolicyEditorOperationKind operation)
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorSession session = operation switch
        {
            PolicyEditorOperationKind.Create => PolicyEditorSession.StartCreate(
                PolicyEditorTestFixtures.BuildMissingManagement(),
                draft),
            PolicyEditorOperationKind.Repair => PolicyEditorSession.StartRepair(
                PolicyEditorTestFixtures.BuildInvalidManagement(),
                draft),
            PolicyEditorOperationKind.ReplaceIdentity => PolicyEditorSession.StartReplaceIdentity(
                PolicyEditorTestFixtures.BuildActiveManagement(
                    PolicyEditorTestFixtures.BuildDocument(id: "old-id"),
                    "token-active"),
                draft),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var validation = new FakeValidationClient();
        var prompt = new FakeConfirmationPrompt();
        var writer = new FakeWriteClient();
        return (
            new PolicyEditorSessionViewModel(session, validation, prompt, writer),
            validation,
            prompt,
            writer);
    }

    private static PolicyValidationResult ValidResultFor(PolicyEditorSessionViewModel vm, string receipt = "receipt-1", List<PolicyFinding>? findings = null) =>
        new()
        {
            IsValid = true,
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(vm.Session.Draft),
            ValidationReceipt = receipt,
            Findings = findings ?? [],
        };

    // ---- ValidateCommand --------------------------------------------------------------------

    [Fact]
    public async Task ValidateCommand_ValidOutcome_AppliesValidationToSession()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Equal(1, validation.CallCount);
        Assert.NotNull(vm.Session.Validation);
        Assert.True(vm.Session.IsValidationCurrent);
    }

    [Fact]
    public async Task ValidateCommand_InvalidOutcome_RecordsFindingsButNoCurrentValidation()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = false,
            Findings = [new PolicyFinding { Path = "/rules", Severity = PolicyFindingSeverity.Error, Message = "bad" }],
        });

        await vm.ValidateCommand.ExecuteAsync(null);

        Assert.Null(vm.Session.Validation);
        Assert.Single(vm.Session.Findings.All);
    }

    // ---- Correction #14: Save reuses an already-current validation instead of re-validating -----

    [Fact]
    public async Task SaveCommand_WhenCurrentValidationStillMatchesUnchangedDraft_DoesNotReValidate()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(1, validation.CallCount);
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "server-token-1"));

        await vm.SaveCommand.ExecuteAsync(null);

        // Save must not call the validation client again: the draft/raw is unchanged and the
        // existing validation (receipt + CanonicalDraft) is still current.
        Assert.Equal(1, validation.CallCount);
        Assert.True(vm.LastSaveSucceeded);
        Assert.Equal("receipt-1", writer.LastRequest!.ValidationReceipt);
    }

    [Fact]
    public async Task SaveCommand_WhenDraftChangedSinceLastValidation_ReValidatesBeforeWrite()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm, receipt: "receipt-1"));
        await vm.ValidateCommand.ExecuteAsync(null);
        Assert.Equal(1, validation.CallCount);

        vm.AddRuleCommand.Execute(null); // mutates the draft -> Session.IsValidationCurrent becomes false
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm, receipt: "receipt-2"));
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "server-token-1"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, validation.CallCount);
        Assert.Equal("receipt-2", writer.LastRequest!.ValidationReceipt);
    }

    [Fact]
    public async Task ConfirmOverwriteCommand_AlwaysReValidatesEvenWhenCurrentValidationStillMatches()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm, receipt: "receipt-1"));
        PolicyManagementSnapshot remote = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "remote-token-2");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = remote, Message = "stale" });
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, validation.CallCount);
        Assert.True(vm.Session.IsValidationCurrent); // draft unchanged, still "current" per fingerprint

        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm, receipt: "receipt-2"));
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "final-token"));

        await vm.ConfirmOverwriteCommand.ExecuteAsync(null);

        // Correction #16: the retry must revalidate for a current receipt rather than resending the
        // now-rejected one, even though nothing in the draft itself changed.
        Assert.Equal(2, validation.CallCount);
        Assert.Equal("receipt-2", writer.LastRequest!.ValidationReceipt);
        Assert.True(vm.LastSaveSucceeded);
    }

    // ---- Raw/structured mode switching (correction #3) ---------------------------------------

    [Fact]
    public void SwitchToRawCommand_SwitchesModeAndClearsSyntaxError()
    {
        (PolicyEditorSessionViewModel vm, _, _, _) = CreateForCreateSession();

        vm.SwitchToRawCommand.Execute(null);

        Assert.Equal(PolicyEditorMode.Raw, vm.Session.Mode);
        Assert.Null(vm.SyntaxError);
    }

    [Fact]
    public async Task SwitchToStructuredCommand_OnInvalidLocalSyntax_PopulatesSyntaxErrorAndStaysRaw_WithoutCallingValidator()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        vm.RawBuffer = "{ not valid json";
        await vm.WaitForRawSyntaxAnalysisAsync();

        await vm.SwitchToStructuredCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SyntaxError);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidJson, vm.SyntaxError.Kind);
        Assert.Equal("The document is not valid JSON", vm.SyntaxErrorTitle);
        Assert.Equal(PolicyEditorMode.Raw, vm.Session.Mode);
        Assert.Equal(0, validation.CallCount); // a local syntax failure never reaches authoritative validation
    }

    [Fact]
    public async Task RawSyntaxAnalysis_OnInvalidDraft_UsesDistinctPolicyDraftTitle()
    {
        (PolicyEditorSessionViewModel vm, _, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        JsonNode root = JsonNode.Parse(vm.RawBuffer)!;
        root["Rules"] = "not-an-array";

        vm.RawBuffer = root.ToJsonString();
        await vm.WaitForRawSyntaxAnalysisAsync();

        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidPolicyDraft, vm.SyntaxError!.Kind);
        Assert.Equal("The document is not a valid policy draft", vm.SyntaxErrorTitle);
    }

    [Fact]
    public async Task RawSyntaxAnalysis_CancelsStaleWorkAndAppliesOnlyTheLatestBuffer()
    {
        (PolicyEditorSessionViewModel vm, _, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        vm.RawBuffer = "{ not valid json";
        vm.RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(vm.Draft);

        await vm.WaitForRawSyntaxAnalysisAsync();

        Assert.Null(vm.SyntaxError);
        Assert.False(vm.IsRawSyntaxPending);
        Assert.True(vm.SwitchToStructuredCommand.CanExecute(null));
    }

    [Fact]
    public async Task RawSyntaxAnalysis_PendingStateBlocksValidationAndSave()
    {
        (PolicyEditorSessionViewModel vm, _, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        vm.RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(vm.Draft) + " ";

        Assert.True(vm.IsRawSyntaxPending);
        Assert.False(vm.ValidateCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SwitchToStructuredCommand.CanExecute(null));

        await vm.WaitForRawSyntaxAnalysisAsync();

        Assert.False(vm.IsRawSyntaxPending);
        Assert.True(vm.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public async Task SwitchToStructuredCommand_LocallyValidButAuthoritativelyInvalid_StaysInRawMode()
    {
        // The core of correction #3: a syntactically valid raw buffer alone must never be treated
        // as accepted; only an authoritative valid result may advance the session.
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = false,
            Findings = [new PolicyFinding { Path = "/metadata", Severity = PolicyFindingSeverity.Error, Message = "server rejects this" }],
        });

        await vm.SwitchToStructuredCommand.ExecuteAsync(null);

        Assert.Equal(1, validation.CallCount);
        Assert.Equal(PolicyEditorMode.Raw, vm.Session.Mode);
        Assert.Single(vm.Session.Findings.All);
    }

    [Fact]
    public async Task SwitchToStructuredCommand_AuthoritativelyValid_SwitchesToStructuredUsingCanonicalDraft()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        PolicyDraftDocument canonical = PolicyEditorMapper.ToSharedDraft(vm.Session.Draft);
        canonical.Metadata.Description = "server canonicalized";
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            CanonicalDraft = canonical,
            ValidationReceipt = "receipt-1",
            Findings = [],
        });

        await vm.SwitchToStructuredCommand.ExecuteAsync(null);

        Assert.Equal(PolicyEditorMode.Structured, vm.Session.Mode);
        Assert.Equal("server canonicalized", vm.Session.Draft.Metadata.Description);
        Assert.Null(vm.SyntaxError);
    }

    [Fact]
    public async Task SwitchToStructuredCommand_StaleGenerationSuppression_IgnoresResultIfRawBufferChangedWhileInFlight()
    {
        // Simulates a slow authoritative validation whose response arrives after the user has already
        // edited the raw buffer again: the (now stale) response must be discarded rather than silently
        // applied, and must not switch the session out of raw mode underneath the user.
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, _) = CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        validation.Gate = new TaskCompletionSource();

        Task switchTask = vm.SwitchToStructuredCommand.ExecuteAsync(null);
        vm.RawBuffer = vm.RawBuffer + " "; // mutate the in-flight buffer before the validator answers
        validation.Gate.SetResult();
        await switchTask;

        Assert.Equal(PolicyEditorMode.Raw, vm.Session.Mode); // never advanced from the stale response
    }

    // ---- SaveCommand: create flow -----------------------------------------------------------

    [Fact]
    public async Task SaveCommand_CreateFlow_PromptsOperationConfirmationThenWritesAndMarksSaved()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "server-token-1"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(PolicyEditorConfirmationKind.Create, prompt.LastRequest!.Kind);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(PolicyReplacementOperation.Create, writer.LastRequest!.Operation);
        Assert.True(vm.LastSaveSucceeded);
        Assert.Equal("server-token-1", vm.Session.OriginManagement.StoreToken);
        Assert.False(vm.Session.IsDirty);
    }

    // ---- Correction #19: stale-create race (another actor created/activated the policy store
    // entry concurrently while this session was still working from a Missing snapshot). The retry
    // must re-derive the operation from the *returned* snapshot (never blindly retry as Create),
    // per correction #21's resolver rules: Active+same id -> Update, Active+different id ->
    // ReplaceIdentity, still Missing (a *different* token raced in) -> Create with the new token.

    [Fact]
    public async Task SaveCommand_CreateFlow_StaleTokenRaceWhereAnotherActorCreatedSameId_ConflictResolvesToUpdate()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot raced = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "raced-token-1");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = raced, Message = "stale" });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.LastSaveSucceeded);
        Assert.NotNull(vm.Session.Conflict);
        Assert.Equal(PolicyReplacementOperation.Update, vm.Session.Conflict!.RetryDecision.Operation);
        Assert.Equal("raced-token-1", vm.Session.Conflict.RetryDecision.Token);
    }

    [Fact]
    public async Task SaveCommand_CreateFlow_StaleTokenRaceWhereAnotherActorCreatedDifferentId_ConflictResolvesToReplaceIdentity()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot raced = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-2"), "raced-token-2");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = raced, Message = "stale" });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Session.Conflict);
        Assert.Equal(PolicyReplacementOperation.ReplaceIdentity, vm.Session.Conflict!.RetryDecision.Operation);
        Assert.Equal("raced-token-2", vm.Session.Conflict.RetryDecision.Token);
    }

    [Fact]
    public async Task SaveCommand_CreateFlow_StaleTokenRaceStillMissingWithNewToken_ConflictResolvesToCreateWithReturnedToken()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot raced = PolicyEditorTestFixtures.BuildMissingManagement("raced-missing-token");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = raced, Message = "stale" });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Session.Conflict);
        Assert.Equal(PolicyReplacementOperation.Create, vm.Session.Conflict!.RetryDecision.Operation);
        Assert.Equal("raced-missing-token", vm.Session.Conflict.RetryDecision.Token);
    }

    [Fact]
    public async Task ConfirmOverwriteCommand_CreateRaceResolvedToUpdate_RetriesWithDerivedOperationAndExactToken()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForCreateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot raced = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "raced-token-1");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = raced, Message = "stale" });
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session.Conflict);

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "final-token"));

        await vm.ConfirmOverwriteCommand.ExecuteAsync(null);

        // The retry must never blindly resend the original Create request: it targets the exact
        // token/operation the server just returned, confirmed via a fresh ConfirmOverwrite prompt.
        Assert.Equal(PolicyEditorConfirmationKind.ConfirmOverwrite, prompt.LastRequest!.Kind);
        Assert.Equal(PolicyReplacementOperation.Update, prompt.LastRequest.Operation);
        Assert.Equal("raced-token-1", prompt.LastRequest.ExpectedStoreToken);
        Assert.Equal(PolicyReplacementOperation.Update, writer.LastRequest!.Operation);
        Assert.Equal("raced-token-1", writer.LastRequest.ExpectedStoreToken);
        Assert.Equal(PolicyConflictHandling.ConfirmOverwrite, writer.LastRequest.ConflictHandling);
        Assert.True(vm.LastSaveSucceeded);
        Assert.Equal("final-token", vm.Session.OriginManagement.StoreToken);
    }

    [Fact]
    public async Task SaveCommand_UserDeclinesOperationConfirmation_DoesNotWrite()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForCreateSession();
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        prompt.NextResult = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, writer.CallCount);
        Assert.False(vm.LastSaveSucceeded);
    }

    [Fact]
    public async Task SaveCommand_ValidationInvalid_DoesNotWrite()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) = CreateForCreateSession();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = false,
            Findings = [new PolicyFinding { Path = "/rules", Severity = PolicyFindingSeverity.Error, Message = "bad" }],
        });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, writer.CallCount);
        Assert.False(vm.LastSaveSucceeded);
        Assert.True(vm.HasFindings);
        Assert.Contains(nameof(PolicyEditorSessionViewModel.Findings), changedProperties);
    }

    // ---- SaveCommand: update flow (no operation confirmation, but warning ack path) -----------

    [Fact]
    public async Task SaveCommand_UpdateFlow_NoOperationConfirmationRequired()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "token-2"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, prompt.CallCount); // Update never requires an operation-kind confirmation
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(PolicyReplacementOperation.Update, writer.LastRequest!.Operation);
        Assert.True(vm.LastSaveSucceeded);
    }

    [Fact]
    public async Task SaveCommand_UnknownWriteResult_PreservesDraftAndBlocksRetryUntilRefresh()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        vm.Session.Draft.Metadata.Description = "unsaved change";
        vm.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.NextOutcome = PolicyWriteOutcome.Failure(PolicyWriteFailureKind.WriteResultUnknown);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.LastSaveSucceeded);
        Assert.True(vm.RequiresManagementRefresh);
        Assert.Equal(PolicyWriteFailureKind.WriteResultUnknown, vm.LastWriteFailureKind);
        Assert.Equal("unsaved change", vm.Session.Draft.Metadata.Description);
        Assert.True(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ValidateCommand.CanExecute(null));
        Assert.False(vm.ConfirmOverwriteCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_CancelAfterDispatchedAuthenticatedCommit_AppliesAuthoritativeSuccess()
    {
        PolicyManagementSnapshot initial = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
            "token-1");
        var validation = new FakeValidationClient();
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        authoritative.Metadata.Description = "committed change";
        PolicyManagementSnapshot refreshed = PolicyEditorTestFixtures.BuildActiveManagement(
            authoritative,
            "committed-token");
        var management = new GatedManagementService(
            new(BrokerPolicyManagementStatus.Retrieved, refreshed));
        var writer = new WindowsPolicyEditorWriteClient(
            new CommittedElevator("committed-token"),
            management);
        using var vm = new PolicyEditorSessionViewModel(
            PolicyEditorSession.StartUpdate(initial),
            validation,
            new FakeConfirmationPrompt(),
            writer);
        vm.Session.Draft.Metadata.Description = "committed change";
        vm.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));

        Task save = vm.SaveCommand.ExecuteAsync(null);
        await management.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SaveCommand.Cancel();
        management.RefreshGate.SetResult();
        await save;

        Assert.True(vm.LastSaveSucceeded);
        Assert.False(vm.IsDirty);
        Assert.False(vm.RequiresManagementRefresh);
        Assert.Equal("committed-token", vm.Session.OriginManagement.StoreToken);
        Assert.Equal("committed change", vm.Session.Draft.Metadata.Description);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_RealAdapterPreDispatchCancellation_LeavesDraftRetryableWithoutFailure()
    {
        PolicyManagementSnapshot initial = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
            "token-1");
        var validation = new FakeValidationClient();
        var elevator = new GatedCancelledElevator();
        var writer = new WindowsPolicyEditorWriteClient(
            elevator,
            new FailIfCalledManagementService());
        using var vm = new PolicyEditorSessionViewModel(
            PolicyEditorSession.StartUpdate(initial),
            validation,
            new FakeConfirmationPrompt(),
            writer);
        vm.Session.Draft.Metadata.Description = "keep this draft";
        vm.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));

        Task save = vm.SaveCommand.ExecuteAsync(null);
        await elevator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SaveCommand.Cancel();
        elevator.Release.SetResult();
        try
        {
            await save;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(vm.LastSaveSucceeded);
        Assert.Equal(PolicyWriteFailureKind.None, vm.LastWriteFailureKind);
        Assert.Null(vm.LastErrorCode);
        Assert.Equal("", vm.StatusMessage);
        Assert.Equal("keep this draft", vm.Session.Draft.Metadata.Description);
        Assert.True(vm.IsDirty);
        Assert.False(vm.RequiresManagementRefresh);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveCommand_CancelAfterDispatchedAuthenticatedRejection_AppliesRejection()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        vm.Session.Draft.Metadata.Description = "preserve me";
        vm.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.Gate = new TaskCompletionSource();
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.InvalidPolicy });

        Task save = vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, writer.CallCount);
        vm.SaveCommand.Cancel();
        writer.Gate.SetResult();
        await save;

        Assert.False(vm.LastSaveSucceeded);
        Assert.Equal(PolicyWriteFailureKind.BrokerRejected, vm.LastWriteFailureKind);
        Assert.Equal(ErrorCode.InvalidPolicy, vm.LastErrorCode);
        Assert.Equal("preserve me", vm.Session.Draft.Metadata.Description);
        Assert.True(vm.IsDirty);
        Assert.False(vm.RequiresManagementRefresh);
    }

    [Fact]
    public async Task SaveCommand_AuthenticatedRejectionForSupersededDraftGeneration_IsIgnored()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        vm.Session.Draft.Metadata.Description = "submitted";
        vm.NotifyDraftChangedCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.Gate = new TaskCompletionSource();
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.InvalidPolicy });

        Task save = vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, writer.CallCount);
        vm.Session.Draft.Metadata.Description = "newer generation";
        vm.NotifyDraftChangedCommand.Execute(null);
        writer.Gate.SetResult();
        await save;

        Assert.False(vm.LastSaveSucceeded);
        Assert.Equal(PolicyWriteFailureKind.None, vm.LastWriteFailureKind);
        Assert.Null(vm.LastErrorCode);
        Assert.Equal("newer generation", vm.Session.Draft.Metadata.Description);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task SaveCommand_WarningsPresent_RequiresAcknowledgement_DeclinedDoesNotWrite()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForUpdateSession();
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(
            vm,
            findings: [new PolicyFinding { Path = "/rules", Severity = PolicyFindingSeverity.Warning, Message = "check this" }]));
        prompt.NextResult = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(PolicyEditorConfirmationKind.Warnings, prompt.LastRequest!.Kind);
        Assert.Equal(0, writer.CallCount);
        Assert.False(vm.LastSaveSucceeded);
    }

    [Fact]
    public async Task SaveCommand_WarningPromptUsesAuthoritativeCountBeyondDisplayedFindingLimit()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, _) =
            CreateForUpdateSession();
        List<PolicyFinding> warnings = Enumerable.Range(
                0,
                PolicyEditorFindingIndex.MaxDisplayedFindings + 7)
            .Select(index => new PolicyFinding
            {
                Path = $"/Rules/{index}",
                Severity = PolicyFindingSeverity.Warning,
                Message = "warning",
            })
            .ToList();
        validation.NextOutcome = new PolicyEditorValidationOutcome(
            ValidResultFor(vm, findings: warnings),
            BoundedFindings:
            [
                new PolicyValidationFinding(
                    "/Rules/0",
                    null,
                    PolicyValidationSeverity.Warning,
                    "warning"),
            ],
            OmittedFindingCount: warnings.Count - 1);
        prompt.NextResult = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(warnings.Count, prompt.LastRequest!.WarningCount);
        Assert.Equal(2, prompt.LastRequest.Findings.Count);
        Assert.Contains(
            prompt.LastRequest.Findings,
            finding => finding.Message.Contains("additional validation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveCommand_WarningsAcknowledged_ProceedsToWriteWithWarningsAcknowledgedFlagSet()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(
            vm,
            findings: [new PolicyFinding { Path = "/rules", Severity = PolicyFindingSeverity.Warning, Message = "check this" }]));
        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "token-2"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.LastSaveSucceeded);
        Assert.True(writer.LastRequest!.WarningsAcknowledged);
        Assert.True(vm.Session.IsDirty is false);
    }

    [Theory]
    [InlineData(PolicyEditorOperationKind.Create)]
    [InlineData(PolicyEditorOperationKind.Repair)]
    [InlineData(PolicyEditorOperationKind.ReplaceIdentity)]
    public async Task SuccessfulInflightSave_RebasesOperationForNewerDraftAndNextSave(
        PolicyEditorOperationKind originalOperation)
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForOperation(originalOperation);
        writer.Gate = new TaskCompletionSource();
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-first-save"));

        Task firstSave = vm.SaveCommand.ExecuteAsync(null);
        if (originalOperation == PolicyEditorOperationKind.ReplaceIdentity)
        {
            vm.Session.Draft.Metadata.Id = "id-2";
        }
        else
        {
            vm.Session.Draft.Metadata.Description = "newer edit";
        }
        vm.NotifyDraftChangedCommand.Execute(null);
        writer.Gate.SetResult();
        await firstSave;

        PolicyEditorOperationKind expectedRebased =
            originalOperation == PolicyEditorOperationKind.ReplaceIdentity
                ? PolicyEditorOperationKind.ReplaceIdentity
                : PolicyEditorOperationKind.Update;
        Assert.Equal(expectedRebased, vm.Operation);
        Assert.Equal(
            expectedRebased == PolicyEditorOperationKind.Update,
            vm.IsIdentityLocked);
        Assert.True(vm.SavedWithNewerChanges);
        Assert.True(vm.IsDirty);
        Assert.Equal("token-after-first-save", vm.Session.OriginManagement.StoreToken);

        writer.Gate = null;
        validation.NextOutcome = new PolicyEditorValidationOutcome(
            ValidResultFor(vm, receipt: "receipt-next-save"));
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: vm.Draft.Metadata.Id),
                "token-after-next-save"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            expectedRebased == PolicyEditorOperationKind.Update
                ? PolicyReplacementOperation.Update
                : PolicyReplacementOperation.ReplaceIdentity,
            writer.LastRequest!.Operation);
        Assert.Equal(PolicyEditorOperationKind.Update, vm.Operation);
        Assert.False(vm.SavedWithNewerChanges);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SuccessfulInflightSave_ExactStructuredRevertClearsNewerChangesStatus()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession();
        string? originalDescription = vm.Draft.Metadata.Description;
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.Gate = new TaskCompletionSource();
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-save"));

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.Draft.Metadata.Description = "temporary newer edit";
        vm.NotifyDraftChangedCommand.Execute(null);
        vm.Draft.Metadata.Description = originalDescription;
        vm.NotifyDraftChangedCommand.Execute(null);
        writer.Gate.SetResult();
        await save;
        await vm.WaitForStructuredDirtyAnalysisAsync();

        Assert.True(vm.LastSaveSucceeded);
        Assert.False(vm.SavedWithNewerChanges);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SuccessfulInflightSave_ExactRawRevertUsesNewAuthoritativeBaseline()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForUpdateSession();
        vm.SwitchToRawCommand.Execute(null);
        string submitted = vm.RawBuffer;
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.Gate = new TaskCompletionSource();
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-save"));

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.RawBuffer = "{";
        vm.RawBuffer = submitted;
        writer.Gate.SetResult();
        await save;
        await vm.WaitForRawSyntaxAnalysisAsync();

        Assert.True(vm.LastSaveSucceeded);
        Assert.False(vm.SavedWithNewerChanges);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.SyntaxError);
    }

    [Fact]
    public async Task SuccessfulInflightSave_WithInvalidRawEdit_NextSameIdentitySaveUsesUpdate()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        writer.Gate = new TaskCompletionSource();
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-first-save"));

        Task firstSave = vm.SaveCommand.ExecuteAsync(null);
        vm.RawBuffer = "{";
        await vm.WaitForRawSyntaxAnalysisAsync();
        writer.Gate.SetResult();
        await firstSave;

        Assert.Equal("{", vm.RawBuffer);
        Assert.NotNull(vm.SyntaxError);
        Assert.Equal(PolicyEditorOperationKind.Update, vm.Operation);
        Assert.True(vm.SavedWithNewerChanges);

        writer.Gate = null;
        vm.RawBuffer = PolicyEditorRawSyntax.ToCanonicalRaw(vm.Draft);
        await vm.WaitForRawSyntaxAnalysisAsync();
        validation.NextOutcome = new PolicyEditorValidationOutcome(
            ValidResultFor(vm, receipt: "receipt-next-save"));
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-next-save"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(PolicyReplacementOperation.Update, writer.LastRequest!.Operation);
        Assert.Equal("token-after-first-save", writer.LastRequest.ExpectedStoreToken);
        Assert.True(vm.LastSaveSucceeded);
    }

    [Fact]
    public async Task SuccessfulInflightSave_RawEditBeforeDebounce_BlocksReuseOfPriorAnalyzedElement()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) =
            CreateForCreateSession();
        vm.SwitchToRawCommand.Execute(null);
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        writer.Gate = new TaskCompletionSource();
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "id-1"),
                "token-after-first-save"));

        Task firstSave = vm.SaveCommand.ExecuteAsync(null);
        JsonNode edited = JsonNode.Parse(vm.RawBuffer)!;
        edited["Metadata"]!["Description"] = "raw edit B";
        string rawB = edited.ToJsonString();
        vm.RawBuffer = rawB;
        writer.Gate.SetResult();
        await firstSave;

        Assert.True(vm.IsRawSyntaxPending);
        Assert.False(vm.SaveCommand.CanExecute(null));

        await vm.WaitForRawSyntaxAnalysisAsync();
        Assert.False(vm.IsRawSyntaxPending);
        Assert.True(PolicyEditorRawSyntax.TryParseStrict(
            rawB,
            out PolicyEditorDraftDocument? parsedB,
            out _));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-B",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(parsedB!),
            Findings = [],
        });
        PolicyDocument committedB = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        committedB.Metadata.Description = "raw edit B";
        writer.Gate = null;
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                committedB,
                "token-after-second-save"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, validation.CallCount);
        Assert.Equal(
            "raw edit B",
            validation.LastDraft.GetProperty("Metadata").GetProperty("Description").GetString());
        Assert.Equal(
            "raw edit B",
            writer.LastRequest!.Draft.GetProperty("Metadata").GetProperty("Description").GetString());
        Assert.True(vm.LastSaveSucceeded);
    }

    // ---- Stale-token conflict capture and confirmed retry -------------------------------------

    [Fact]
    public async Task SaveCommand_StaleTokenRejection_CapturesConflictWithoutMarkingSaved()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) = CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot remote = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "remote-token-2");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = remote, Message = "stale" });

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.LastSaveSucceeded);
        Assert.NotNull(vm.Session.Conflict);
        Assert.Equal("remote-token-2", vm.Session.Conflict!.RetryDecision.Token);
        Assert.True(vm.HasConflict);
    }

    [Fact]
    public async Task ConfirmOverwriteCommand_ConfirmedAndStillCurrent_WritesWithConfirmOverwriteHandling()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, FakeConfirmationPrompt prompt, FakeWriteClient writer) =
            CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot remote = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "remote-token-2");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = remote, Message = "stale" });
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session.Conflict);

        PolicyDocument authoritative = PolicyEditorTestFixtures.BuildDocument(id: "id-1");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(authoritative, "final-token"));

        await vm.ConfirmOverwriteCommand.ExecuteAsync(null);

        Assert.Equal(PolicyEditorConfirmationKind.ConfirmOverwrite, prompt.LastRequest!.Kind);
        Assert.Equal(PolicyConflictHandling.ConfirmOverwrite, writer.LastRequest!.ConflictHandling);
        Assert.True(vm.LastSaveSucceeded);
        Assert.Null(vm.Session.Conflict);
    }

    [Fact]
    public async Task ConfirmOverwriteCommand_ConflictWentStaleBeforeConfirmation_ClearsConflictWithoutWriting_NoBlindForce()
    {
        (PolicyEditorSessionViewModel vm, FakeValidationClient validation, _, FakeWriteClient writer) = CreateForUpdateSession("id-1");
        validation.NextOutcome = new PolicyEditorValidationOutcome(ValidResultFor(vm));
        PolicyManagementSnapshot remote = PolicyEditorTestFixtures.BuildActiveManagement(
            PolicyEditorTestFixtures.BuildDocument(id: "id-1"), "remote-token-2");
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.StalePolicyStoreToken, Management = remote, Message = "stale" });
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session.Conflict);

        // The user edits the draft again before confirming the overwrite: the captured conflict is no
        // longer current, so ConfirmOverwrite must refuse to blindly force the write through.
        vm.Session.Draft.Metadata.Description = "one more edit";
        vm.NotifyDraftChangedCommand.Execute(null);
        int writeCountBefore = writer.CallCount;

        await vm.ConfirmOverwriteCommand.ExecuteAsync(null);

        Assert.Equal(writeCountBefore, writer.CallCount); // no additional write happened
        Assert.Null(vm.Session.Conflict);
        Assert.False(vm.LastSaveSucceeded);
    }

    // ---- ConfirmDiscardAsync ------------------------------------------------------------------

    [Fact]
    public async Task ConfirmDiscardAsync_NotDirty_ReturnsTrueWithoutPrompting()
    {
        (PolicyEditorSessionViewModel vm, _, FakeConfirmationPrompt prompt, _) = CreateForCreateSession();

        bool result = await vm.ConfirmDiscardAsync();

        Assert.True(result);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task ConfirmDiscardAsync_Dirty_PromptsAndReturnsUserDecision()
    {
        (PolicyEditorSessionViewModel vm, _, FakeConfirmationPrompt prompt, _) = CreateForCreateSession();
        vm.Session.Draft.Metadata.Description = "unsaved edit";
        vm.NotifyDraftChangedCommand.Execute(null);
        prompt.NextResult = false;

        bool result = await vm.ConfirmDiscardAsync();

        Assert.False(result);
        Assert.Equal(1, prompt.CallCount);
        Assert.Equal(PolicyEditorConfirmationKind.DiscardChanges, prompt.LastRequest!.Kind);
    }

    [Fact]
    public async Task ConfirmDiscardAsync_ExactRevertBeforeDebounce_DoesNotPrompt()
    {
        (PolicyEditorSessionViewModel vm, _, FakeConfirmationPrompt prompt, _) =
            CreateForCreateSession();
        vm.Session.Draft.Metadata.Description = "temporary";
        vm.NotifyDraftChangedCommand.Execute(null);
        vm.Session.Draft.Metadata.Description = null;
        vm.NotifyDraftChangedCommand.Execute(null);
        Assert.True(vm.IsDirty);

        bool result = await vm.ConfirmDiscardAsync();

        Assert.True(result);
        Assert.False(vm.IsDirty);
        Assert.Equal(0, prompt.CallCount);
    }

    [Fact]
    public async Task StructuredDirtyAnalysis_ExactRevertReturnsToClean()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("id-1", "Contoso"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient(),
            structuredDirtyDebounce: TimeSpan.Zero);
        var document = new PolicyEditorDocumentUi(viewModel);

        document.Description = "changed";
        Assert.True(viewModel.IsDirty);
        await viewModel.WaitForStructuredDirtyAnalysisAsync();
        Assert.True(viewModel.IsDirty);

        document.Description = null;
        Assert.True(viewModel.IsDirty);
        await viewModel.WaitForStructuredDirtyAnalysisAsync();

        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task StructuredDirtyAnalysis_SuppressesCancelledStaleComparison()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("id-1", "Contoso"));
        string baseline = session.GetEffectiveRawJson();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PolicyEditorDraftDocument? firstSerializedDraft = null;
        int calls = 0;
        string Serialize(PolicyEditorDraftDocument draft)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstSerializedDraft = draft;
                firstStarted.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                firstFinished.TrySetResult();
                return baseline;
            }

            return PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        }

        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient(),
            structuredDirtyDebounce: TimeSpan.Zero,
            structuredDraftSerializer: Serialize);
        var document = new PolicyEditorDocumentUi(viewModel);

        document.Description = "first";
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotSame(session.Draft, firstSerializedDraft);
        Assert.Equal("first", firstSerializedDraft!.Metadata.Description);
        document.Description = "second";
        Assert.Equal("first", firstSerializedDraft.Metadata.Description);
        await viewModel.WaitForStructuredDirtyAnalysisAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsDirty);

        releaseFirst.TrySetResult();
        await firstFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(20);

        Assert.True(viewModel.IsDirty);
        Assert.Equal("second", viewModel.Draft.Metadata.Description);
    }

    [Fact]
    public void StructuredDirtyGetter_DoesNotSerializeLargeDraftSynchronously()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        draft.Metadata.Description = new string('x', 4 * 1024 * 1024);
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient(),
            structuredDirtyDebounce: TimeSpan.FromMinutes(1));
        var document = new PolicyEditorDocumentUi(viewModel);

        var stopwatch = Stopwatch.StartNew();
        document.Publisher = "Fabrikam";
        for (int index = 0; index < 10_000; index++)
        {
            Assert.True(viewModel.IsDirty);
        }
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Constant-time dirty reads took {stopwatch.Elapsed}.");
    }

    private sealed class CommittedElevator(string committedStoreToken) : IPolicyWriteElevator
    {
        public Task<PolicyElevationResult> ReplacePolicyAsync(
            PolicyElevationWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PolicyElevationResult(
                PolicyElevationOutcome.Replaced,
                request,
                CommittedStoreToken: committedStoreToken));
        }
    }

    private sealed class GatedManagementService(BrokerPolicyManagementResult result)
        : IBrokerPolicyManagementService
    {
        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RefreshGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BrokerPolicyManagementResult> GetManagementAsync(
            CancellationToken cancellationToken)
        {
            RefreshStarted.TrySetResult();
            await RefreshGate.Task;
            return result;
        }

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            System.Text.Json.JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class GatedCancelledElevator : IPolicyWriteElevator
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PolicyElevationResult> ReplacePolicyAsync(
            PolicyElevationWriteRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            return new PolicyElevationResult(PolicyElevationOutcome.Cancelled, request);
        }
    }

    private sealed class FailIfCalledManagementService : IBrokerPolicyManagementService
    {
        public Task<BrokerPolicyManagementResult> GetManagementAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Cancellation must not trigger management refresh.");

        public Task<BrokerPolicyValidationOutcome> ValidateAsync(
            System.Text.Json.JsonElement draft,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
