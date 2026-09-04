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
        draft.Rules.Add(PolicyRuleFactory.CreateBlank("rule-a"));
        string raw = PolicyEditorRawSyntax.ToCanonicalRaw(draft);

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("id-1", parsed!.Metadata.Id);
        Assert.Single(parsed.Rules);
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

    private static PolicyDraftDocument BuildValidPackageDraft(string id = "contoso-policy")
    {
        return new PolicyDraftDocument
        {
            Schema = PolicyEditorPolicyContract.DraftSchema,
            PolicyVersion = "1.2.3",
            PolicyType = PolicyEditorPolicyContract.PolicyType,
            Metadata = new PolicyDraftMetadata { Id = id, Publisher = "Contoso" },
            Enforcement = new PolicyEnforcement
            {
                DefaultDecision = Decision.Deny,
                RulePrecedence = RulePrecedence.PriorityThenDeny,
            },
            Rules = [],
        };
    }

    [Fact]
    public void TryParseStrict_WrongSchema_FailsClosedWithSchemaPointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["$schema"] = "https://example.com/wrong-schema.json";
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.UnsupportedSchema, error!.Kind);
        Assert.Equal("/$schema", error!.Pointer);
    }

    [Fact]
    public void TryParseStrict_CommittedSchemaForDraft_FailsClosedWithSchemaPointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["$schema"] = SchemaUris.Policy;
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(
            raw,
            out PolicyEditorDraftDocument? parsed,
            out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.UnsupportedSchema, error!.Kind);
        Assert.Equal("/$schema", error!.Pointer);
    }

    [Fact]
    public void TryParseStrict_WrongPolicyType_FailsClosedWithPolicyTypePointer()
    {
        JsonNode root = JsonNode.Parse(PolicySerializer.Serialize(BuildValidPackageDraft()))!;
        root["PolicyType"] = "SomeOtherPolicy";
        string raw = root.ToJsonString();

        bool ok = PolicyEditorRawSyntax.TryParseStrict(raw, out PolicyEditorDraftDocument? parsed, out PolicyEditorSyntaxError? error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(PolicyEditorSyntaxErrorKind.UnsupportedPolicyType, error!.Kind);
        Assert.Equal("/PolicyType", error.Pointer);
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
    public void TryParseStrict_WrongRulePrecedence_FailsClosedWithRulePrecedencePointer()
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
        Assert.Equal(PolicyEditorSyntaxErrorKind.UnsupportedRulePrecedence, error!.Kind);
        Assert.Equal("/Enforcement/RulePrecedence", error.Pointer);
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
        draft.Rules.Add(PolicyRuleFactory.CreateBlank("rule-a"));

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
        draft.Rules.Add(PolicyRuleFactory.CreateBlank("rule-a"));

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
