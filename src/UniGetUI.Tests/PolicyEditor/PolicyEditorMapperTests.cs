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

        Assert.Equal(document.PolicyVersion, draft.PolicyVersion);
        Assert.Equal(PolicyEditorPolicyContract.DraftSchema, draft.Schema);
        Assert.Equal(document.PolicyType, draft.PolicyType);

        Assert.Equal(document.Metadata.Id, draft.Metadata.Id);
        Assert.Equal(document.Metadata.Publisher, draft.Metadata.Publisher);
        Assert.Equal(document.Metadata.ValidFrom, draft.Metadata.ValidFrom);
        Assert.Equal(document.Metadata.ValidUntil, draft.Metadata.ValidUntil);
        Assert.Equal(document.Metadata.Description, draft.Metadata.Description);
        Assert.Equal(document.Metadata.SupportUrl, draft.Metadata.SupportUrl);

        Assert.Equal(document.Enforcement.DefaultDecision, draft.Enforcement.DefaultDecision);
        Assert.Equal(document.Enforcement.RulePrecedence, draft.Enforcement.RulePrecedence);
        Assert.Equal(document.Enforcement.AuditMode, draft.Enforcement.AuditMode);

        PolicyEditorDraftRule draftRule = Assert.Single(draft.Rules);
        PolicyRule sourceRule = document.Rules[0];
        Assert.Equal(sourceRule.Id, draftRule.Id);
        Assert.Equal(sourceRule.Enabled, draftRule.Enabled);
        Assert.Equal(sourceRule.Priority, draftRule.Priority);
        Assert.Equal(sourceRule.Decision, draftRule.Decision);
        Assert.Equal(sourceRule.Reason, draftRule.Reason);

        Assert.Equal(sourceRule.Match.Operations, draftRule.Match.Operations);
        Assert.Equal(sourceRule.Match.Managers, draftRule.Match.Managers);
        Assert.Equal(sourceRule.Match.Sources, draftRule.Match.Sources);
        Assert.Equal(sourceRule.Match.PackageIdentifiers, draftRule.Match.PackageIdentifiers);
        Assert.Equal(sourceRule.Match.PackageNames, draftRule.Match.PackageNames);
        Assert.Equal(sourceRule.Match.Versions, draftRule.Match.Versions);
        Assert.NotNull(draftRule.Match.VersionRange);
        Assert.Equal(sourceRule.Match.VersionRange!.MinVersion, draftRule.Match.VersionRange!.MinVersion);
        Assert.Equal(sourceRule.Match.VersionRange!.MaxVersion, draftRule.Match.VersionRange!.MaxVersion);
        Assert.Equal(sourceRule.Match.VersionRange!.IncludePrerelease, draftRule.Match.VersionRange!.IncludePrerelease);
        Assert.Equal(sourceRule.Match.Scopes, draftRule.Match.Scopes);
        Assert.Equal(sourceRule.Match.Architectures, draftRule.Match.Architectures);
        Assert.Equal(sourceRule.Match.Elevation, draftRule.Match.Elevation);

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

    // ---- Tri-state boolean-match conversion (correction #4: mixed/2+ element arrays are contract-
    // invalid/unreachable and must throw, never be silently normalized) ------------------------------

    [Theory]
    [InlineData(new bool[] { }, TriState.Omitted)]
    [InlineData(new[] { true }, TriState.True)]
    [InlineData(new[] { false }, TriState.False)]
    public void ToDraft_NormalizesEmptyOrSingleElementBooleanListsToTriState(bool[] wireValues, TriState expected)
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildMinimalRule();
        rule.Match.Interactive = [.. wireValues];
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(document);

        Assert.Equal(expected, draft.Rules[0].Match.Interactive);
    }

    [Theory]
    [InlineData(new[] { true, false })]
    [InlineData(new[] { false, true })]
    [InlineData(new[] { true, true })]
    [InlineData(new[] { false, false })]
    [InlineData(new[] { true, false, true })]
    public void ToTriState_RejectsMultiElementBooleanLists_ContractInvalidUnreachable(bool[] wireValues)
    {
        Assert.Throws<InvalidDataException>(() => PolicyEditorMapper.ToTriState(wireValues));
    }

    [Theory]
    [InlineData(new[] { true, false })]
    [InlineData(new[] { false, true })]
    public void ToDraft_RejectsMultiElementBooleanMatchLists_DoesNotSilentlyNormalizeToOmitted(bool[] wireValues)
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildMinimalRule();
        rule.Match.Interactive = [.. wireValues];
        PolicyDocument document = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        Assert.Throws<InvalidDataException>(() => PolicyEditorMapper.ToDraft(document));
    }

    [Theory]
    [InlineData(TriState.Omitted)]
    [InlineData(TriState.True)]
    [InlineData(TriState.False)]
    public void FromTriState_RoundTripsThroughToTriState(TriState state)
    {
        List<bool> wire = PolicyEditorMapper.FromTriState(state);
        TriState roundTripped = PolicyEditorMapper.ToTriState(wire);

        Assert.Equal(state, roundTripped);
    }

    // ---- PolicyDocument <-> PolicyEditorDraftDocument (authoritative committed shape) -------------

    [Fact]
    public void ToDocument_ProducesFixedSchemaAndPolicyTypeRegardlessOfDraftContent()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");

        PolicyDocument document = PolicyEditorMapper.ToDocument(draft, revision: 1, publishedAt: DateTimeOffset.UtcNow);

        Assert.Equal(PolicyEditorPolicyContract.CommittedSchema, document.Schema);
        Assert.Equal(PolicyEditorPolicyContract.PolicyType, document.PolicyType);
        Assert.Equal(RulePrecedence.PriorityThenDeny, document.Enforcement.RulePrecedence);
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
        draft.Rules[0].Match.Sources.Add("new-source");
        draft.Metadata.Description = "changed";

        Assert.DoesNotContain("new-source", document.Rules[0].Match.Sources);
        Assert.NotEqual("changed", document.Metadata.Description);
    }

    [Fact]
    public void ToDocument_DeepCopiesLists_MutatingDocumentDoesNotAffectSourceDraft()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");
        draft.Rules.Add(PolicyRuleFactory.CreateBlank());
        draft.Rules[0].Match.Sources.Add("winget");

        PolicyDocument document = PolicyEditorMapper.ToDocument(draft, revision: 1, publishedAt: DateTimeOffset.UtcNow);
        document.Rules[0].Match.Sources.Add("extra");

        Assert.Single(draft.Rules[0].Match.Sources);
    }

    [Fact]
    public void CloneDocument_PreservesRevisionAndPublishedAt_AndIsIndependent()
    {
        PolicyRule rule = PolicyEditorTestFixtures.BuildFullRule();
        PolicyDocument original = PolicyEditorTestFixtures.BuildDocument(rules: rule);

        PolicyDocument clone = PolicyEditorMapper.CloneDocument(original);
        clone.Metadata.Description = "changed";
        clone.Rules[0].Match.Sources.Add("added");

        Assert.Equal(original.Metadata.Revision, clone.Metadata.Revision);
        Assert.Equal(original.Metadata.PublishedAt, clone.Metadata.PublishedAt);
        Assert.NotEqual("changed", original.Metadata.Description);
        Assert.DoesNotContain("added", original.Rules[0].Match.Sources);
    }

    // ---- PolicyDraftDocument (package draft, no Revision/PublishedAt) <-> PolicyEditorDraftDocument ----
    // (correction #1: the raw-JSON seam must go through this shape, never the full PolicyDocument.)

    [Fact]
    public void ToDraft_FromPackageDraftDocument_MapsEveryFieldAndHasNoRevisionOrPublishedAt()
    {
        var packageDraft = new PolicyDraftDocument
        {
            Schema = PolicyEditorPolicyContract.DraftSchema,
            PolicyVersion = "2.0.0",
            PolicyType = PolicyEditorPolicyContract.PolicyType,
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
                RulePrecedence = RulePrecedence.PriorityThenDeny,
                AuditMode = true,
            },
            Rules = [PolicyEditorTestFixtures.BuildFullRule()],
        };

        PolicyEditorDraftDocument draft = PolicyEditorMapper.ToDraft(packageDraft);

        Assert.Equal("2.0.0", draft.PolicyVersion);
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
    public void ToSharedDraft_BuildsPackageDraftDocument_FixedSchemaTypeAndNoRevisionOrPublishedAt()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("some-id", "Some Publisher");
        draft.Rules.Add(PolicyRuleFactory.CreateBlank());

        PolicyDraftDocument shared = PolicyEditorMapper.ToSharedDraft(draft);

        Assert.Equal(PolicyEditorPolicyContract.DraftSchema, shared.Schema);
        Assert.Equal(PolicyEditorPolicyContract.PolicyType, shared.PolicyType);
        Assert.Equal(RulePrecedence.PriorityThenDeny, shared.Enforcement.RulePrecedence);
        Assert.Single(shared.Rules);

        // The package's own PolicyDraftMetadata type has no Revision/PublishedAt members either.
        System.Reflection.PropertyInfo[] props = shared.Metadata.GetType().GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Revision");
        Assert.DoesNotContain(props, p => p.Name == "PublishedAt");
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
            Schema = PolicyEditorPolicyContract.DraftSchema,
            PolicyVersion = "1.0.0",
            PolicyType = PolicyEditorPolicyContract.PolicyType,
            Metadata = new PolicyDraftMetadata { Id = "id-1", Publisher = "Contoso" },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Decision.Deny,
                RulePrecedence = RulePrecedence.PriorityThenDeny,
            },
            Rules = [],
        };

        PolicyDraftDocument clone = PolicyEditorMapper.CloneDraftDocument(source);
        clone.Metadata.Description = "mutated after clone";

        Assert.NotEqual("mutated after clone", source.Metadata.Description);
        Assert.Equal(source.Metadata.Id, clone.Metadata.Id);
    }
}
