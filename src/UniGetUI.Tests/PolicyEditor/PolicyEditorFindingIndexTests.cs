using System.Text.Json;
using Avalonia.Automation;
using Devolutions.Now.Policy.Api;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using UniGetUI.PackageEngine.AgentBroker.PolicyManagement;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorFindingIndexTests
{
    [Fact]
    public async Task FixedFindingSummary_SelectsFirstErrorAndCyclesInSourceOrder()
    {
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement());
        var validation = new FakeValidationClient();
        using var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var announcements = new List<(string? Message, AutomationLiveSetting LiveSetting)>();
        using var dialog = new PolicyEditorDialogViewModel(
            sessionViewModel,
            (message, liveSetting) => announcements.Add((message, liveSetting)));
        PolicyValidationFinding? navigated = null;
        dialog.FindingNavigationRequested += (_, finding) => navigated = finding;
        validation.NextOutcome = new PolicyEditorValidationOutcome(new PolicyValidationResult
        {
            IsValid = false,
            Findings =
            [
                new PolicyFinding
                {
                    Path = "/Metadata/Publisher",
                    Severity = PolicyFindingSeverity.Warning,
                    Message = "review publisher",
                },
                new PolicyFinding
                {
                    Path = "/Metadata/Id",
                    Severity = PolicyFindingSeverity.Error,
                    Message = "correct id",
                },
            ],
        });

        await sessionViewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(dialog.HasFindingSummary);
        Assert.Equal("/Metadata/Id", dialog.SelectedFinding!.Pointer);
        Assert.Equal("/Metadata/Id", navigated!.Pointer);
        Assert.Equal("1 error(s)", dialog.FindingCountText);
        Assert.False(dialog.HasMultipleFindings);
        Assert.Equal("Validation found errors", dialog.Status.Title);
        Assert.Equal("Correct the selected error before saving.", dialog.Status.Message);
        Assert.Contains(announcements, item =>
            item.LiveSetting == AutomationLiveSetting.Assertive);
    }

    [Fact]
    public async Task AuthoritativeWarningAppearsInlineWithoutSaveConfirmation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement());
        var validation = new FakeValidationClient
        {
            NextOutcome = new PolicyEditorValidationOutcome(
                new PolicyValidationResult
                {
                    IsValid = true,
                    CanonicalDraft = PolicyEditorMapper.ToSharedDraft(session.Draft),
                    ValidationReceipt = "receipt",
                    Findings =
                    [
                        new PolicyFinding
                        {
                            Path = "/Rules/0/Match/SkipHashCheck",
                            RuleId = "allow-tools",
                            Severity = PolicyFindingSeverity.Warning,
                            Code = PolicyFindingCode.SensitiveOptionAllowed,
                            Message = "warning",
                            Arguments = new Dictionary<string, JsonElement>
                            {
                                ["option"] = JsonSerializer.SerializeToElement(
                                    "SkipHashCheck"),
                            },
                        },
                    ],
                }),
        };
        var prompt = new FakeConfirmationPrompt { NextResult = false };
        using var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            validation,
            prompt,
            new FakeWriteClient());
        using var dialog = new PolicyEditorDialogViewModel(sessionViewModel, (_, _) => { });

        sessionViewModel.NotifyDraftChangedCommand.Execute(null);
        await sessionViewModel.WaitForAuthoritativeValidationAsync();

        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(
            "/Rules/0/Match/SkipHashCheck",
            Assert.Single(sessionViewModel.Findings).NavigationPointer);
        Assert.False(dialog.HasFindingSummary);
        Assert.False(dialog.Status.IsOpen);

        await sessionViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, prompt.CallCount);
        Assert.Equal(
            "/Rules/0/Match/SkipHashCheck",
            Assert.Single(sessionViewModel.Findings).NavigationPointer);
        Assert.False(dialog.HasFindingSummary);
    }

    [Fact]
    public void LiveLocalFindingRefreshDoesNotRequestFocusNavigation()
    {
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement());
        PolicyEditorDraftRule rule = session.AddRule(
            PolicyRuleFactory.CreateBlank("invalid rule id"));
        rule.Match.Operations.Add(Devolutions.Now.Policy.Model.Operation.Install);
        using var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var dialog = new PolicyEditorDialogViewModel(sessionViewModel, (_, _) => { });
        PolicyValidationFinding? navigated = null;
        dialog.FindingNavigationRequested += (_, finding) => navigated = finding;

        dialog.Document.Publisher = "Updated publisher";

        Assert.Contains(sessionViewModel.Findings, finding =>
            finding.Pointer == "/Rules/0/Id");
        Assert.Null(navigated);
    }

    private static PolicyValidationFinding Finding(string pointer, string? ruleId, string message = "message") =>
        new(pointer, ruleId, PolicyValidationSeverity.Warning, message);

    [Fact]
    public void Build_Empty_ProducesEmptyLookupsNotThrowing()
    {
        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([]);

        Assert.Empty(index.All);
        Assert.Empty(index.ForPointer("/anything"));
        Assert.Empty(index.ForRule("any-rule"));
    }

    [Fact]
    public void ForPointer_ReturnsAllFindingsAtThatExactPointer()
    {
        PolicyValidationFinding a = Finding("/rules/0/priority", "rule-a", "priority too low");
        PolicyValidationFinding b = Finding("/rules/0/priority", "rule-a", "priority collides");
        PolicyValidationFinding c = Finding("/rules/1/priority", "rule-b");

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b], index.ForPointer("/rules/0/priority"));
        Assert.Equal([c], index.ForPointer("/rules/1/priority"));
        Assert.Empty(index.ForPointer("/rules/2/priority"));
    }

    [Fact]
    public void ForRule_ReturnsAllFindingsForThatRuleId_RegardlessOfPointer()
    {
        PolicyValidationFinding a = Finding("/rules/0/priority", "rule-a");
        PolicyValidationFinding b = Finding("/rules/0/reason", "rule-a");
        PolicyValidationFinding c = Finding("/rules/1/priority", "rule-b");

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b], index.ForRule("rule-a"));
        Assert.Equal([c], index.ForRule("rule-b"));
    }

    [Fact]
    public void ForRule_FindingsWithoutRuleId_AreExcludedFromRuleLookupButPresentInAll()
    {
        PolicyValidationFinding documentLevel = Finding("/enforcement/defaultDecision", null);

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([documentLevel]);

        Assert.Contains(documentLevel, index.All);
        Assert.Empty(index.ForRule("rule-a"));
        Assert.Equal([documentLevel], index.ForPointer("/enforcement/defaultDecision"));
    }

    [Fact]
    public void All_PreservesInputOrder()
    {
        PolicyValidationFinding a = Finding("/a", null);
        PolicyValidationFinding b = Finding("/b", null);
        PolicyValidationFinding c = Finding("/c", null);

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build([a, b, c]);

        Assert.Equal([a, b, c], index.All);
    }

    [Fact]
    public void RecognizedFinding_UsesLocalizedCodeInsteadOfAgentMessage()
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.DefaultAllow,
            Path = "/Enforcement/DefaultDecision",
            Message = "server-controlled English must not be displayed",
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(
            "The default decision is Allow; requests matching no rule are permitted.",
            finding.Message);
        Assert.DoesNotContain("server-controlled", finding.Message);
    }

    [Theory]
    [InlineData(
        PolicyFindingCode.InvalidFieldValue,
        "/Rules/0/Priority",
        "Priority exceeds 2147483647",
        "A policy field has an invalid value.",
        "Priority exceeds 2147483647")]
    [InlineData(
        PolicyFindingCode.InvalidFieldType,
        "/Rules/0/Match/PackageIdentifiers/Exact",
        "Exact package identifiers must be an array of strings",
        "A policy field has the wrong value type.",
        "Exact package identifiers must be an array of strings")]
    [InlineData(
        PolicyFindingCode.MissingRequiredField,
        "/Metadata/Publisher",
        "Publisher is required",
        "The policy draft is missing a required field.",
        "Publisher is required")]
    [InlineData(
        PolicyFindingCode.UnknownField,
        "/Rules/0/Match/UnsupportedPackageCondition",
        "Unsupported field 'UnsupportedPackageCondition'",
        "The policy draft contains an unknown field.",
        "Unsupported field 'UnsupportedPackageCondition'")]
    [InlineData(
        PolicyFindingCode.InvalidFieldValue,
        "/Rules/0/Match/Version/Exact/0",
        "Exact version entries cannot be empty",
        "A policy field has an invalid value.",
        "cannot be empty")]
    [InlineData(
        PolicyFindingCode.InvalidValidityInterval,
        "/Metadata/ValidUntil",
        "ValidUntil must be later than ValidFrom",
        "The policy validity interval is invalid.",
        "ValidUntil must be later than ValidFrom")]
    [InlineData(
        PolicyFindingCode.UnsupportedPolicyFormatVersion,
        "/PolicyFormatVersion",
        "PolicyFormatVersion 2.0.0 is not supported",
        "The policy format version is unsupported.",
        "2.0.0")]
    public void GenericFindings_PreserveLocalizedSummaryAndSanitizedSpecificDetail(
        PolicyFindingCode code,
        string pointer,
        string agentMessage,
        string expectedSummary,
        string expectedDetail)
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Error,
            Code = code,
            Path = pointer,
            RuleId = pointer.StartsWith("/Rules/", StringComparison.Ordinal) ? "rule-a" : null,
            Message = agentMessage,
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Contains(expectedSummary, finding.Message);
        Assert.Contains(expectedDetail, finding.Message);
        Assert.DoesNotContain(finding.Message, char.IsControl);
    }

    [Theory]
    [InlineData("/PolicyFormatVersion", null, "Policy format version")]
    [InlineData("/Metadata/ValidUntil", null, "Metadata · Valid until")]
    [InlineData("/Rules/0/Priority", "install-tools", "Rule: 'install-tools' · Priority")]
    [InlineData("/Rules/1/Match/Versions/2", null, "Rule: 2 · Match criteria · Versions · Item 3")]
    [InlineData("", null, "Policy document")]
    public void FriendlyLocation_TranslatesJsonPointerWithoutDiscardingRawPointer(
        string pointer,
        string? ruleId,
        string expectedLocation)
    {
        PolicyValidationFinding finding = Finding(pointer, ruleId);

        Assert.Equal(expectedLocation, finding.FriendlyLocation);
        if (pointer.Length > 0)
        {
            Assert.Contains(pointer, finding.AutomationName);
            Assert.True(finding.HasRawPointer);
        }
        else
        {
            Assert.False(finding.HasRawPointer);
        }
    }

    [Theory]
    [InlineData(PolicyFindingSeverity.Error, PolicyValidationSeverity.Error)]
    [InlineData(PolicyFindingSeverity.Warning, PolicyValidationSeverity.Warning)]
    public void SharedAndSanitizedFindings_MapContractSeverityExplicitly(
        PolicyFindingSeverity sharedSeverity,
        PolicyValidationSeverity expectedSeverity)
    {
        var shared = new PolicyFinding
        {
            Severity = sharedSeverity,
            Code = PolicyFindingCode.InvalidFieldValue,
            Path = "/Metadata/Description",
            Message = "finding",
        };
        var sanitized = new BrokerPolicySanitizedFinding(
            sharedSeverity,
            PolicyFindingCode.InvalidFieldValue,
            "/Metadata/Description",
            null,
            "finding",
            new Dictionary<string, string>(),
            false,
            false,
            false,
            false);

        Assert.Equal(expectedSeverity, PolicyValidationFinding.FromShared(shared).Severity);
        Assert.Equal(expectedSeverity, PolicyValidationFinding.FromSanitized(sanitized).Severity);
    }

    [Fact]
    public void UndefinedContractSeverity_FailsClosedInsteadOfBecomingWarning()
    {
        const PolicyFindingSeverity undefined = (PolicyFindingSeverity)int.MaxValue;
        var shared = new PolicyFinding
        {
            Severity = undefined,
            Code = PolicyFindingCode.InvalidFieldValue,
            Message = "finding",
        };
        var sanitized = new BrokerPolicySanitizedFinding(
            undefined,
            PolicyFindingCode.InvalidFieldValue,
            null,
            null,
            "finding",
            new Dictionary<string, string>(),
            false,
            false,
            false,
            false);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolicyValidationFinding.FromShared(shared));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PolicyValidationFinding.FromSanitized(sanitized));
    }

    [Fact]
    public void LocalInfoFindingDoesNotCreateSaveAcknowledgementState()
    {
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement());
        string raw = session.GetEffectiveRawJson();
        var info = new PolicyValidationFinding(
            "/Metadata/Description",
            null,
            PolicyValidationSeverity.Info,
            "information");
        var validation = new PolicyValidationResult
        {
            IsValid = true,
            CanonicalDraft = PolicyEditorMapper.ToSharedDraft(session.Draft),
            ValidationReceipt = "receipt-info",
            Findings = [],
        };

        session.ApplyValidationResult(raw, validation, [info]);

        Assert.NotNull(session.Validation);
        Assert.Equal(
            PolicyValidationSeverity.Info,
            Assert.Single(session.Findings.All).Severity);
    }

    [Theory]
    [InlineData(PolicyValidationSeverity.Error, true, false)]
    [InlineData(PolicyValidationSeverity.Warning, false, true)]
    [InlineData(PolicyValidationSeverity.Info, false, false)]
    public void FindingSeverity_ExposesThemeClassSelectors(
        PolicyValidationSeverity severity,
        bool isError,
        bool isWarning)
    {
        var finding = new PolicyValidationFinding("", null, severity, "message");

        Assert.Equal(isError, finding.IsError);
        Assert.Equal(isWarning, finding.IsWarning);
    }

    [Fact]
    public void SensitiveFinding_UsesStructuredOptionAndRestrictionArguments()
    {
        using JsonDocument option = JsonDocument.Parse("\"AllowCustomParameters\"");
        using JsonDocument restrictions = JsonDocument.Parse("[\"--silent\"]");
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = "/Rules/0/Constraints/AllowCustomParameters",
            RuleId = "allow-tools",
            Message = "untrusted fallback",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["option"] = option.RootElement.Clone(),
                ["allowedCustomParameters"] = restrictions.RootElement.Clone(),
            },
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Contains("Rule “allow-tools”", finding.Message);
        Assert.Contains("custom parameters", finding.Message);
        Assert.Contains("--silent", finding.Message);
        Assert.DoesNotContain("untrusted fallback", finding.Message);
        Assert.Equal(
            "/Rules/0/Constraints/AllowedCustomParameters",
            finding.NavigationPointer);
        Assert.Equal(
            "/Rules/0/Constraints/AllowCustomParameters",
            finding.RawNavigationPointer);
    }

    [Theory]
    [InlineData("SkipHashCheck", "/Rules/0/Constraints/AllowSkipHashCheck", "skipping hash verification")]
    [InlineData("PreRelease", "/Rules/0/Constraints/AllowPreRelease", "prerelease packages")]
    [InlineData("AllowCustomParameters", "/Rules/0/Constraints/AllowCustomParameters", "without an allowlist")]
    [InlineData("AllowCustomInstallLocation", "/Rules/0/Constraints/AllowCustomInstallLocation", "without approved paths")]
    [InlineData("AllowPrePostCommands", "/Rules/0/Constraints/AllowPrePostCommands", "pre/post commands")]
    [InlineData("AllowKillBeforeOperation", "/Rules/0/Constraints/AllowKillBeforeOperation", "stopping running applications")]
    [InlineData("AllowUninstallPrevious", "/Rules/0/Constraints/AllowUninstallPrevious", "uninstalling the previous version")]
    public void SensitiveFinding_MapsOfficialOptionToSpecificMessageAndControl(
        string optionValue,
        string expectedNavigationPointer,
        string expectedMessage)
    {
        using JsonDocument option = JsonDocument.Parse(
            JsonSerializer.Serialize(optionValue));
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = "/Rules/0",
            RuleId = "allow-winget-updates",
            Message = "An enabled Allow rule permits a sensitive option.",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["option"] = option.RootElement.Clone(),
            },
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Contains("Rule “allow-winget-updates”", finding.Message);
        Assert.Contains(expectedMessage, finding.Message);
        Assert.Equal(expectedNavigationPointer, finding.NavigationPointer);
        Assert.Equal(expectedNavigationPointer, finding.RawNavigationPointer);
        Assert.Contains("Additional safety limits", finding.FriendlyLocation);
        Assert.DoesNotContain("An enabled Allow rule permits a sensitive option.", finding.Message);
    }

    [Fact]
    public void OfficialSensitiveOptionFixture_PreservesAgentPointerAndTargetsSafetyControl()
    {
        const string json =
            """
            {
              "ResponseKind": "PolicyValidationResponse",
              "ResponseVersion": "1.0",
              "Server": {
                "ServerVersion": "2026.8.0",
                "Transport": "HttpNamedPipe"
              },
              "Validation": {
                "ResultVersion": "1.0",
                "ValidatorVersion": "gateway-policy-validator/1",
                "IsValid": true,
                "CanonicalDraft": {
                  "PolicyFormatVersion": "1.0.0",
                  "Metadata": {
                    "Id": "contoso.package-policy",
                    "Publisher": "Contoso IT"
                  },
                  "Enforcement": {
                    "DefaultDecision": "Allow",
                    "AuditMode": true
                  },
                  "Rules": [{
                    "Id": "allow.vscode.skip-hash",
                    "Enabled": true,
                    "Priority": 100,
                    "Decision": "Allow",
                    "Match": {
                      "Managers": ["Winget"],
                      "PackageIdentifiers": {
                        "Exact": ["Microsoft.VisualStudioCode"]
                      },
                      "SkipHashCheck": true
                    }
                  }]
                },
                "ValidationReceipt": "receipt:sha256:valid-warning-set",
                "Findings": [{
                  "FindingVersion": "1.0",
                  "Severity": "Warning",
                  "Code": "SensitiveOptionAllowed",
                  "Path": "/Rules/0/Match/SkipHashCheck",
                  "RuleId": "allow.vscode.skip-hash",
                  "Arguments": { "option": "SkipHashCheck" },
                  "Message": "This allow rule permits an individually identified sensitive option."
                }]
              }
            }
            """;
        PolicyValidationResponse response =
            BrokerSerializer.DeserializeStrict<PolicyValidationResponse>(json)
            ?? throw new InvalidOperationException("Official validation fixture did not deserialize.");
        PolicyFinding shared = Assert.Single(response.Validation.Findings);

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal("/Rules/0/Match/SkipHashCheck", finding.Pointer);
        Assert.Equal("/Rules/0/Match/SkipHashCheck", finding.RawNavigationPointer);
        Assert.Equal(
            "/Rules/0/Match/SkipHashCheck",
            finding.NavigationPointer);
        Assert.Contains("Does not matter", finding.Message);
        Assert.Contains("Set it to No", finding.Message);
    }

    [Fact]
    public void SensitiveFinding_MissingOptionKeepsCoarseActionableFallback()
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = "/Rules/1",
            RuleId = "allow-tools",
            Message = "Review the sensitive behavior.",
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal("/Rules/1", finding.NavigationPointer);
        Assert.Contains("Rule “allow-tools”", finding.Message);
        Assert.Contains("Review the sensitive behavior.", finding.Message);
        Assert.Contains(finding.FriendlyLocation, finding.ConfirmationMessage);
    }

    [Fact]
    public void AgentControlledFindingLocations_AreControlFreeAndBounded()
    {
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Error,
            Code = PolicyFindingCode.InvalidFieldValue,
            Path = "/" + new string('p', 3000) + "\r\nhidden",
            RuleId = new string('r', 3000) + "\0hidden",
            Message = "ignored",
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(2048, finding.Pointer.Length);
        Assert.Equal(2048, finding.RuleId!.Length);
        Assert.DoesNotContain('\r', finding.Pointer);
        Assert.DoesNotContain('\n', finding.Pointer);
        Assert.DoesNotContain('\0', finding.RuleId);
    }

    [Fact]
    public void GenericFallbackDetail_IsControlFreeBoundedAndAnnounceable()
    {
        string malicious = "Priority\u0007 exceeds 2147483647\r\n"
            + new string('x', 5000);
        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Error,
            Code = PolicyFindingCode.InvalidFieldValue,
            Path = "/Rules/0/Priority\u0000hidden",
            RuleId = "rule-a",
            Message = malicious,
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.True(finding.Message.EnumerateRunes().Count() <= 2048);
        Assert.DoesNotContain(finding.Message, char.IsControl);
        Assert.DoesNotContain(finding.AutomationName, char.IsControl);
        Assert.Contains("Priority exceeds 2147483647", finding.Message);
        Assert.Contains("JSON pointer", finding.AutomationName);
    }

    [Fact]
    public void StructuredProjection_MapsDocumentAndRuleFindingsToExactFields()
    {
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(
            PolicyEditorTestFixtures.BuildActiveManagement());
        PolicyEditorDraftRule draftRule =
            session.AddRule(PolicyRuleFactory.CreateBlank("first-rule"));
        draftRule.Match.Operations.Add(Devolutions.Now.Policy.Model.Operation.Install);
        string raw = session.GetEffectiveRawJson();
        var validation = new PolicyValidationResult
        {
            IsValid = false,
            Findings =
            [
                Error(PolicyFindingCode.UnsupportedPolicyFormatVersion, "/PolicyFormatVersion", "format 2 is unsupported"),
                Error(PolicyFindingCode.InvalidValidityInterval, "/Metadata/ValidUntil", "must follow ValidFrom"),
                Error(PolicyFindingCode.InvalidFieldValue, "/Rules/0/Match/PackageIdentifiers/Exact/0", "invalid package identifier"),
                Error(PolicyFindingCode.InvalidFieldValue, "/Rules/0/Match/Version/Exact/0", "invalid exact version"),
            ],
        };
        session.ApplyValidationResult(raw, validation);
        using var sessionViewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var dialog = new PolicyEditorDialogViewModel(sessionViewModel, (_, _) => { });

        Assert.True(dialog.Document.HasPolicyFormatVersionErrors);
        Assert.Contains("format 2", Assert.Single(dialog.Document.PolicyFormatVersionFindings).Message);
        Assert.True(dialog.Document.HasValidUntilErrors);
        Assert.False(dialog.Document.HasValidFromErrors);
        Assert.False(dialog.Document.HasPublisherErrors);
        PolicyEditorRuleUi rule = Assert.Single(dialog.Rules);
        Assert.True(rule.HasExactPackageIdentifierErrors);
        Assert.True(rule.HasExactVersionErrors);
        Assert.Contains("package identifier", Assert.Single(rule.ExactPackageIdentifierFindings).Message);
        Assert.Contains("exact version", Assert.Single(rule.ExactVersionFindings).Message);
        Assert.False(rule.HasMinVersionErrors);
        Assert.False(rule.HasMaxVersionErrors);
    }

    [Fact]
    public void ScalarTruncation_DoesNotSplitSupplementaryPlaneCharacters()
    {
        string input = new string('a', 2047) + "\U0001F680" + "discarded";

        string result = PolicyFindingPresentation.SanitizeAgentText(input, 2048);

        Assert.Equal(2048, result.EnumerateRunes().Count());
        Assert.EndsWith("\U0001F680", result, StringComparison.Ordinal);
        Assert.True(char.IsSurrogatePair(result, result.Length - 2));
    }

    [Fact]
    public void Build_CapsThousandsOfFindingsAndAddsLocalizedOmissionRecord()
    {
        PolicyValidationFinding[] findings = Enumerable.Range(0, 5000)
            .Select(index => Finding($"/rules/{index}", $"rule-{index}"))
            .ToArray();

        PolicyEditorFindingIndex index = PolicyEditorFindingIndex.Build(findings);

        Assert.Equal(PolicyEditorFindingIndex.MaxDisplayedFindings, index.All.Count);
        Assert.True(index.FindingsTruncated);
        Assert.Equal(4801, index.OmittedFindingCount);
        Assert.Contains("4801", index.All[^1].Message);
        Assert.Empty(index.All[^1].Pointer);
    }

    [Fact]
    public void FromShared_CapsArgumentCountKeysValuesAndRenderedMessage()
    {
        var arguments = new Dictionary<string, JsonElement>();
        using JsonDocument oversized = JsonDocument.Parse($"\"{new string('x', 5000)}\"");
        for (int index = 0; index < 100; index++)
        {
            arguments[$"{index:D3}{new string('k', 500)}"] = oversized.RootElement.Clone();
        }

        var shared = new PolicyFinding
        {
            Severity = PolicyFindingSeverity.Warning,
            Code = PolicyFindingCode.SensitiveOptionAllowed,
            Path = new string('p', 5000),
            RuleId = new string('r', 5000),
            Message = new string('m', 5000),
            Arguments = arguments,
        };

        PolicyValidationFinding finding = PolicyValidationFinding.FromShared(shared);

        Assert.Equal(32, finding.Arguments!.Count);
        Assert.All(finding.Arguments, pair =>
        {
            Assert.True(pair.Key.EnumerateRunes().Count() <= 256);
            Assert.True(pair.Value.EnumerateRunes().Count() <= 256);
        });
        Assert.True(finding.Pointer.EnumerateRunes().Count() <= 2048);
        Assert.True(finding.RuleId!.EnumerateRunes().Count() <= 2048);
        Assert.True(finding.Message.EnumerateRunes().Count() <= 2048);
    }

    private static PolicyFinding Error(
        PolicyFindingCode code,
        string pointer,
        string message) =>
        new()
        {
            Severity = PolicyFindingSeverity.Error,
            Code = code,
            Path = pointer,
            Message = message,
        };
}
