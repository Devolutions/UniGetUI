using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorTemplatesTests
{
    [Fact]
    public void CreateNew_UsesCurrentFinalFormat()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(PolicyFormatVersion.Current, draft.PolicyFormatVersion);
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        Assert.DoesNotContain("\"PolicyType\"", raw);
        Assert.DoesNotContain("\"RulePrecedence\"", raw);
    }

    [Fact]
    public void CreateNew_DefaultsToDenyAndNoRules()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Equal(Decision.Deny, draft.Enforcement.DefaultDecision);
        Assert.Empty(draft.Rules);
        Assert.Empty(PolicyEditorLocalValidation.ValidateDraft(draft));
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        Assert.Contains("\"Rules\": []", raw);
        Assert.True(PolicyEditorRawSyntax.TryParseStrict(
            raw,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        Assert.Null(error);
        Assert.Empty(parsed!.Rules);
    }

    [Fact]
    public void CreateNew_UsesSuppliedIdAndPublisher()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("my-id", "My Publisher");

        Assert.Equal("my-id", draft.Metadata.Id);
        Assert.Equal("My Publisher", draft.Metadata.Publisher);
    }

    [Fact]
    public void CreateNew_HasNoValidityWindowOrDescriptionByDefault()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        Assert.Null(draft.Metadata.ValidFrom);
        Assert.Null(draft.Metadata.ValidUntil);
        Assert.Null(draft.Metadata.Description);
        Assert.Null(draft.Metadata.SupportUrl);
        Assert.Null(draft.Enforcement.AuditMode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_RejectsEmptyId(string? id)
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorTemplates.CreateNew(id!, "Contoso"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CreateNew_RejectsEmptyPublisher(string? publisher)
    {
        Assert.Throws<ArgumentException>(() => PolicyEditorTemplates.CreateNew("id-1", publisher!));
    }

    [Fact]
    public void CreateNew_PreservesWhitespaceOnlyPublisher()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", " ");

        Assert.Equal(" ", draft.Metadata.Publisher);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("a._:-Z9", true)]
    [InlineData("-starts-with-dash", false)]
    [InlineData("contains space", false)]
    [InlineData("é", false)]
    public void IsValidResourceId_EnforcesContractCharacters(string value, bool expected) =>
        Assert.Equal(expected, PolicyEditorTemplates.IsValidResourceId(value));

    [Fact]
    public void IsValidResourceId_EnforcesMaximumLength()
    {
        Assert.True(PolicyEditorTemplates.IsValidResourceId(new string('a', 128)));
        Assert.False(PolicyEditorTemplates.IsValidResourceId(new string('a', 129)));
    }

    [Fact]
    public void LocalResourceIdValidation_ReportsPrecisePointersAndLength()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew(
            new string('a', 129),
            "Contoso");
        draft.Rules.Add(PolicyRuleFactory.CreateBlank("Allow WinGet updates"));
        draft.Rules[0].Match.Operations.Add(Operation.Install);

        IReadOnlyList<PolicyValidationFinding> findings =
            PolicyEditorLocalValidation.ValidateDraft(draft);

        Assert.Collection(
            findings,
            finding =>
            {
                Assert.Equal("/Metadata/Id", finding.Pointer);
                Assert.Contains("cannot exceed 128", finding.Message);
                Assert.Equal("Policy ID", finding.FriendlyLocation);
            },
            finding =>
            {
                Assert.Equal("/Rules/0/Id", finding.Pointer);
                Assert.Contains("spaces are not allowed", finding.Message);
                Assert.Contains("Rule ID", finding.FriendlyLocation);
            });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SourceNames_RequireExactlyOneManager(int managerCount)
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("source-rule");
        rule.Match.SourceNames.Add("corporate");
        if (managerCount >= 1) rule.Match.Managers.Add(ManagerName.Winget);
        if (managerCount >= 2) rule.Match.Managers.Add(ManagerName.Scoop);
        draft.Rules.Add(rule);

        PolicyValidationFinding finding = Assert.Single(
            PolicyEditorLocalValidation.ValidateDraft(draft),
            item => item.Pointer == "/Rules/0/Match/Managers");

        Assert.Contains("exactly one Package manager", finding.Message);
        Assert.Contains("separate rules", finding.Message);
    }

    [Fact]
    public void SourceNames_WithOneManagerAreValid()
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("source-rule");
        rule.Match.SourceNames.Add("corporate");
        rule.Match.Managers.Add(ManagerName.Winget);
        draft.Rules.Add(rule);

        Assert.DoesNotContain(
            PolicyEditorLocalValidation.ValidateDraft(draft),
            item => item.Pointer.EndsWith("/Managers", StringComparison.Ordinal));
    }

    [Fact]
    public void SafetyAdvisories_AppearOnlyForSelectedRiskyAllowStatesAndAreDeduplicated()
    {
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("allow-rule");
        rule.Decision = Decision.Allow;
        rule.Enabled = true;
        rule.Match.Operations.Add(Operation.Install);
        rule.Match.SkipHashCheck = TriState.True;
        rule.Match.HasCustomParameters = TriState.True;
        rule.Match.HasCustomInstallLocation = TriState.True;
        rule.Match.HasPrePostCommands = TriState.True;
        rule.Constraints = new PolicyEditorDraftConstraints
        {
            AllowInteractive = true,
            AllowSkipHashCheck = true,
            AllowPreRelease = true,
            AllowCustomInstallLocation = true,
            AllowCustomParameters = true,
            AllowPrePostCommands = true,
            AllowKillBeforeOperation = true,
            AllowUninstallPrevious = true,
        };

        IReadOnlyList<string> advisories =
            PolicyEditorAdvisories.FieldSpecific(rule);

        Assert.Equal(advisories.Count, advisories.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(advisories, message => message.Contains("integrity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(advisories, message => message.Contains("custom install", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(advisories, message => message.Contains("arbitrary commands", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advisories, message => message.Contains("user interaction", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advisories, message => message.Contains("prerelease", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advisories, message => message.Contains("stopped", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advisories, message => message.Contains("removing", StringComparison.OrdinalIgnoreCase));

        rule.Decision = Decision.Deny;
        Assert.Empty(PolicyEditorAdvisories.FieldSpecific(rule));

        rule.Decision = Decision.Allow;
        rule.Match.Managers.Add(ManagerName.Winget);
        Assert.Contains(
            PolicyEditorAdvisories.FieldSpecific(rule),
            message => message.Contains("arbitrary commands", StringComparison.OrdinalIgnoreCase));
        rule.Match.SkipHashCheck = TriState.False;
        Assert.DoesNotContain(
            PolicyEditorAdvisories.FieldSpecific(rule),
            message => message.Contains("integrity", StringComparison.OrdinalIgnoreCase));
        rule.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        rule.Match.ExactPackageIdentifiers.Add("Contoso.App");
        rule.Constraints.AllowSkipHashCheck = false;
        Assert.Empty(PolicyEditorAdvisories.FieldSpecific(rule));
    }

    [Fact]
    public void RiskCoverage_SuppressesOnlyProvablyShadowedSensitiveBehavior()
    {
        PolicyEditorDraftRule deny = PolicyRuleFactory.CreateBlank("deny-skip-hash");
        deny.Enabled = true;
        deny.Match.Operations.Add(Operation.Update);
        deny.Match.Managers.Add(ManagerName.Winget);
        deny.Match.SkipHashCheck = TriState.True;
        PolicyEditorDraftRule allow = PolicyRuleFactory.CreateBlank("allow-updates");
        allow.Enabled = true;
        allow.Decision = Decision.Allow;
        allow.Match.Operations.Add(Operation.Update);
        allow.Match.Managers.Add(ManagerName.Winget);
        allow.Constraints = new PolicyEditorDraftConstraints
        {
            AllowSkipHashCheck = true,
        };

        Assert.True(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));
        Assert.Empty(PolicyEditorAdvisories.SkipHashCheck(allow, [deny, allow], 1));
        Assert.True(PolicyEditorAdvisories.PolicyEditorRiskCoverage.ShouldSuppressFinding(
            new PolicyValidationFinding(
                "/Rules/1/Match/SkipHashCheck",
                "allow-updates",
                PolicyValidationSeverity.Warning,
                "warning",
                Devolutions.Now.Policy.Api.PolicyFindingCode.SensitiveOptionAllowed,
                new Dictionary<string, string> { ["option"] = "\"SkipHashCheck\"" }),
            [deny, allow]));

        deny.Match.HasPrePostCommands = TriState.True;
        Assert.False(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));

        deny.Match.HasPrePostCommands = TriState.Omitted;
        deny.Match.Operations[0] = Operation.Install;
        Assert.False(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));

        deny.Match.Operations[0] = Operation.Update;
        deny.Match.Managers[0] = ManagerName.Scoop;
        Assert.False(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));

        deny.Match.Managers[0] = ManagerName.Winget;
        allow.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        allow.Match.ExactPackageIdentifiers.Add("Contoso.App");
        deny.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        deny.Match.ExactPackageIdentifiers.Add("Contoso.App");
        Assert.True(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));

        deny.Match.PackageIdentifierMode = PackageIdentifierMode.Patterns;
        deny.Match.PackageIdentifierPatterns.Add("*");
        Assert.False(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [deny, allow],
            1,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));

        Assert.False(PolicyEditorAdvisories.PolicyEditorRiskCoverage.IsCovered(
            [allow, deny],
            0,
            PolicyEditorAdvisories.PolicyEditorRisk.SkipHashCheck));
    }

    [Fact]
    public void OmittedSensitiveMatchUsesSelectorLocalCausalAdvisory()
    {
        PolicyEditorDraftRule allow = PolicyRuleFactory.CreateBlank("allow-rule");
        allow.Enabled = true;
        allow.Decision = Decision.Allow;
        allow.Match.Operations.Add(Operation.Update);
        allow.Constraints = new PolicyEditorDraftConstraints
        {
            AllowSkipHashCheck = true,
        };

        Assert.Contains(
            "Does not matter",
            PolicyEditorAdvisories.SkipHashCheckMatch(allow),
            StringComparison.Ordinal);
        Assert.Contains(
            "Set it to No",
            PolicyEditorAdvisories.SkipHashCheckMatch(allow),
            StringComparison.Ordinal);
        Assert.Empty(PolicyEditorAdvisories.SkipHashCheck(allow));

        allow.Match.SkipHashCheck = TriState.True;

        Assert.Empty(PolicyEditorAdvisories.SkipHashCheckMatch(allow));
        Assert.Contains(
            "explicitly permits",
            PolicyEditorAdvisories.SkipHashCheck(allow),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AddedRule_TriStateSelectorsDisplayDoesNotMatter()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("id-1", "Contoso"));
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        PolicyEditorDraftRule added = session.AddRule();
        using var rule = new PolicyEditorRuleUi(added, viewModel);
        int omittedIndex = PolicyEditorEnumDisplay.IndexOfTriState(TriState.Omitted);

        Assert.False(added.Enabled);
        Assert.Equal(omittedIndex, rule.InteractiveIndex);
        Assert.Equal(omittedIndex, rule.SkipHashCheckIndex);
        Assert.Equal(omittedIndex, rule.PreReleaseIndex);
        Assert.Equal(omittedIndex, rule.HasCustomParametersIndex);
        Assert.Equal(omittedIndex, rule.HasCustomInstallLocationIndex);
        Assert.Equal(omittedIndex, rule.HasPrePostCommandsIndex);
        Assert.Equal(omittedIndex, rule.HasKillBeforeOperationIndex);
        Assert.Equal(omittedIndex, rule.HasUninstallPreviousIndex);
        Assert.Equal("Does not matter", PolicyEditorEnumDisplay.TriStateDisplayItems[omittedIndex]);
        Assert.True(rule.IsDisabled);
        Assert.False(rule.IsEnabledWithoutMatchConditions);

        rule.Enabled = true;
        Assert.False(rule.IsDisabled);
        Assert.True(rule.IsEnabledWithoutMatchConditions);

        rule.InteractiveIndex = PolicyEditorEnumDisplay.IndexOfTriState(TriState.False);
        Assert.False(rule.IsEnabledWithoutMatchConditions);
    }

    [Fact]
    public void RuleMovementAvailabilityTracksVisibleBoundaries()
    {
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew("id-1", "Contoso"));
        PolicyEditorDraftRule first = session.AddRule();
        first.Match.Operations.Add(Operation.Install);
        PolicyEditorDraftRule second = session.AddRule();
        second.Match.Operations.Add(Operation.Update);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        using var firstUi = new PolicyEditorRuleUi(first, 0, viewModel);
        using var secondUi = new PolicyEditorRuleUi(second, 1, viewModel);

        Assert.False(firstUi.CanMoveUp);
        Assert.True(firstUi.CanMoveDown);
        Assert.True(secondUi.CanMoveUp);
        Assert.False(secondUi.CanMoveDown);
    }

    [Fact]
    public void CreateNew_DoesNotExposeRevisionOrPublishedAt()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");

        // PolicyEditorDraftMetadata deliberately has no Revision/PublishedAt members; this test
        // documents that contract by asserting the type surface, via reflection, excludes them.
        System.Reflection.PropertyInfo[] props = draft.Metadata.GetType().GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
    }

    [Fact]
    public void PolicyFormatVersion_IsStampedForNewDraftsAndReadOnlyInStructuredUi()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("policy", "Publisher");
        PolicyEditorSession session = PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            draft);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.Equal(PolicyFormatVersion.Current, draft.PolicyFormatVersion);
        Assert.Equal(draft.PolicyFormatVersion.Value, document.PolicyFormatVersion);
        Assert.False(
            typeof(PolicyEditorDocumentUi)
                .GetProperty(nameof(PolicyEditorDocumentUi.PolicyFormatVersion))!
                .CanWrite);
    }

    [Fact]
    public void StructuredUpdate_PreservesCompatibleExistingPolicyFormatVersion()
    {
        Devolutions.Now.Policy.Api.PolicyManagementSnapshot management =
            PolicyEditorTestFixtures.BuildActiveManagement();
        PolicyEditorSession session = PolicyEditorSession.StartUpdate(management);
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());
        var document = new PolicyEditorDocumentUi(viewModel);

        Assert.Equal("1.2.3", viewModel.Draft.PolicyFormatVersion.Value);
        Assert.Equal("1.2.3", document.PolicyFormatVersion);
    }

    [Fact]
    public void CreateReplacementId_AppendsReadableSuffixWhenItFits()
    {
        Assert.Equal("active-policy-new", PolicyEditorTemplates.CreateReplacementId("active-policy"));
    }

    [Fact]
    public void CreateReplacementId_UsesTheFullLimitWithoutChangingTheValidPrefix()
    {
        string activeId = new('a', PolicyEditorTemplates.ResourceIdMaxLength - 4);

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal(PolicyEditorTemplates.ResourceIdMaxLength, replacementId.Length);
        Assert.Equal($"{activeId}-new", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    [Fact]
    public void CreateReplacementId_TruncatesAMaximumLengthIdentifier()
    {
        string activeId = new('a', PolicyEditorTemplates.ResourceIdMaxLength);

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal($"{new string('a', PolicyEditorTemplates.ResourceIdMaxLength - 4)}-new", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    [Fact]
    public void CreateReplacementId_MaximumLengthAlreadyEndingInSuffixStillChangesIdentity()
    {
        string activeId = $"{new string('a', PolicyEditorTemplates.ResourceIdMaxLength - 4)}-new";

        string replacementId = PolicyEditorTemplates.CreateReplacementId(activeId);

        Assert.Equal($"{activeId[..^1]}0", replacementId);
        AssertValidReplacement(activeId, replacementId);
    }

    private static void AssertValidReplacement(string activeId, string replacementId)
    {
        Assert.NotEqual(activeId, replacementId);
        Assert.InRange(replacementId.Length, 1, PolicyEditorTemplates.ResourceIdMaxLength);
        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:\\-]{0,127}$", replacementId);
    }
}
