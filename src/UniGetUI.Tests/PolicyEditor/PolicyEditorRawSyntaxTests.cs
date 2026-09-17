using System.Text.Json.Nodes;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyEditorRawSyntaxTests
{
    [Fact]
    public void TryParseStrict_ValidCanonicalRaw_Succeeds()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule-a");
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("id-1", parsed!.Metadata.Id);
        Assert.Contains(parsed.Rules, rule => rule.Id == "rule-a");
    }

    [Fact]
    public void RawProjection_PreservesRuleIdentityBeforeEffectiveOrdering()
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule later = PolicyRuleFactory.CreateBlank("later");
        later.Priority = 20;
        later.Decision = Decision.Allow;
        later.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        later.Match.ExactPackageIdentifiers.Add("Later.App");
        PolicyEditorDraftRule first = PolicyRuleFactory.CreateBlank("first");
        first.Priority = 10;
        first.Decision = Decision.Deny;
        first.Match.PackageIdentifierMode = PackageIdentifierMode.Exact;
        first.Match.ExactPackageIdentifiers.Add("First.App");
        draft.Rules.Add(later);
        draft.Rules.Add(first);
        string raw = PolicyEditorRawSyntax.ToCanonicalRawPreservingPriorities(draft);
        JsonNode root = JsonNode.Parse(raw)!;
        root["Rules"]![0]!["Id"] = "Later invalid";
        root["Rules"]![1]!["Id"] = "First invalid";

        Assert.True(PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        PolicyEditorMapper.NormalizeStructuredRuleOrder(parsed!.Rules);

        Assert.Null(error);
        Assert.Equal(["First invalid", "Later invalid"], parsed.Rules.Select(rule => rule.Id));
        Assert.Equal(
            ["First.App", "Later.App"],
            parsed.Rules.Select(rule => Assert.Single(rule.Match.ExactPackageIdentifiers)));
    }

    [Fact]
    public void RawCanonicalization_PreservesAuthoredPriorityUntilStructuredProjection()
    {
        PolicyEditorDraftDocument draft =
            PolicyEditorTemplates.CreateNew("policy", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule");
        rule.Priority = 20;
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);

        string rawCanonical =
            PolicyEditorRawSyntax.ToCanonicalRawPreservingPriorities(draft);
        string structuredCanonical = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        Assert.Contains("\"Priority\": 20", rawCanonical);
        Assert.Contains("\"Priority\": 0", structuredCanonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseStrict_EmptyText_FailsWithoutTouchingOutput(string? text)
    {
        bool ok = PolicyEditorRawSyntax.TryParseStrict(text, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.EmptyDocument, error!.Kind);
    }

    [Fact]
    public void TryParseStrict_MalformedJson_UsesStableKindWithoutExceptionText()
    {
        string malformed = "{ this is not valid json ";

        bool ok = PolicyEditorRawSyntax.TryParseStrict(malformed, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidJson, error!.Kind);
        Assert.Equal("", error.Pointer);
    }

    [Fact]
    public void TryParseStrict_DuplicateProperty_IsRejectedWithoutNormalization()
    {
        string raw = PolicySerializer.Serialize(BuildValidPackageDraft());
        string duplicate = raw.Replace(
            "\"Publisher\": \"Contoso\"",
            "\"Publisher\": \"Contoso\", \"Publisher\": \"Fabrikam\"",
            StringComparison.Ordinal);
        Assert.NotEqual(raw, duplicate);

        Assert.False(PolicyEditorRawSyntax.TryParseStrict(
            duplicate,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("Sources", "[\"community\"]")]
    [InlineData("PackageNames", "[\"Contoso App\"]")]
    [InlineData("Versions", "[\"1.0.0\"]")]
    [InlineData("Elevation", "[\"Standard\"]")]
    [InlineData("Interactive", "[true]")]
    public void TryParseStrict_RemovedMatchShapes_AreRejected(
        string property,
        string value)
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["Rules"] = new JsonArray(JsonNode.Parse(
            $$"""
            {
              "Id": "rule",
              "Enabled": false,
              "Priority": 0,
              "Decision": "Deny",
              "Match": {
                "Operations": ["Install"],
                "{{property}}": {{value}}
              }
            }
            """));

        Assert.False(PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error));
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidPolicyDraft, error!.Kind);
    }

    private static PolicyDraftDocument BuildValidPackageDraft(string id = "contoso-policy")
    {
        return new PolicyDraftDocument
        {
            PolicyFormatVersion = PolicyFormatVersion.Parse("1.2.3"),
            Metadata = new PolicyDraftMetadata { Id = id, Publisher = "Contoso" },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Decision.Deny,
            },
            Rules = [],
        };
    }

    [Fact]
    public void TryParseStrict_LegacySchemaField_FailsClosedWithPrecisePointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["$schema"] = "https://example.com/wrong-schema.json";
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.LegacySchemaField, error!.Kind);
        Assert.Equal("/$schema", error!.Pointer);
    }

    [Fact]
    public void TryParseStrict_LegacyPolicyVersionField_FailsClosedWithPrecisePointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyVersion"] = "1.0.0";
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            raw,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.LegacyPolicyVersionField, error!.Kind);
        Assert.Equal("/PolicyVersion", error!.Pointer);
    }

    [Fact]
    public void TryParseStrict_MissingPolicyFormatVersion_FailsClosedWithPrecisePointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        Assert.True(root.AsObject().Remove("PolicyFormatVersion"));

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.MissingPolicyFormatVersion, error!.Kind);
        Assert.Equal("/PolicyFormatVersion", error.Pointer);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-beta")]
    public void TryParseStrict_MalformedPolicyFormatVersion_FailsClosedWithPrecisePointer(string version)
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyFormatVersion"] = version;

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidPolicyFormatVersion, error!.Kind);
        Assert.Equal("/PolicyFormatVersion", error.Pointer);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("2.0.0")]
    public void TryParseStrict_UnsupportedPolicyFormatVersion_FailsClosedWithPrecisePointer(string version)
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyFormatVersion"] = version;

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.UnsupportedPolicyFormatVersion, error!.Kind);
        Assert.Equal("/PolicyFormatVersion", error.Pointer);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.27.18446744073709551615")]
    public void TryParseStrict_CompatibleMajorOneVersion_IsPreserved(string version)
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyFormatVersion"] = version;

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(version, parsed!.PolicyFormatVersion.Value);
    }

    [Fact]
    public void TryParseStrict_RemovedPolicyTypeField_IsRejected()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyType"] = "SomeOtherPolicy";
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidPolicyDraft, error!.Kind);
    }

    [Fact]
    public void TryParseStrict_MissingEnforcement_UsesCanonicalPointer()
    {
        JsonNode root = JsonNode.Parse(
            PolicyEditorRawSyntax.ToCanonicalRaw(
                PolicyEditorTemplates.CreateNew("id-1", "Contoso")))!;
        root.AsObject().Remove("Enforcement");

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.MissingEnforcement, error!.Kind);
        Assert.Equal("/Enforcement", error.Pointer);
    }

    [Fact]
    public void TryParseStrict_RemovedRulePrecedenceField_IsRejected()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        JsonNode root = JsonNode.Parse(raw)!;
        root["Enforcement"]!["RulePrecedence"] = "DenyOnly";
        string tampered = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            tampered,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.InvalidPolicyDraft, error!.Kind);
    }

    [Fact]
    public void TryParseStrict_MissingMetadata_UsesCanonicalPointer()
    {
        JsonNode root = JsonNode.Parse(
            PolicyEditorRawSyntax.ToCanonicalRaw(
                PolicyEditorTemplates.CreateNew("id-1", "Contoso")))!;
        root.AsObject().Remove("Metadata");

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            root.ToJsonString(),
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.MissingMetadata, error!.Kind);
        Assert.Equal("/Metadata", error.Pointer);
    }

    [Fact]
    public void ToCanonicalRaw_ProducesTextThatRoundTripsThroughTryParseStrict()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("round-trip-id", "Contoso");
        draft.Enforcement.DefaultDecision = Decision.Allow;
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule-a");
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);

        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out _);

        Assert.True(ok);
        Assert.Equal(Decision.Allow, parsed!.Enforcement.DefaultDecision);
    }

    [Fact]
    public void TryParseStrict_NeverMutatesInputBuffer_OnFailure()
    {
        // This documents the "retains invalid text" contract at the seam level: the strict parser
        // never returns a partially-built draft, and the error always carries a stable kind so the
        // caller (PolicyEditorSession.SetRawBuffer/TryParseRaw) can safely leave the raw buffer as-is.
        string invalid = "{ \"schema\": 1, }";

        bool ok = PolicyEditorRawSyntax.TryParseStrict(invalid, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseStrictWithElement_ReturnsTheSameParsedRootForAuthoritativeValidation()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        bool ok = PolicyEditorRawSyntax.TryParseStrictWithElement(
            raw,
            out PolicyEditorDraftDocument? parsed,
            out System.Text.Json.JsonElement element,
            out PolicyEditorSyntaxError? error);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.Equal("id-1", element.GetProperty("Metadata").GetProperty("Id").GetString());
    }

    // ---- Correction #1: raw mode is PolicyDraftDocument-shaped; Revision/PublishedAt are absent from
    // canonical output and rejected as unknown fields on the way in. --------------------------------

    [Fact]
    public void ToCanonicalRaw_NeverEmitsRevisionOrPublishedAt()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("rule-a");
        rule.Match.Operations.Add(Operation.Install);
        draft.Rules.Add(rule);

        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        Assert.DoesNotContain("revision", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishedAt", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToCanonicalRaw_HasNoParametersToCarryRevisionOrPublishedAt()
    {
        // The correction requires these arguments be removed entirely, not merely defaulted: this
        // documents that contract via reflection over the public method signature.
        System.Reflection.MethodInfo method = typeof(PolicyEditorRawSyntax).GetMethod(nameof(PolicyEditorRawSyntax.ToCanonicalRaw))!;
        System.Reflection.ParameterInfo[] parameters = method.GetParameters();

        Assert.Single(parameters);
        Assert.DoesNotContain(parameters, p => p.Name is "revision" or "publishedAt");
    }

    [Fact]
    public void TryParseStrict_RejectsInjectedRevisionField_AsUnknown()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        JsonNode root = JsonNode.Parse(raw)!;
        root["Metadata"]!["Revision"] = 3;
        string tampered = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(tampered, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseStrict_RejectsInjectedPublishedAtField_AsUnknown()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        JsonNode root = JsonNode.Parse(raw)!;
        root["Metadata"]!["PublishedAt"] = "2026-01-01T00:00:00Z";
        string tampered = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(tampered, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseStrict_RejectsTopLevelRevisionField_AsUnknown()
    {
        PolicyEditorDraftDocument draft = PolicyEditorTemplates.CreateNew("id-1", "Contoso");
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);
        JsonNode root = JsonNode.Parse(raw)!;
        root["Revision"] = 3;
        string tampered = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(tampered, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void ToSharedDraft_NeverProducesAPolicyDocument_OnlyThePackageDraftShape()
    {
        // Reflection-level contract check for correction #1: PolicyEditorRawSyntax parses/serializes
        // PolicyDraftDocument, never PolicyDocument.
        System.Reflection.MethodInfo tryParse = typeof(PolicyEditorRawSyntax).GetMethod(
            nameof(PolicyEditorRawSyntax.TryParseStrict),
            [
                typeof(string),
                typeof(PolicyEditorDraftDocument).MakeByRefType(),
                typeof(PolicyEditorSyntaxError).MakeByRefType(),
            ])!;
        Assert.Equal(typeof(PolicyEditorDraftDocument).MakeByRefType(), tryParse.GetParameters()[1].ParameterType);
    }
}
