using Avalonia.Automation;
using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using ModelDecision = Devolutions.Now.Policy.Model.Decision;
using ModelOperation = Devolutions.Now.Policy.Model.Operation;

namespace UniGetUI.Tests.PolicyEditor;

/// <summary>
/// Covers the typed-input guard contract of the UI-only wrappers in
/// <c>PolicyEditorStructuredUi.cs</c> (<see cref="PolicyEditorDocumentUi"/> and
/// <see cref="PolicyEditorRuleUi"/>): invalid text typed into <c>ValidFromText</c>/<c>ValidUntilText</c>/
/// a localized local error, and must block <c>SaveCommand</c>/<c>SaveCommand</c> until corrected.
/// Blank date text must clear the underlying value rather than error. The Save button's <c>IsEnabled</c>
/// binding (<c>CanValidateOrSave</c>) and <c>SaveCommand.CanExecute</c> are asserted to always agree,
/// since both are wired to the same busy/error guard and must never diverge.
/// </summary>
public class PolicyEditorStructuredInputGuardTests
{
    [Fact]
    public void ValidityControls_ProjectOffsetInstantToLocalTimeWithoutChangingInstant()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        DateTimeOffset source = DateTimeOffset.Parse("2026-08-29T12:34:56+09:00");
        viewModel.Draft.Metadata.ValidFrom = source;
        var document = new PolicyEditorDocumentUi(viewModel);
        DateTimeOffset expectedLocal = TimeZoneInfo.ConvertTime(source, TimeZoneInfo.Local);

        Assert.Equal(expectedLocal.Date, document.ValidFromDate!.Value.Date);
        Assert.Equal(expectedLocal.TimeOfDay, document.ValidFromTime);

        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(viewModel.Draft);
        Assert.True(PolicyEditorRawSyntax.TryParseStrict(
            raw,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        Assert.Null(error);
        Assert.Equal(source.ToUniversalTime(), parsed!.Metadata.ValidFrom!.Value.ToUniversalTime());
    }

    [Fact]
    public void EmptyValidityControls_CombineDateAndTimeInEitherSelectionOrder()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        DateTime firstDate = new(2026, 10, 15);
        DateTime secondDate = new(2027, 1, 20);

        document.ValidFromDate = new DateTimeOffset(
            firstDate,
            TimeZoneInfo.Local.GetUtcOffset(firstDate));

        Assert.Null(viewModel.Draft.Metadata.ValidFrom);
        Assert.Equal(firstDate, document.ValidFromDate!.Value.Date);
        Assert.Null(document.ValidFromTime);
        Assert.NotNull(document.ValidFromError);
        Assert.True(viewModel.IsDirty);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.SwitchToRawCommand.CanExecute(null));

        document.ValidFromTime = TimeSpan.FromHours(12);

        Assert.NotNull(viewModel.Draft.Metadata.ValidFrom);
        Assert.Equal(firstDate, document.ValidFromDate!.Value.Date);
        Assert.Equal(TimeSpan.FromHours(12), document.ValidFromTime);
        Assert.Null(document.ValidFromError);

        document.ClearValidFromCommand.Execute(null);
        document.ValidUntilTime = TimeSpan.FromHours(18);

        Assert.Null(viewModel.Draft.Metadata.ValidUntil);
        Assert.Null(document.ValidUntilDate);
        Assert.Equal(TimeSpan.FromHours(18), document.ValidUntilTime);
        Assert.NotNull(document.ValidUntilError);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        document.ValidUntilDate = new DateTimeOffset(
            secondDate,
            TimeZoneInfo.Local.GetUtcOffset(secondDate));

        Assert.NotNull(viewModel.Draft.Metadata.ValidUntil);
        Assert.Equal(secondDate, document.ValidUntilDate!.Value.Date);
        Assert.Equal(TimeSpan.FromHours(18), document.ValidUntilTime);
        Assert.Null(document.ValidUntilError);
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(viewModel.Draft);
        Assert.Contains("\"ValidUntil\"", raw);
        Assert.DoesNotContain("\"ValidFrom\"", raw);
    }

    [Fact]
    public void ValidityControls_BlockEqualOrInvertedWindowAndClearRestoresValidity()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        document.ValidFromText = "2026-08-29T12:00:00Z";
        document.ValidUntilText = "2026-08-29T12:00:00Z";

        Assert.Contains("later than", document.ValidUntilError);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        document.ValidUntilText = "2026-08-29T11:59:59Z";
        Assert.Contains("later than", document.ValidUntilError);
        document.ClearValidUntil();

        Assert.Null(document.ValidUntilError);
        Assert.Null(viewModel.Draft.Metadata.ValidUntil);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void ValidityClearCommands_ResetOnlyTheirEndpointAndOmitItFromJson()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        var changed = new HashSet<string?>();
        document.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        document.ValidFromText = "2026-08-29T12:00:00Z";
        document.ValidUntilText = "2026-08-30T12:00:00Z";
        Assert.True(viewModel.IsDirty);
        changed.Clear();

        document.ClearValidFromCommand.Execute(null);

        Assert.Null(viewModel.Draft.Metadata.ValidFrom);
        Assert.Null(document.ValidFromDate);
        Assert.Null(document.ValidFromTime);
        Assert.Null(document.ValidFromError);
        Assert.NotNull(viewModel.Draft.Metadata.ValidUntil);
        Assert.DoesNotContain(
            "\"ValidFrom\"",
            PolicyEditorRawSyntax.ToCanonicalRaw(viewModel.Draft));
        Assert.Contains(
            "\"ValidUntil\"",
            PolicyEditorRawSyntax.ToCanonicalRaw(viewModel.Draft));
        Assert.Contains(nameof(PolicyEditorDocumentUi.ValidFromDate), changed);
        Assert.Contains(nameof(PolicyEditorDocumentUi.ValidFromTime), changed);
        Assert.Contains(nameof(PolicyEditorDocumentUi.IsOutsideValidityWindow), changed);

        changed.Clear();
        document.ClearValidUntilCommand.Execute(null);

        Assert.Null(viewModel.Draft.Metadata.ValidUntil);
        Assert.Null(document.ValidUntilDate);
        Assert.Null(document.ValidUntilTime);
        Assert.Null(document.ValidUntilError);
        Assert.False(document.IsOutsideValidityWindow);
        Assert.DoesNotContain(
            "\"ValidUntil\"",
            PolicyEditorRawSyntax.ToCanonicalRaw(viewModel.Draft));
        Assert.Contains(nameof(PolicyEditorDocumentUi.ValidUntilDate), changed);
        Assert.Contains(nameof(PolicyEditorDocumentUi.ValidUntilTime), changed);
        Assert.Contains(nameof(PolicyEditorDocumentUi.IsOutsideValidityWindow), changed);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public void ValidityControls_ShowCurrentTimeZoneAndOutsideWindowAdvisory()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        viewModel.Draft.Metadata.ValidFrom = DateTimeOffset.UtcNow.AddDays(1);
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.Contains("UTC", document.LocalTimeZoneText);
        Assert.True(document.IsOutsideValidityWindow);
        Assert.Contains("rejected", document.ValidityWindowAdvisory);
    }

    [Fact]
    public void InvalidValidFromText_IsRetained_ExposesLocalizedError_AndBlocksValidateAndSave()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        AssertSaveGuardAgrees(viewModel);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        document.ValidFromText = "not a date";

        Assert.Equal("not a date", document.ValidFromText);
        Assert.False(string.IsNullOrEmpty(document.ValidFromError));
        Assert.Null(viewModel.Draft.Metadata.ValidFrom);
        Assert.True(viewModel.HasLocalInputErrors);
        Assert.True(viewModel.IsDirty);
        Assert.False(viewModel.CanValidateOrSave);
        AssertSaveGuardAgrees(viewModel);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        document.ValidFromText = "2026-08-29T12:34:56Z";

        Assert.Null(document.ValidFromError);
        Assert.Equal("2026-08-29T12:34:56Z", document.ValidFromText);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-29T12:34:56Z"),
            viewModel.Draft.Metadata.ValidFrom);
        Assert.False(viewModel.HasLocalInputErrors);
        Assert.True(viewModel.CanValidateOrSave);
        AssertSaveGuardAgrees(viewModel);
    }

    [Fact]
    public async Task InvalidWrapperOnlyEdit_RequiresDiscardConfirmation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        var prompt = new FakeConfirmationPrompt { NextResult = false };
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            prompt,
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        document.ValidFromText = "not a date";
        bool discarded = await viewModel.ConfirmDiscardAsync();

        Assert.True(viewModel.IsDirty);
        Assert.False(discarded);
        Assert.Equal(PolicyEditorConfirmationKind.DiscardChanges, prompt.LastRequest!.Kind);
    }

    [Fact]
    public void InvalidValidUntilText_IsRetained_ExposesLocalizedError_AndBlocksValidateAndSave()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        document.ValidUntilText = "banana";

        Assert.Equal("banana", document.ValidUntilText);
        Assert.False(string.IsNullOrEmpty(document.ValidUntilError));
        Assert.Null(viewModel.Draft.Metadata.ValidUntil);
        Assert.True(viewModel.HasLocalInputErrors);
        AssertSaveGuardAgrees(viewModel);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        document.ValidUntilText = "";

        Assert.Null(document.ValidUntilError);
        Assert.Null(viewModel.Draft.Metadata.ValidUntil);
        Assert.False(viewModel.HasLocalInputErrors);
        AssertSaveGuardAgrees(viewModel);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void BlankValidFromText_ClearsTheUnderlyingValueWithoutError()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        document.ValidFromText = "2026-08-29T12:34:56Z";
        Assert.NotNull(viewModel.Draft.Metadata.ValidFrom);

        document.ValidFromText = "";

        Assert.Equal("", document.ValidFromText);
        Assert.Null(document.ValidFromError);
        Assert.Null(viewModel.Draft.Metadata.ValidFrom);
        Assert.False(viewModel.HasLocalInputErrors);
        AssertSaveGuardAgrees(viewModel);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void LocaleDependentDateText_IsRejectedInsteadOfUsingTheMachineTimeZone()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        document.ValidFromText = "01/02/2026 12:30";

        Assert.Equal("01/02/2026 12:30", document.ValidFromText);
        Assert.NotNull(document.ValidFromError);
        Assert.Null(viewModel.Draft.Metadata.ValidFrom);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("2026-08-29T12:34:56Z", true)]
    [InlineData("2026-08-29T12:34:56+09:00", true)]
    [InlineData("2026-08-29T12:34:56", false)]
    public void ValidityDate_RequiresExplicitRfc3339Offset(string text, bool expectedValid)
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        document.ValidFromText = text;

        Assert.Equal(expectedValid, document.ValidFromError is null);
        Assert.Equal(expectedValid, viewModel.Draft.Metadata.ValidFrom.HasValue);
        Assert.Equal(expectedValid, viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void SupportUrl_InitialEmptyBindingPreservesNullAndCleanState()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        Assert.Null(document.SupportUrl);
        Assert.False(viewModel.IsDirty);

        document.SupportUrl = "";

        Assert.Null(document.SupportUrl);
        Assert.False(viewModel.IsDirty);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", " ")]
    [InlineData("https://example.test/support", "https://example.test/support")]
    public void SupportUrl_PreservesAuthoredTextAndClearsOnlyEmpty(
        string authored,
        string? expected)
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        document.SupportUrl = authored;

        Assert.Equal(expected, document.SupportUrl);
        Assert.Equal(expected, viewModel.Draft.Metadata.SupportUrl);
    }

    [Fact]
    public async Task SupportUrl_WhitespaceReachesAuthoritativeValidationUnchanged()
    {
        var validation = new FakeValidationClient
        {
            NextOutcome = new PolicyEditorValidationOutcome(
                new PolicyValidationResult
                {
                    IsValid = false,
                    Findings =
                    [
                        new PolicyFinding
                        {
                            Severity = PolicyFindingSeverity.Error,
                            Code = PolicyFindingCode.InvalidFieldValue,
                            Path = "/Metadata/SupportUrl",
                            Message = "invalid URL",
                        },
                    ],
                }),
        };
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);
        document.SupportUrl = " ";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            " ",
            validation.LastDraft.GetProperty("Metadata").GetProperty("SupportUrl").GetString());
        Assert.Null(viewModel.Session.Validation);
        Assert.Equal(
            PolicyValidationSeverity.Error,
            Assert.Single(viewModel.Session.Findings.All).Severity);
    }

    [Fact]
    public void RefreshFromDraft_NotifiesEveryDocumentBoundProperty()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);
        var changed = new HashSet<string?>();
        document.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        viewModel.Session.SwitchToRaw();
        string submitted = viewModel.Session.RawBuffer;
        var canonical = PolicyEditorMapper.ToSharedDraft(viewModel.Draft);
        canonical = new PolicyDraftDocument
        {
            PolicyFormatVersion = PolicyFormatVersion.Parse("1.2.3"),
            Metadata = canonical.Metadata,
            Enforcement = canonical.Enforcement,
            Rules = canonical.Rules,
        };
        canonical.Metadata.Id = "replacement-id";
        canonical.Metadata.Publisher = "Fabrikam";
        canonical.Metadata.Description = "canonical description";
        canonical.Metadata.SupportUrl = "https://example.test/support";
        canonical.Metadata.ValidFrom = DateTimeOffset.Parse("2026-08-29T12:34:56Z");
        canonical.Metadata.ValidUntil = DateTimeOffset.Parse("2027-08-29T12:34:56Z");
        canonical.Enforcement.DefaultDecision = Devolutions.Now.Policy.Model.Decision.Allow;
        canonical.Enforcement.AuditMode = true;
        viewModel.Session.AcceptValidatedRaw(
            submitted,
            new PolicyValidationResult
            {
                IsValid = true,
                CanonicalDraft = canonical,
                ValidationReceipt = "receipt-refresh",
            });

        document.RefreshFromDraft();

        string[] expectedProperties =
        [
            nameof(PolicyEditorDocumentUi.Id),
            nameof(PolicyEditorDocumentUi.Publisher),
            nameof(PolicyEditorDocumentUi.PolicyFormatVersion),
            nameof(PolicyEditorDocumentUi.Description),
            nameof(PolicyEditorDocumentUi.HasDescription),
            nameof(PolicyEditorDocumentUi.SupportUrl),
            nameof(PolicyEditorDocumentUi.ValidFromText),
            nameof(PolicyEditorDocumentUi.ValidUntilText),
            nameof(PolicyEditorDocumentUi.ValidFromError),
            nameof(PolicyEditorDocumentUi.ValidUntilError),
            nameof(PolicyEditorDocumentUi.DecisionIndex),
            nameof(PolicyEditorDocumentUi.AuditModeIndex),
            nameof(PolicyEditorDocumentUi.IsIdentityLocked),
        ];
        Assert.All(expectedProperties, property => Assert.Contains(property, changed));
        Assert.Equal("replacement-id", document.Id);
        Assert.Equal("Fabrikam", document.Publisher);
        Assert.Equal("1.2.3", document.PolicyFormatVersion);
        Assert.Equal("canonical description", document.Description);
        Assert.Equal("https://example.test/support", document.SupportUrl);
        Assert.Equal(0, document.DecisionIndex);
        Assert.Equal(1, document.AuditModeIndex);
        Assert.True(document.IsAuditModeEnabled);
    }

    [Fact]
    public async Task AuditMode_NullDisplaysNoAndRoundTripsWithoutAddingAValue()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("policy-id", "Contoso"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient(),
            rawSyntaxDebounce: TimeSpan.Zero);
        var document = new PolicyEditorDocumentUi(viewModel);
        string original = PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft);

        Assert.Null(session.Draft.Enforcement.AuditMode);
        Assert.Equal(0, document.AuditModeIndex);
        Assert.False(document.IsAuditModeEnabled);
        Assert.DoesNotContain("\"AuditMode\"", original);

        viewModel.SwitchToRawCommand.Execute(null);
        await viewModel.SwitchToStructuredCommand.ExecuteAsync(null);

        Assert.Null(session.Draft.Enforcement.AuditMode);
        Assert.Equal(original, PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft));
    }

    [Fact]
    public void AuditMode_ExplicitChoicesSerializeFalseAndTrue()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("policy-id", "Contoso"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        document.AuditModeIndex = 0;
        Assert.False(session.Draft.Enforcement.AuditMode);
        Assert.Contains("\"AuditMode\": false", PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft));
        Assert.False(document.IsAuditModeEnabled);

        document.AuditModeIndex = 1;
        Assert.True(session.Draft.Enforcement.AuditMode);
        Assert.Contains("\"AuditMode\": true", PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft));
        Assert.True(document.IsAuditModeEnabled);
    }

    [Fact]
    public async Task SuccessfulInflightSave_PreservesNewerInvalidDateBufferAndError()
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient { Gate = new TaskCompletionSource() };
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            sessionViewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-date",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(sessionViewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "test-policy"),
                "saved-token"));

        Task pending = sessionViewModel.SaveCommand.ExecuteAsync(null);
        dialog.Document.ValidFromText = "2026-08-29T12:34:56";
        writer.Gate.SetResult();
        await pending;

        Assert.Equal("2026-08-29T12:34:56", dialog.Document.ValidFromText);
        Assert.NotNull(dialog.Document.ValidFromError);
        Assert.True(sessionViewModel.HasLocalInputErrors);
        Assert.True(sessionViewModel.IsDirty);
        Assert.True(sessionViewModel.SavedWithNewerChanges);
        Assert.Equal(PolicyEditorOperationKind.Update, sessionViewModel.Operation);
        Assert.Equal(dialog.Document.ValidFromError, dialog.Status.Message);
        Assert.NotEqual("The package broker policy was saved successfully.", dialog.Status.Message);
        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("Policy saved; newer changes remain", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            });
    }

    [Theory]
    [InlineData(PolicyWriteFailureKind.BrokerRejected, ErrorCode.Forbidden, false)]
    [InlineData(PolicyWriteFailureKind.WriteResultUnknown, null, false)]
    [InlineData(PolicyWriteFailureKind.BrokerRejected, ErrorCode.StalePolicyStoreToken, true)]
    public async Task InflightWriteOutcome_IsAnnouncedWhenNewerLocalErrorControlsVisualStatus(
        PolicyWriteFailureKind failureKind,
        ErrorCode? errorCode,
        bool isConflict)
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient { Gate = new TaskCompletionSource() };
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            sessionViewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-write",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(sessionViewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            failureKind,
            errorCode is { } code
                ? new ErrorResponse
                {
                    Code = code,
                    Management = isConflict
                        ? PolicyEditorTestFixtures.BuildActiveManagement(active, "token-2")
                        : null,
                }
                : null);

        Task pending = sessionViewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, writer.CallCount);
        dialog.Document.ValidFromText = "not-a-date";
        writer.Gate.SetResult();
        await pending;

        Assert.True(sessionViewModel.HasLocalInputErrors);
        Assert.Equal("Correct the highlighted fields", dialog.Status.Title);
        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains(
                    isConflict
                        ? "The policy changed since you started editing"
                        : "The policy could not be saved",
                    announcement.Message);
                Assert.Equal(
                    isConflict
                        ? AutomationLiveSetting.Polite
                        : AutomationLiveSetting.Assertive,
                    announcement.LiveSetting);
            });
    }

    [Fact]
    public async Task InflightCommittedWrite_IsAnnouncedWhenNewerRawSyntaxErrorControlsVisualStatus()
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient { Gate = new TaskCompletionSource() };
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(
                PolicyEditorTestFixtures.BuildDocument(id: "test-policy"),
                "token-1"));
        var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        sessionViewModel.SwitchToRawCommand.Execute(null);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            sessionViewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-write",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(sessionViewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "test-policy"),
                "token-2"));

        Task pending = sessionViewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, writer.CallCount);
        sessionViewModel.RawBuffer = "{";
        await sessionViewModel.WaitForRawSyntaxAnalysisAsync();
        writer.Gate.SetResult();
        await pending;

        Assert.NotNull(sessionViewModel.SyntaxError);
        Assert.Equal("The document is not valid JSON", dialog.Status.Title);
        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("Policy saved; newer changes remain", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            });
    }

    [Fact]
    public void RuleIdChange_NotifiesAutomationName()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule(
            PolicyRuleFactory.CreateBlank("old-id"));
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);
        var changed = new List<string?>();
        rule.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        rule.Id = "new-id";

        Assert.Contains(nameof(PolicyEditorRuleUi.Id), changed);
        Assert.Contains(nameof(PolicyEditorRuleUi.AutomationName), changed);
        Assert.Contains("new-id", rule.AutomationName);
    }

    [Fact]
    public void MultilineListEditing_PreservesWhitespaceAndTrimsOnlyEmptyLines()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        rule.SourceNames = " leading\r\n \r\n\r\ntrailing \n";

        Assert.Equal([" leading", " ", "trailing "], draftRule.Match.SourceNames);
        Assert.Equal(" leading\r\n \r\ntrailing ", rule.SourceNames);
    }

    [Fact]
    public void SourceNames_AppearOnlyForOneSourceCapableManagerButExistingValuesStayEditable()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        Assert.False(rule.CanUseSourceNames);
        Assert.False(rule.IsSourceNamesVisible);

        draftRule.Match.Managers.Add(Devolutions.Now.Policy.Model.ManagerName.Winget);
        viewModel.NotifyDraftChangedCommand.Execute(null);
        Assert.True(rule.CanUseSourceNames);
        Assert.True(rule.IsSourceNamesVisible);

        rule.SourceNames = "corporate";
        draftRule.Match.Managers.Clear();
        draftRule.Match.Managers.Add(Devolutions.Now.Policy.Model.ManagerName.Npm);
        viewModel.NotifyDraftChangedCommand.Execute(null);

        Assert.False(rule.CanUseSourceNames);
        Assert.True(rule.IsSourceNamesVisible);
        Assert.Contains(
            viewModel.Findings,
            finding => finding.Pointer == "/Rules/0/Match/Managers");
    }

    [Fact]
    public void ExclusiveConditionModes_RequireAValueAndExposeOnlySelectedMode()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        rule.PackageIdentifierModeIndex = (int)PackageIdentifierMode.Patterns;
        Assert.True(rule.IsPackageIdentifierPatternMode);
        Assert.False(rule.IsExactPackageIdentifierMode);
        Assert.Contains(
            viewModel.Findings,
            finding => finding.Pointer == "/Rules/0/Match/PackageIdentifiers/Patterns");

        rule.PackageIdentifierPatterns = "Contoso.*";
        Assert.DoesNotContain(
            viewModel.Findings,
            finding => finding.Pointer == "/Rules/0/Match/PackageIdentifiers/Patterns");

        rule.PackageVersionModeIndex = (int)PackageVersionMode.Exact;
        Assert.True(rule.IsExactVersionMode);
        Assert.False(rule.IsVersionRangeMode);
        rule.ExactVersions = "non-semantic-release";
        Assert.DoesNotContain(
            viewModel.Findings,
            finding => finding.Pointer == "/Rules/0/Match/Version/Exact");
    }

    [Fact]
    public void FieldSafetyAdvisory_AppearsAndClearsAtItsCausalControl()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        draftRule.Enabled = true;
        draftRule.Match.Operations.Add(ModelOperation.Install);
        draftRule.Match.SkipHashCheck = TriState.True;
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);
        rule.ApplyDecision(ModelDecision.Allow);
        rule.HasConstraints = true;

        rule.AllowSkipHashCheck = true;

        Assert.True(rule.HasSkipHashCheckAdvisory);
        Assert.Contains("integrity", rule.SkipHashCheckAdvisory);
        Assert.Equal(1, rule.FieldSafetyAdvisoryCount);
        Assert.True(rule.HasFieldSafetyAdvisories);
        Assert.Empty(rule.RuleSafetyAdvisories);

        rule.AllowSkipHashCheck = false;

        Assert.False(rule.HasSkipHashCheckAdvisory);
        Assert.Equal("", rule.SkipHashCheckAdvisory);
        Assert.Equal(0, rule.FieldSafetyAdvisoryCount);
        Assert.False(rule.HasFieldSafetyAdvisories);
    }

    [Fact]
    public async Task DenyToAllow_InitializesOnlyUntouchedHighImpactMatchDefaults()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        draftRule.Match.SkipHashCheck = TriState.True;
        draftRule.Match.Interactive = TriState.Omitted;
        draftRule.Match.PreRelease = TriState.Omitted;
        draftRule.Match.HasKillBeforeOperation = TriState.Omitted;
        draftRule.Match.HasUninstallPrevious = TriState.Omitted;
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Allow));

        Assert.Equal(ModelDecision.Allow, draftRule.Decision);
        Assert.Equal(TriState.True, draftRule.Match.SkipHashCheck);
        Assert.Equal(TriState.False, draftRule.Match.HasCustomParameters);
        Assert.Equal(TriState.False, draftRule.Match.HasCustomInstallLocation);
        Assert.Equal(TriState.False, draftRule.Match.HasPrePostCommands);
        Assert.Equal(TriState.Omitted, draftRule.Match.Interactive);
        Assert.Equal(TriState.Omitted, draftRule.Match.PreRelease);
        Assert.Equal(TriState.Omitted, draftRule.Match.HasKillBeforeOperation);
        Assert.Equal(TriState.Omitted, draftRule.Match.HasUninstallPrevious);

        draftRule.Match.SkipHashCheck = TriState.False;
        draftRule.Enabled = true;
        draftRule.Constraints = new PolicyEditorDraftConstraints
        {
            AllowSkipHashCheck = true,
        };
        Assert.Empty(rule.SkipHashCheckMatchAdvisory);
        Assert.Empty(rule.SkipHashCheckAdvisory);

        rule.SkipHashCheckIndex = PolicyEditorEnumDisplay.IndexOfTriState(TriState.Omitted);
        Assert.Contains(
            "Does not matter",
            rule.SkipHashCheckMatchAdvisory,
            StringComparison.Ordinal);

        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Deny));
        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Allow));

        Assert.Equal(TriState.Omitted, draftRule.Match.SkipHashCheck);
        Assert.Equal(TriState.False, draftRule.Match.HasCustomParameters);
    }

    [Fact]
    public void OptionalDescriptionAndReason_PreserveNullEmptyWhitespaceAndExplicitOmission()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.False(document.HasDescription);
        Assert.Null(document.Description);
        Assert.False(viewModel.IsDirty);
        document.Description = "";
        Assert.Null(document.Description);
        Assert.False(viewModel.IsDirty);

        PolicyEditorDraftRule draftRule = viewModel.Session.AddRule();
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        document.HasDescription = true;
        Assert.True(document.HasDescription);
        Assert.Equal("", document.Description);
        document.Description = " ";
        Assert.Equal(" ", document.Description);
        document.Description = "";
        Assert.Equal("", document.Description);
        document.HasDescription = false;
        Assert.Null(document.Description);

        Assert.False(rule.HasReason);
        Assert.Null(rule.Reason);
        rule.Reason = "";
        Assert.Null(rule.Reason);
        rule.HasReason = true;
        Assert.True(rule.HasReason);
        Assert.Equal("", rule.Reason);
        rule.Reason = " ";
        Assert.Equal(" ", rule.Reason);
        rule.Reason = "";
        Assert.Equal("", rule.Reason);
        rule.HasReason = false;
        Assert.Null(rule.Reason);
    }

    [Fact]
    public void VersionModeToggle_RefreshesDisplayedPropertiesAndPreservesRangeDraft()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        PolicyEditorDraftRule draftRule = session.AddRule();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);
        rule.ApplyDecision(Devolutions.Now.Policy.Model.Decision.Allow);
        var changed = new HashSet<string?>();
        rule.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        rule.PackageVersionModeIndex = (int)PackageVersionMode.Range;
        rule.MinVersion = "1.2.3";
        rule.MaxVersion = "2.0.0";
        rule.IncludePrerelease = true;
        changed.Clear();

        rule.PackageVersionModeIndex = (int)PackageVersionMode.Omitted;
        rule.PackageVersionModeIndex = (int)PackageVersionMode.Range;

        Assert.Equal("1.2.3", rule.MinVersion);
        Assert.Equal("2.0.0", rule.MaxVersion);
        Assert.True(rule.IncludePrerelease);
        Assert.Equal("1.2.3", draftRule.Match.VersionRange!.MinVersion);
        Assert.Equal("2.0.0", draftRule.Match.VersionRange.MaxVersion);
        Assert.True(draftRule.Match.VersionRange.IncludePrerelease);
        Assert.Contains(nameof(PolicyEditorRuleUi.MinVersion), changed);
        Assert.Contains(nameof(PolicyEditorRuleUi.MaxVersion), changed);
        Assert.Contains(nameof(PolicyEditorRuleUi.IncludePrerelease), changed);
    }

    [Fact]
    public void ConstraintsToggle_RefreshesDisplayedPropertiesToMatchDraft()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        PolicyEditorDraftRule draftRule = session.AddRule();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);
        rule.ApplyDecision(Devolutions.Now.Policy.Model.Decision.Allow);
        var changed = new HashSet<string?>();
        rule.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        rule.HasConstraints = true;
        rule.AllowSkipHashCheck = true;
        rule.AllowCustomParameters = true;
        rule.AllowedCustomParameters = "--silent";
        changed.Clear();

        rule.HasConstraints = false;
        rule.HasConstraints = true;

        Assert.False(rule.AllowSkipHashCheck);
        Assert.False(rule.AllowCustomParameters);
        Assert.Empty(rule.AllowedCustomParameters);
        Assert.False(draftRule.Constraints!.AllowSkipHashCheck);
        Assert.False(draftRule.Constraints.AllowCustomParameters);
        Assert.Empty(draftRule.Constraints.AllowedCustomParameters);
        Assert.Contains(nameof(PolicyEditorRuleUi.AllowSkipHashCheck), changed);
        Assert.Contains(nameof(PolicyEditorRuleUi.AllowCustomParameters), changed);
        Assert.Contains(nameof(PolicyEditorRuleUi.AllowedCustomParameters), changed);
    }

    [Fact]
    public async Task AllowToDeny_ConfiguredSafetyLimitsRequireAtomicConfirmation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        PolicyEditorDraftRule draftRule = session.AddRule();
        draftRule.Match.Operations.Add(ModelOperation.Install);
        draftRule.Decision = ModelDecision.Allow;
        draftRule.Constraints = new PolicyEditorDraftConstraints
        {
            AllowInteractive = false,
            AllowedCustomParameters = ["--silent"],
        };
        var prompt = new FakeConfirmationPrompt { NextResult = false };
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            prompt,
            new FakeWriteClient());
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Deny));

        Assert.Equal(ModelDecision.Allow, draftRule.Decision);
        Assert.NotNull(draftRule.Constraints);
        Assert.Equal(["--silent"], draftRule.Constraints.AllowedCustomParameters);
        Assert.Equal(PolicyEditorConfirmationKind.RemoveAllowSafetyLimits, prompt.LastRequest!.Kind);

        prompt.NextResult = true;
        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Deny));

        Assert.Equal(ModelDecision.Deny, draftRule.Decision);
        Assert.Null(draftRule.Constraints);
        Assert.DoesNotContain(
            "\"Constraints\"",
            PolicyEditorRawSyntax.ToCanonicalRaw(session.Draft));
    }

    [Fact]
    public async Task AllowToDeny_NoSafetyLimitsRequiresNoConfirmation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        PolicyEditorDraftRule draftRule = session.AddRule();
        draftRule.Match.Operations.Add(ModelOperation.Install);
        draftRule.Decision = ModelDecision.Allow;
        var prompt = new FakeConfirmationPrompt();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            prompt,
            new FakeWriteClient());
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        await viewModel.ChangeRuleDecisionAsync(
            rule,
            PolicyEditorEnumDisplay.IndexOfDecision(ModelDecision.Deny));

        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(ModelDecision.Deny, draftRule.Decision);
        Assert.Null(draftRule.Constraints);
    }

    [Fact]
    public async Task StructuredRawRoundTrip_PreservesExactOptionalAuthoredText()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("test-policy", "Contoso"));
        PolicyEditorDraftRule draftRule = session.AddRule();
        var validation = new FakeValidationClient();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            new FakeWriteClient(),
            rawSyntaxDebounce: TimeSpan.Zero);
        var document = new PolicyEditorDocumentUi(viewModel);
        using var rule = new PolicyEditorRuleUi(draftRule, viewModel);

        document.HasDescription = true;
        document.Description = "";
        rule.HasReason = true;
        rule.Reason = " ";
        rule.PackageVersionModeIndex = (int)PackageVersionMode.Range;
        rule.MinVersion = "1.0.0";
        rule.MaxVersion = "2.0.0";
        rule.MinVersion = "";
        rule.MaxVersion = "";
        Assert.Null(draftRule.Match.VersionRange!.MinVersion);
        Assert.Null(draftRule.Match.VersionRange.MaxVersion);
        rule.MinVersion = "1.0.0";
        rule.MaxVersion = "2.0.0";

        viewModel.SwitchToRawCommand.Execute(null);
        Assert.True(session.TryParseRaw(
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        Assert.Null(error);
        Assert.Equal("", parsed!.Metadata.Description);
        PolicyEditorDraftRule parsedRule = parsed.Rules.Single(
            candidate => candidate.Id == draftRule.Id);
        Assert.Equal(" ", parsedRule.Reason);
        Assert.Equal("1.0.0", parsedRule.Match.VersionRange!.MinVersion);
        Assert.Equal("2.0.0", parsedRule.Match.VersionRange!.MaxVersion);

        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-text-round-trip",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(parsed),
        });
        await viewModel.SwitchToStructuredCommand.ExecuteAsync(null);

        Assert.Equal(PolicyEditorMode.Structured, session.Mode);
        Assert.Equal("", session.Draft.Metadata.Description);
        PolicyEditorDraftRule projectedRule = session.Draft.Rules.Single(
            candidate => candidate.Id == draftRule.Id);
        Assert.Equal(" ", projectedRule.Reason);
        Assert.Equal("1.0.0", projectedRule.Match.VersionRange!.MinVersion);
        Assert.Equal("2.0.0", projectedRule.Match.VersionRange!.MaxVersion);
    }

    [Fact]
    public async Task ConflictStatus_PrecedesGenericWriteFailureAndShowsOverwriteGuidance()
    {
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-stale",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse
            {
                Code = ErrorCode.StalePolicyStoreToken,
                Management = PolicyEditorTestFixtures.BuildActiveManagement(active, "token-2"),
            });

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasConflict);
        Assert.Equal(PolicyWriteFailureKind.BrokerRejected, viewModel.LastWriteFailureKind);
        Assert.Equal("The policy changed since you started editing", dialog.Status.Title);
        Assert.Equal(
            "Review your changes, then choose Overwrite to save anyway.",
            dialog.Status.Message);
        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("The policy changed since you started editing", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            });
    }

    [Fact]
    public async Task GenericWriteFailureWithoutConflictRetainsFailureStatus()
    {
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-rejected",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            PolicyWriteFailureKind.BrokerRejected,
            new ErrorResponse { Code = ErrorCode.InvalidPolicy });

        await viewModel.SaveCommand.ExecuteAsync(null);
        using var dialog = new PolicyEditorDialogViewModel(viewModel);

        Assert.False(viewModel.HasConflict);
        Assert.Equal("The policy could not be saved", dialog.Status.Title);
        Assert.NotEqual(
            "Review your changes, then choose Overwrite to save anyway.",
            dialog.Status.Message);
    }

    [Theory]
    [InlineData(PolicyWriteFailureKind.BrokerRejected, ErrorCode.Forbidden)]
    [InlineData(PolicyWriteFailureKind.WriteResultUnknown, null)]
    public async Task CompletedWriteErrors_AreAnnouncedAssertively(
        PolicyWriteFailureKind failureKind,
        ErrorCode? errorCode)
    {
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-rejected",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Failure(
            failureKind,
            errorCode is { } code ? new ErrorResponse { Code = code } : null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("The policy could not be saved", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Assertive, announcement.LiveSetting);
            });
    }

    [Theory]
    [InlineData("BrokerUnavailable", "unavailable")]
    [InlineData("Timeout", "timed out")]
    [InlineData("EmptyResponse", "without a response")]
    [InlineData("InvalidResponse", "invalid policy response")]
    [InlineData("PostCommitRefreshTimeout", "saved, but refreshing")]
    [InlineData("PostCommitRefreshUnavailable", "saved, but the current policy state")]
    public void UnknownWriteResult_ExplainsStructuredCauseAndRefreshAction(
        string diagnosticCode,
        string expectedCause)
    {
        string message = PolicyEditorDialogViewModel.DescribeWriteFailure(
            PolicyWriteFailureKind.WriteResultUnknown,
            null,
            diagnosticCode);

        Assert.Contains(expectedCause, message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Refresh policy management state", message);
    }

    [Fact]
    public void UnknownWriteResult_WithoutDiagnosticDoesNotClaimAuthenticationFailure()
    {
        string message = PolicyEditorDialogViewModel.DescribeWriteFailure(
            PolicyWriteFailureKind.WriteResultUnknown,
            null);

        Assert.Contains("could not be confirmed", message);
        Assert.DoesNotContain("authenticate", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompletedSaveSuccess_IsAnnouncedPolitely()
    {
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-saved",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "test-policy"),
                "saved-token"));

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("Policy saved", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            });
    }

    [Fact]
    public async Task ValidationError_IsAnnouncedAssertivelyWithoutDuplicateSummary()
    {
        var validation = new FakeValidationClient();
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = false,
            Findings =
            [
                new PolicyFinding
                {
                    Severity = PolicyFindingSeverity.Error,
                    Code = PolicyFindingCode.InvalidFieldValue,
                    Path = "/Metadata/SupportUrl",
                    Message = "invalid URL",
                },
            ],
        });

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("A policy field has an invalid value", announcement.Message);
                Assert.Contains("invalid URL", announcement.Message);
                Assert.Contains("Metadata", announcement.Message);
                Assert.Contains("Support URL", announcement.Message);
                Assert.Contains("/Metadata/SupportUrl", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Assertive, announcement.LiveSetting);
            });
        Assert.Equal("Validation found errors", dialog.Status.Title);
    }

    [Fact]
    public async Task OrdinaryBusyStatus_IsAnnouncedOncePolitely()
    {
        var validation = new FakeValidationClient
        {
            Gate = new TaskCompletionSource(),
        };
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));

        Task pending = viewModel.SaveCommand.ExecuteAsync(null);

        (string? message, AutomationLiveSetting liveSetting) = Assert.Single(announcements);
        Assert.Contains("Working", message);
        Assert.Equal(AutomationLiveSetting.Polite, liveSetting);

        validation.Gate.TrySetResult();
        await pending;

        Assert.Single(announcements);
    }

    [Theory]
    [InlineData(WriteAnnouncementScenario.Saved)]
    [InlineData(WriteAnnouncementScenario.Rejected)]
    [InlineData(WriteAnnouncementScenario.Conflict)]
    public async Task CompletedWriteOutcome_IsReplacedByNextSaveOutcome(
        WriteAnnouncementScenario scenario)
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        validation.NextOutcome = ValidOutcome(viewModel, "receipt-write");
        writer.NextOutcome = CreateWriteOutcome(scenario, active);

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(2, announcements.Count);
        announcements.Clear();
        validation.NextOutcome = ValidOutcome(viewModel, "receipt-validation");

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, announcements.Count);
        Assert.Contains("Working", announcements[0].Message);
        Assert.Equal(AutomationLiveSetting.Polite, announcements[0].LiveSetting);
    }

    [Fact]
    public async Task ExistingWriteCompletion_IsSilentOnConstruction_AndNewCompletionAnnouncesOnce()
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient();
        PolicyDocument active = PolicyEditorTestFixtures.BuildDocument(id: "test-policy");
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement(active, "token-1"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        validation.NextOutcome = ValidOutcome(viewModel, "receipt-first");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(active, "token-2"));
        await viewModel.SaveCommand.ExecuteAsync(null);

        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        Assert.Empty(announcements);

        validation.NextOutcome = ValidOutcome(viewModel, "receipt-second");
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(active, "token-3"));
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Collection(
            announcements,
            announcement =>
            {
                Assert.Contains("Working", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            },
            announcement =>
            {
                Assert.Contains("Policy saved", announcement.Message);
                Assert.Equal(AutomationLiveSetting.Polite, announcement.LiveSetting);
            });
    }

    [Fact]
    public async Task RawSyntaxError_DoesNotDuplicateDetailedLiveRegionAnnouncement()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        viewModel.SwitchToRawCommand.Execute(null);

        viewModel.RawBuffer = "{";
        await viewModel.WaitForRawSyntaxAnalysisAsync();

        Assert.NotNull(viewModel.SyntaxError);
        Assert.Equal("The document is not valid JSON", dialog.Status.Title);
        Assert.Empty(announcements);
    }

    [Fact]
    public async Task RawFindingNavigationIsDisabledWhileSyntaxAnalysisIsPending()
    {
        using PolicyEditorSessionViewModel viewModel = CreateViewModel();
        using var dialog = new PolicyEditorDialogViewModel(
            viewModel,
            (_, _) => { });
        int navigationRequests = 0;
        dialog.FindingNavigationRequested += (_, _) => navigationRequests++;
        viewModel.SwitchToRawCommand.Execute(null);

        viewModel.RawBuffer = "{";

        Assert.True(viewModel.IsRawSyntaxPending);
        Assert.False(dialog.CanNavigateFinding);
        dialog.NavigateToSelectedFinding();
        Assert.Equal(0, navigationRequests);

        await viewModel.WaitForRawSyntaxAnalysisAsync();
        Assert.True(dialog.CanNavigateFinding);
    }

    [Fact]
    public async Task SaveCommand_IsDisabledWhileBusy_IndependentlyOfTypedInputErrors()
    {
        var validation = new FakeValidationClient { Gate = new TaskCompletionSource() };
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);

        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Task validateTask = viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanValidateOrSave);
        AssertSaveGuardAgrees(viewModel);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        validation.Gate.TrySetResult();
        await validateTask;

        Assert.False(viewModel.IsBusy);
        AssertSaveGuardAgrees(viewModel);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeDuringValidation_IgnoresLateCompletion()
    {
        var validation = new FakeValidationClient
        {
            Gate = new TaskCompletionSource(),
        };
        using PolicyEditorSessionViewModel viewModel = CreateViewModel(validation);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-late",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });

        Task pending = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);
        Assert.False(await viewModel.ConfirmDiscardAsync());

        viewModel.Dispose();
        validation.Gate.TrySetResult();
        await pending;

        Assert.Null(viewModel.Session.Validation);
    }

    [Fact]
    public async Task DisposeDuringWrite_IgnoresLateSuccessfulResponse()
    {
        var validation = new FakeValidationClient();
        var writer = new FakeWriteClient
        {
            Gate = new TaskCompletionSource(),
        };
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("test-policy", "Contoso");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        var viewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            writer);
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = "receipt-write",
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });
        writer.NextOutcome = PolicyWriteOutcome.Success(
            PolicyEditorTestFixtures.BuildReplacementResponse(
                PolicyEditorTestFixtures.BuildDocument(id: "test-policy"),
                "late-token"));

        Task pending = viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsBusy);
        Assert.False(await viewModel.ConfirmDiscardAsync());

        viewModel.Dispose();
        writer.Gate.TrySetResult();
        await pending;

        Assert.False(viewModel.LastSaveSucceeded);
        Assert.Equal(PolicyWriteFailureKind.None, viewModel.LastWriteFailureKind);
        Assert.Equal("token-missing", viewModel.Session.OriginManagement.StoreToken);
    }

    /// <summary>
    /// The Save button's <c>IsEnabled</c> binding and <c>SaveCommand</c>'s own <c>CanExecute</c> gate must
    /// never disagree: both derive from <c>CanValidateOrSave</c>/<c>CanStartRemoteOperation</c> so a
    /// visually-enabled Save button can never silently no-op, and a disabled one is never bypassable via a
    /// keyboard shortcut bound directly to the command.
    /// </summary>
    private static void AssertSaveGuardAgrees(PolicyEditorSessionViewModel viewModel) =>
        Assert.Equal(viewModel.CanValidateOrSave, viewModel.SaveCommand.CanExecute(null));

    private static PolicyEditorValidationOutcome ValidOutcome(
        PolicyEditorSessionViewModel viewModel,
        string receipt) =>
        new(new PolicyValidationResult
        {
            IsValid = true,
            ValidationReceipt = receipt,
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(viewModel.Draft),
        });

    private static PolicyWriteOutcome CreateWriteOutcome(
        WriteAnnouncementScenario scenario,
        PolicyDocument active) =>
        scenario switch
        {
            WriteAnnouncementScenario.Saved => PolicyWriteOutcome.Success(
                PolicyEditorTestFixtures.BuildReplacementResponse(active, "token-2")),
            WriteAnnouncementScenario.Rejected => PolicyWriteOutcome.Failure(
                PolicyWriteFailureKind.BrokerRejected,
                new ErrorResponse { Code = ErrorCode.Forbidden }),
            WriteAnnouncementScenario.Conflict => PolicyWriteOutcome.Failure(
                PolicyWriteFailureKind.BrokerRejected,
                new ErrorResponse
                {
                    Code = ErrorCode.StalePolicyStoreToken,
                    Management = PolicyEditorTestFixtures.BuildActiveManagement(
                        active,
                        "token-conflict"),
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

    private static PolicyEditorSessionViewModel CreateViewModel(FakeValidationClient? validation = null)
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("test-policy", "Contoso");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        return new(
            session,
            validation ?? new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
    }

    public enum WriteAnnouncementScenario
    {
        Saved,
        Rejected,
        Conflict,
    }
}
