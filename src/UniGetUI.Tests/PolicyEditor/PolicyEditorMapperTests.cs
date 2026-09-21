using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;
using InvalidPolicyDiagnostics = Devolutions.Now.Policy.Api.InvalidPolicyDiagnostics;
using PolicyFinding = Devolutions.Now.Policy.Api.PolicyFinding;
using PolicyFindingCode = Devolutions.Now.Policy.Api.PolicyFindingCode;
using PolicyFindingSeverity = Devolutions.Now.Policy.Api.PolicyFindingSeverity;
using PolicyManagementSnapshot = Devolutions.Now.Policy.Api.PolicyManagementSnapshot;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorMapperTests
{
    [Fact]
    public void CloneManagementSnapshot_OmitsInvalidDiagnosticsFromEditorState()
    {
        PolicyManagementSnapshot source = PolicyEditorTestFixtures.BuildInvalidManagement();
        source.InvalidDiagnostics = new InvalidPolicyDiagnostics
        {
            DiagnosticsVersion = "1.0",
            Findings =
            [
                new PolicyFinding
                {
                    FindingVersion = "1.0",
                    Severity = PolicyFindingSeverity.Error,
                    Code = PolicyFindingCode.SchemaViolation,
                    Message = new string('x', 100_000),
                },
            ],
        };

        PolicyManagementSnapshot clone = PolicyEditorMapper.CloneManagementSnapshot(source);

        Assert.Equal(source.State, clone.State);
        Assert.Equal(source.StoreToken, clone.StoreToken);
        Assert.Null(clone.InvalidDiagnostics);
    }

    [Fact]
    public void ToDraft_MapsEveryDocumentFieldExceptRevisionAndPublishedAt()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(document);

        Assert.Equal(document.PolicyFormatVersion, draft.PolicyFormatVersion);

        Assert.Equal(document.Metadata.Id, draft.Metadata.Id);
        Assert.Equal(document.Metadata.Publisher, draft.Metadata.Publisher);
        Assert.Equal(document.Metadata.ValidFrom, draft.Metadata.ValidFrom);
        Assert.Equal(document.Metadata.ValidUntil, draft.Metadata.ValidUntil);
        Assert.Equal(document.Metadata.Description, draft.Metadata.Description);
        Assert.Equal(document.Metadata.SupportUrl, draft.Metadata.SupportUrl);

        Assert.Equal(document.Enforcement.DefaultDecision, draft.Enforcement.DefaultDecision);
        Assert.Equal(document.Enforcement.AuditMode, draft.Enforcement.AuditMode);

        PolicyEditorDraftRule draftRule = Assert.Single(draft.Rules);
        PolicyRule sourceRule = document.Rules[0];
        Assert.Equal(sourceRule.Id, draftRule.Id);
        Assert.Equal(sourceRule.Enabled, draftRule.Enabled);
        Assert.Equal(0u, draftRule.Priority);
        Assert.Equal(sourceRule.Decision, draftRule.Decision);
        Assert.Equal(sourceRule.Reason, draftRule.Reason);

        Assert.Equal(sourceRule.Match.Operations, draftRule.Match.Operations);
        Assert.Equal(sourceRule.Match.Managers, draftRule.Match.Managers);
        Assert.Equal(sourceRule.Match.SourceNames, draftRule.Match.SourceNames);
        Assert.Equal(PackageIdentifierMode.Exact, draftRule.Match.PackageIdentifierMode);
        Assert.Equal(
            sourceRule.Match.PackageIdentifiers!.Exact,
            draftRule.Match.ExactPackageIdentifiers);
        Assert.Equal(PackageVersionMode.Range, draftRule.Match.VersionMode);
        Assert.NotNull(draftRule.Match.VersionRange);
        Assert.Equal(sourceRule.Match.Version!.Range!.MinVersion, draftRule.Match.VersionRange!.MinVersion);
        Assert.Equal(sourceRule.Match.Version.Range.MaxVersion, draftRule.Match.VersionRange.MaxVersion);
        Assert.Equal(sourceRule.Match.Version.Range.IncludePrerelease, draftRule.Match.VersionRange.IncludePrerelease);
        Assert.Equal(sourceRule.Match.Scopes, draftRule.Match.Scopes);
        Assert.Equal(sourceRule.Match.Architectures, draftRule.Match.Architectures);
        Assert.Equal(sourceRule.Match.ExecutionElevation, draftRule.Match.ExecutionElevation);

        // Every boolean tri-state criterion round-trips through the mapper's ToTriState/FromTriState.
        Assert.Equal(TriState.True, draftRule.Match.Interactive);
        Assert.Equal(TriState.False, draftRule.Match.SkipHashCheck);
        Assert.Equal(TriState.Omitted, draftRule.Match.PreRelease);
        Assert.Equal(TriState.True, draftRule.Match.HasCustomParameters);
        Assert.Equal(TriState.False, draftRule.Match.HasCustomInstallLocation);
        Assert.Equal(TriState.Omitted, draftRule.Match.HasPrePostCommands);
        Assert.Equal(TriState.True, draftRule.Match.HasKillBeforeOperation);
        Assert.Equal(TriState.False, draftRule.Match.HasUninstallPrevious);

        Assert.NotNull(draftRule.Constraints);
        PolicyConstraints sourceConstraints = sourceRule.Constraints!;
        PolicyEditorDraftConstraints draftConstraints = draftRule.Constraints!;
        Assert.Equal(sourceConstraints.AllowInteractive, draftConstraints.AllowInteractive);
        Assert.Equal(sourceConstraints.AllowSkipHashCheck, draftConstraints.AllowSkipHashCheck);
        Assert.Equal(sourceConstraints.AllowPreRelease, draftConstraints.AllowPreRelease);
        Assert.Equal(sourceConstraints.AllowCustomInstallLocation, draftConstraints.AllowCustomInstallLocation);
        Assert.Equal(sourceConstraints.AllowedInstallLocationPatterns, draftConstraints.AllowedInstallLocationPatterns);
        Assert.Equal(sourceConstraints.AllowCustomParameters, draftConstraints.AllowCustomParameters);
        Assert.Equal(sourceConstraints.AllowedCustomParameters, draftConstraints.AllowedCustomParameters);
        Assert.Equal(sourceConstraints.AllowedCustomParameterPatterns, draftConstraints.AllowedCustomParameterPatterns);
        Assert.Equal(sourceConstraints.DeniedCustomParameters, draftConstraints.DeniedCustomParameters);
        Assert.Equal(sourceConstraints.AllowPrePostCommands, draftConstraints.AllowPrePostCommands);
        Assert.Equal(sourceConstraints.AllowKillBeforeOperation, draftConstraints.AllowKillBeforeOperation);
        Assert.Equal(sourceConstraints.AllowUninstallPrevious, draftConstraints.AllowUninstallPrevious);
        Assert.Equal(sourceConstraints.AllowUpgrade, draftConstraints.AllowUpgrade);
    }

    [Theory]
    [InlineData(null, TriState.Omitted)]
    [InlineData(true, TriState.True)]
    [InlineData(false, TriState.False)]
    public void ToDraft_MapsNullableBooleanToTriState(bool? wireValue, TriState expected)
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildMinimalRule();
        rule.Match.Interactive = wireValue;
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(document);

        Assert.Equal(expected, draft.Rules[0].Match.Interactive);
    }

    [Theory]
    [InlineData(TriState.Omitted)]
    [InlineData(TriState.True)]
    [InlineData(TriState.False)]
    public void FromTriState_RoundTripsThroughToTriState(TriState state)
    {
        bool? wire = PolicyEditorMapper.FromTriState(state);
        TriState roundTripped = PolicyEditorMapper.ToTriState(wire);

        Assert.Equal(state, roundTripped);
    }

    [Fact]
    public void FinalConditionModes_AreExclusiveAndCanonicalJsonOmitsInactiveShapes()
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule");
        draft.Rules.Add(rule);

        rule.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        rule.Match.ExactPackageIdentifiers.Add("Contoso.App");
        rule.Match.PackageIdentifierPatterns.Add("Ignored.*");
        rule.Match.VersionMode = PackageVersionMode.Exact;
        rule.Match.ExactVersions.Add("release-channel-A");
        rule.Match.VersionRange = new PolicyEditorDraftVersionRange
        {
            MinVersion = "1.0.0",
            MaxVersion = "2.0.0",
        };
        rule.Match.Interactive = TriState.True;

        string exact = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        Assert.Contains("\"Exact\": [", exact);
        Assert.Contains("\"release-channel-A\"", exact);
        Assert.Contains("\"Interactive\": true", exact);
        Assert.DoesNotContain("\"Patterns\"", exact);
        Assert.DoesNotContain("\"Range\"", exact);
        Assert.DoesNotContain("[true]", exact);

        rule.Match.PackageIdentifierMode = PackageIdentifierMode.Patterns;
        rule.Match.VersionMode = PackageVersionMode.Range;
        string ranged = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        Assert.Contains("\"Patterns\": [", ranged);
        Assert.Contains("\"Range\": {", ranged);
        Assert.DoesNotContain("\"release-channel-A\"", ranged);
        Assert.DoesNotContain("\"Contoso.App\"", ranged);

        rule.Match.PackageIdentifierMode = PackageIdentifierMode.Omitted;
        rule.Match.VersionMode = PackageVersionMode.Omitted;
        string omitted = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        Assert.DoesNotContain("\"PackageIdentifiers\"", omitted);
        Assert.DoesNotContain("\"Version\"", omitted);
    }

    [Fact]
    public void NullableBooleanMatch_OmittedSerializesAsNoProperty()
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule");
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);

        string omitted = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        Assert.DoesNotContain("\"Interactive\"", omitted);

        rule.Match.Interactive = TriState.False;
        string selected = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        Assert.Contains("\"Interactive\": false", selected);
        Assert.DoesNotContain("[false]", selected);
    }

    // ---- PolicyDocument <-> PolicyEditorDraftDocument (authoritative committed shape) -------------

    [Fact]
    public void ToDocument_ProducesFinalContractWithoutRemovedFields()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");

        PolicyDocument document = PolicyEditorMapper.ToDocument(draft, revision: 1, publishedAt: DateTimeOffset.UtcNow);

        string json = PolicySerializer.Serialize(document);
        Assert.DoesNotContain("\"PolicyType\"", json);
        Assert.DoesNotContain("\"RulePrecedence\"", json);
    }

    [Fact]
    public void ToDocument_UsesSuppliedRevisionAndPublishedAt_NeverSynthesizesThem()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");
        DateTimeOffset publishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        PolicyDocument document = PolicyEditorMapper.ToDocument(draft, revision: 7, publishedAt: publishedAt);

        Assert.Equal((uint)7, document.Metadata.Revision);
        Assert.Equal(publishedAt, document.Metadata.PublishedAt);
    }

    [Fact]
    public void RoundTrip_DraftToDocumentToDraft_PreservesEditableContent()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument original = PolicyEditorTestFixtures.BuildDocument(rules: rule);
        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(original);

        PolicyDocument roundTripped = PolicyEditorMapper.ToDocument(draft, original.Metadata.Revision, original.Metadata.PublishedAt);
        PolicyEditorDraftDocument redraft = PolicyEditorMapper.ToDraft(roundTripped);

        string originalCanonical = PolicySerializer.Serialize(PolicyEditorMapper.ToDocument(draft, original.Metadata.Revision, original.Metadata.PublishedAt));
        string redraftCanonical = PolicySerializer.Serialize(PolicyEditorMapper.ToDocument(redraft, original.Metadata.Revision, original.Metadata.PublishedAt));
        Assert.Equal(originalCanonical, redraftCanonical);
    }

    [Fact]
    public void ToDraft_DeepCopiesLists_MutatingDraftDoesNotAffectSourceDocument()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(document);
        draft.Rules[0].Match.SourceNames.Add("new-source");
        draft.Metadata.Description = "changed";

        Assert.DoesNotContain("new-source", document.Rules[0].Match.SourceNames);
        Assert.NotEqual("changed", document.Metadata.Description);
    }

    [Fact]
    public void ToDocument_DeepCopiesLists_MutatingDocumentDoesNotAffectSourceDraft()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");
        draft.Rules.Clear();
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank();
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);
        draft.Rules[0].Match.SourceNames.Add("winget");

        PolicyDocument document = PolicyEditorMapper.ToDocument(draft, revision: 1, publishedAt: DateTimeOffset.UtcNow);
        document.Rules[0].Match.SourceNames.Add("extra");

        Assert.Single(draft.Rules[0].Match.SourceNames);
    }

    [Fact]
    public void CloneDocument_PreservesRevisionAndPublishedAt_AndIsIndependent()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument original = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyDocument clone = PolicyEditorMapper.CloneDocument(original);
        clone.Metadata.Description = "changed";
        clone.Rules[0].Match.SourceNames.Add("added");

        Assert.Equal(original.Metadata.Revision, clone.Metadata.Revision);
        Assert.Equal(original.Metadata.PublishedAt, clone.Metadata.PublishedAt);
        Assert.NotEqual("changed", original.Metadata.Description);
        Assert.DoesNotContain("added", original.Rules[0].Match.SourceNames);
    }

    // ---- PolicyDraftDocument (package draft, no Revision/PublishedAt) <-> PolicyEditorDraftDocument ----
    // (correction #1: the raw-JSON seam must go through this shape, never the full PolicyDocument.)

    [Fact]
    public void ToDraft_FromPackageDraftDocument_MapsEveryFieldAndHasNoRevisionOrPublishedAt()
    {
        var packageDraft = new PolicyDraftDocument
        {
            PolicyFormatVersion = PolicyFormatVersion.Parse("1.2.3"),
            Metadata = new PolicyDraftMetadata
            {
                Id = "draft-id",
                Publisher = "Draft Publisher",
                ValidFrom = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                ValidUntil = DateTimeOffset.Parse("2027-01-01T00:00:00Z"),
                Description = "a draft",
                SupportUrl = "https://example.com",
            },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Decision.Allow,
                AuditMode = true,
            },
            Rules = [PolicyEditorTestFixtures.BuildFullRule()],
        };

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(packageDraft);

        Assert.Equal("1.2.3", draft.PolicyFormatVersion.Value);
        Assert.Equal("draft-id", draft.Metadata.Id);
        Assert.Equal("Draft Publisher", draft.Metadata.Publisher);
        Assert.Equal(packageDraft.Metadata.ValidFrom, draft.Metadata.ValidFrom);
        Assert.Equal(packageDraft.Metadata.ValidUntil, draft.Metadata.ValidUntil);
        Assert.Equal("a draft", draft.Metadata.Description);
        Assert.Equal("https://example.com", draft.Metadata.SupportUrl);
        Assert.Equal(Decision.Allow, draft.Enforcement.DefaultDecision);
        Assert.Equal(true, draft.Enforcement.AuditMode);
        Assert.Single(draft.Rules);

        // PolicyEditorDraftMetadata has no Revision/PublishedAt members at all.
        System.Reflection.PropertyInfo[] props = draft.Metadata.GetType().GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
    }

    [Fact]
    public void ToSharedDraft_BuildsFinalPackageDraftWithoutRemovedOrServerFields()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");
        draft.Rules.Clear();
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank();
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);

        PolicyDraftDocument shared = PolicyEditorMapper.ToSharedDraft(draft);

        Assert.Single(shared.Rules);
        string json = PolicySerializer.Serialize(shared);
        Assert.DoesNotContain("\"PolicyType\"", json);
        Assert.DoesNotContain("\"RulePrecedence\"", json);

        // The package's own PolicyDraftMetadata type has no Revision/PublishedAt members either.
        System.Reflection.PropertyInfo[] props = shared.Metadata.GetType().GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
    }

    [Fact]
    public void ToDraft_OrdersByEvaluatorPrecedenceAndNormalizesPriorities()
    {
        PolicyRule later = PolicyEditorTestFixtures.BuildMinimalRule("later");
        later.Priority = 20;
        later.Decision = Decision.Allow;
        PolicyRule tiedAllow = PolicyEditorTestFixtures.BuildMinimalRule("tied-allow");
        tiedAllow.Priority = 10;
        tiedAllow.Decision = Decision.Allow;
        PolicyRule tiedDenyFirst = PolicyEditorTestFixtures.BuildMinimalRule("tied-deny-first");
        tiedDenyFirst.Priority = 10;
        tiedDenyFirst.Decision = Decision.Deny;
        PolicyRule tiedDenySecond = PolicyEditorTestFixtures.BuildMinimalRule("tied-deny-second");
        tiedDenySecond.Priority = 10;
        tiedDenySecond.Decision = Decision.Deny;
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(
            rules: [later, tiedAllow, tiedDenyFirst, tiedDenySecond]);

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(document);
        PolicyDraftDocument serialized = PolicyEditorMapper.ToSharedDraft(draft);

        Assert.Equal(
            ["tied-deny-first", "tied-deny-second", "tied-allow", "later"],
            draft.Rules.Select(rule => rule.Id));
        Assert.Equal([0u, 1u, 2u, 3u], draft.Rules.Select(rule => rule.Priority));
        Assert.Equal([0u, 1u, 2u, 3u], serialized.Rules.Select(rule => rule.Priority));
    }

    [Fact]
    public void StructuredRuleMove_NormalizesStableUniquePriorities()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule first = PolicyRuleFactory.CreateBlank("first");
        first.Match.Operations.Add(Operation.Install);
        PolicyEditorDraftRule second = PolicyRuleFactory.CreateBlank("second");
        second.Match.Operations.Add(Operation.Update);
        PolicyRuleListOperations.Add(draft.Rules, first);
        PolicyRuleListOperations.Add(draft.Rules, second);

        PolicyRuleListOperations.Move(draft.Rules, second, 0);
        string once = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        Assert.True(PolicyEditorRawSyntax.TryParseStrict(
            once,
            out PolicyEditorDraftDocument? projected,
            out PolicyEditorSyntaxError? error));
        string twice = PolicyEditorRawSyntax.ToCanonicalRaw(projected!);

        Assert.Null(error);
        Assert.Equal(["second", "first"], draft.Rules.Select(rule => rule.Id));
        Assert.Equal([0u, 1u], draft.Rules.Select(rule => rule.Priority));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ToSharedDraft_ThenToDraft_RoundTripsExactly()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument original = PolicyEditorTestFixtures.BuildDocument(rules: rule);
        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(original);

        PolicyDraftDocument shared = PolicyEditorMapper.ToSharedDraft(draft);
        PolicyEditorDraftDocument redraft = PolicyEditorMapper.ToDraft(shared);

        Assert.Equal(draft.Metadata.Id, redraft.Metadata.Id);
        Assert.Equal(draft.Metadata.Publisher, redraft.Metadata.Publisher);
        Assert.Equal(draft.Enforcement.DefaultDecision, redraft.Enforcement.DefaultDecision);
        Assert.Single(redraft.Rules);
        Assert.Equal(draft.Rules[0].Id, redraft.Rules[0].Id);
    }

    [Fact]
    public void CloneDraftDocument_IsIndependentOfSource()
    {
        var source = new PolicyDraftDocument
        {
            PolicyFormatVersion = PolicyFormatVersion.Current,
            Metadata = new PolicyDraftMetadata { Id = "id-1", Publisher = "Contoso" },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Decision.Deny,
            },
            Rules = [],
        };

        PolicyDraftDocument clone = PolicyEditorMapper.CloneDraftDocument(source);
        clone.Metadata.Description = "mutated after clone";

        Assert.NotEqual("mutated after clone", source.Metadata.Description);
        Assert.Equal(source.Metadata.Id, clone.Metadata.Id);
    }
}
