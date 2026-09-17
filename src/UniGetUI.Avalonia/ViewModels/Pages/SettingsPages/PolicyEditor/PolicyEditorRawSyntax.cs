using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>Stable, localizable classification of a raw policy-draft parsing failure.</summary>
public enum PolicyEditorSyntaxErrorKind
{
    EmptyDocument,
    InvalidJson,
    InvalidPolicyDraft,
    LegacySchemaField,
    LegacyPolicyVersionField,
    MissingPolicyFormatVersion,
    InvalidPolicyFormatVersion,
    UnsupportedPolicyFormatVersion,
    UnsupportedPolicyType,
    MissingEnforcement,
    UnsupportedRulePrecedence,
    MissingMetadata,
}

/// <summary>A structural failure that prevented raw JSON text from becoming a structured draft.</summary>
public sealed record PolicyEditorSyntaxError(PolicyEditorSyntaxErrorKind Kind, string Pointer);

/// <summary>
/// The two seams between the editor's raw-text surface and its structured surface:
/// <see cref="TryParseStrict"/> (raw -&gt; structured, only for syntactically and structurally valid
/// text) and <see cref="ToCanonicalRaw"/> (structured -&gt; raw, always succeeds). Parsing is strict and
/// fails closed: invalid JSON, JSON that doesn't match the wire shape, or JSON that disagrees with the
/// fixed policy-type/rule-precedence contract (see <see cref="PolicyEditorPolicyContract"/>) is
/// rejected outright with a <see cref="PolicyEditorSyntaxError"/> and the original raw text is left
/// completely untouched by the caller (this class never mutates or truncates input). Agent-side
/// semantic validation (e.g. whether specific values make operational sense) is intentionally out of
/// scope here — it is external, see <see cref="IPolicyValidationClient"/>.
/// </summary>
public static partial class PolicyEditorRawSyntax
{
    public static bool TryParseStrict(
        string? rawJson,
        out PolicyEditorDraftDocument? draft,
        out PolicyEditorSyntaxError? error)
    {
        return TryParseStrictWithElement(rawJson, out draft, out _, out error);
    }

    public static bool TryParseStrictWithElement(
        string? rawJson,
        out PolicyEditorDraftDocument? draft,
        out JsonElement element,
        out PolicyEditorSyntaxError? error)
    {
        draft = null;
        element = default;
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = new PolicyEditorSyntaxError(PolicyEditorSyntaxErrorKind.EmptyDocument, "");
            return false;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(rawJson);
            element = json.RootElement.Clone();
            if (!TryCheckDraftContractFields(json.RootElement, out error))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            error = new PolicyEditorSyntaxError(PolicyEditorSyntaxErrorKind.InvalidJson, "");
            return false;
        }

        PolicyDraftDocument? document;
        try
        {
            document = PolicySerializer.DeserializePolicyDraftDocumentStrict(
                MakeResourceIdsProjectable(rawJson));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.InvalidPolicyDraft,
                PointerFromException(ex));
            return false;
        }

        if (document is null)
        {
            error = new PolicyEditorSyntaxError(PolicyEditorSyntaxErrorKind.InvalidPolicyDraft, "");
            return false;
        }

        if (!TryCheckFixedContract(document, out error))
        {
            return false;
        }

        draft = PolicyEditorMapper.ToDraftPreservingRuleOrder(document);
        RestoreAuthoredResourceIds(element, draft);
        return true;
    }

    private static string MakeResourceIdsProjectable(string rawJson)
    {
        // The package deserializer validates ResourceId values while materializing the document.
        // Substitute only for shape projection, then restore the authored strings for inline editing.
        JsonNode? root = JsonNode.Parse(rawJson);
        if (root is not JsonObject policy)
        {
            return rawJson;
        }

        if (policy["Metadata"] is JsonObject metadata)
        {
            ReplaceInvalidResourceId(metadata, "Id", "policy");
        }

        if (policy["Rules"] is JsonArray rules)
        {
            for (int index = 0; index < rules.Count; index++)
            {
                if (rules[index] is JsonObject rule)
                {
                    ReplaceInvalidResourceId(rule, "Id", $"rule-{index + 1}");
                }
            }
        }

        return root.ToJsonString();
    }

    private static void ReplaceInvalidResourceId(
        JsonObject owner,
        string propertyName,
        string replacement)
    {
        if (owner[propertyName] is JsonValue value
            && value.TryGetValue(out string? authored)
            && !PolicyEditorTemplates.IsValidResourceId(authored))
        {
            owner[propertyName] = replacement;
        }
    }

    private static void RestoreAuthoredResourceIds(
        JsonElement root,
        PolicyEditorDraftDocument draft)
    {
        if (root.TryGetProperty("Metadata", out JsonElement metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("Id", out JsonElement policyId)
            && policyId.ValueKind == JsonValueKind.String)
        {
            draft.Metadata.Id = policyId.GetString() ?? "";
        }

        if (!root.TryGetProperty("Rules", out JsonElement rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement rule in rules.EnumerateArray())
        {
            if (index >= draft.Rules.Count)
            {
                break;
            }

            if (rule.ValueKind == JsonValueKind.Object
                && rule.TryGetProperty("Id", out JsonElement ruleId)
                && ruleId.ValueKind == JsonValueKind.String)
            {
                draft.Rules[index].Id = ruleId.GetString() ?? "";
            }

            index++;
        }
    }

    private static bool TryCheckDraftContractFields(
        JsonElement root,
        out PolicyEditorSyntaxError? error)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("$schema", out _))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.LegacySchemaField,
                "/$schema");
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("PolicyVersion", out _))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.LegacyPolicyVersionField,
                "/PolicyVersion");
            return false;
        }

        JsonElement policyFormatVersion = default;
        if (root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("PolicyFormatVersion", out policyFormatVersion))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.MissingPolicyFormatVersion,
                "/PolicyFormatVersion");
            return false;
        }

        if (policyFormatVersion.ValueKind == JsonValueKind.String
            && !TryParsePolicyFormatVersion(
                policyFormatVersion.GetString(),
                out PolicyEditorSyntaxErrorKind? formatError))
        {
            error = new PolicyEditorSyntaxError(
                formatError!.Value,
                "/PolicyFormatVersion");
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("PolicyType", out JsonElement policyType)
            && policyType.ValueKind == JsonValueKind.String
            && !string.Equals(
                policyType.GetString(),
                PolicyEditorPolicyContract.PolicyType,
                StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.UnsupportedPolicyType,
                "/PolicyType");
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("Enforcement", out _))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.MissingEnforcement,
                "/Enforcement");
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Enforcement", out JsonElement enforcement)
            && enforcement.ValueKind == JsonValueKind.Object
            && enforcement.TryGetProperty("RulePrecedence", out JsonElement precedence)
            && precedence.ValueKind == JsonValueKind.String
            && !string.Equals(
                precedence.GetString(),
                PolicyEditorPolicyContract.FixedRulePrecedence.ToString(),
                StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.UnsupportedRulePrecedence,
                "/Enforcement/RulePrecedence");
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("Metadata", out _))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.MissingMetadata,
                "/Metadata");
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Serializes exactly the editable draft shape. Server-managed metadata is never emitted.
    /// </summary>
    public static string ToCanonicalRaw(PolicyEditorDraftDocument draft) =>
        PolicySerializer.Serialize(PolicyEditorMapper.ToSharedDraft(draft));

    internal static string ToCanonicalRawPreservingPriorities(
        PolicyEditorDraftDocument draft) =>
        PolicySerializer.Serialize(
            PolicyEditorMapper.ToSharedDraftPreservingPriorities(draft));

    private static bool TryCheckFixedContract(PolicyDraftDocument document, out PolicyEditorSyntaxError? error)
    {
        if (!string.Equals(document.PolicyType, PolicyEditorPolicyContract.PolicyType, StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.UnsupportedPolicyType,
                "/PolicyType");
            return false;
        }

        if (document.Enforcement is null)
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.MissingEnforcement,
                "/Enforcement");
            return false;
        }

        if (document.Enforcement.RulePrecedence != PolicyEditorPolicyContract.FixedRulePrecedence)
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.UnsupportedRulePrecedence,
                "/Enforcement/RulePrecedence");
            return false;
        }

        if (document.Metadata is null)
        {
            error = new PolicyEditorSyntaxError(
                PolicyEditorSyntaxErrorKind.MissingMetadata,
                "/Metadata");
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParsePolicyFormatVersion(
        string? value,
        out PolicyEditorSyntaxErrorKind? error)
    {
        try
        {
            PolicyFormatVersion.Parse(value!);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = PolicyEditorSyntaxErrorKind.InvalidPolicyFormatVersion;
            return false;
        }
        catch (NotSupportedException)
        {
            error = PolicyEditorSyntaxErrorKind.UnsupportedPolicyFormatVersion;
            return false;
        }
        catch (ArgumentNullException)
        {
            error = PolicyEditorSyntaxErrorKind.InvalidPolicyFormatVersion;
            return false;
        }
    }

    private static string PointerFromException(Exception ex) =>
        ex is JsonException { Path: { Length: > 0 } path } ? ConvertJsonPathToPointer(path) : "";

    /// <summary>Converts a System.Text.Json exception path (e.g. <c>$.rules[0].match.versions[1]</c>)
    /// into an RFC 6901 JSON Pointer (e.g. <c>/rules/0/match/versions/1</c>).</summary>
    private static string ConvertJsonPathToPointer(string path)
    {
        StringBuilder builder = new();
        foreach (Match match in JsonPathSegment().Matches(path))
        {
            string segment = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            builder.Append('/').Append(segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\.([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPathSegment();
}
