using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>A structural failure that prevented raw JSON text from becoming a structured draft.</summary>
public sealed record PolicyEditorSyntaxError(string Message, string Pointer);

/// <summary>
/// The two seams between the editor's raw-text surface and its structured surface:
/// <see cref="TryParseStrict"/> (raw -&gt; structured, only for syntactically and structurally valid
/// text) and <see cref="ToCanonicalRaw"/> (structured -&gt; raw, always succeeds). Parsing is strict and
/// fails closed: invalid JSON, JSON that doesn't match the wire shape, or JSON that disagrees with the
/// fixed schema/policy-type/rule-precedence contract (see <see cref="PolicyEditorPolicyContract"/>) is
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
        draft = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = new PolicyEditorSyntaxError("The document is empty.", "");
            return false;
        }

        PolicyDraftDocument? document;
        try
        {
            if (!TryCheckDraftSchema(rawJson, out error))
            {
                return false;
            }

            document = PolicySerializer.DeserializePolicyDraftDocumentStrict(rawJson);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            error = new PolicyEditorSyntaxError(ex.Message, PointerFromException(ex));
            return false;
        }

        if (document is null)
        {
            error = new PolicyEditorSyntaxError("The document could not be parsed.", "");
            return false;
        }

        if (!TryCheckFixedContract(document, out error))
        {
            return false;
        }

        draft = PolicyEditorMapper.ToDraft(document);
        return true;
    }

    private static bool TryCheckDraftSchema(string rawJson, out PolicyEditorSyntaxError? error)
    {
        using JsonDocument json = JsonDocument.Parse(rawJson);
        if (json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("$schema", out JsonElement schema)
            && schema.ValueKind == JsonValueKind.String
            && !string.Equals(schema.GetString(), PolicyEditorPolicyContract.DraftSchema, StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                $"Unsupported schema '{schema.GetString()}'. Expected '{PolicyEditorPolicyContract.DraftSchema}'.",
                "/$schema");
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

    private static bool TryCheckFixedContract(PolicyDraftDocument document, out PolicyEditorSyntaxError? error)
    {
        if (!string.Equals(document.Schema, PolicyEditorPolicyContract.DraftSchema, StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                $"Unsupported schema '{document.Schema}'. Expected '{PolicyEditorPolicyContract.DraftSchema}'.",
                "/$schema");
            return false;
        }

        if (!string.Equals(document.PolicyType, PolicyEditorPolicyContract.PolicyType, StringComparison.Ordinal))
        {
            error = new PolicyEditorSyntaxError(
                $"Unsupported policyType '{document.PolicyType}'. Expected '{PolicyEditorPolicyContract.PolicyType}'.",
                "/policyType");
            return false;
        }

        if (document.Enforcement is null)
        {
            error = new PolicyEditorSyntaxError("Missing enforcement block.", "/enforcement");
            return false;
        }

        if (document.Enforcement.RulePrecedence != PolicyEditorPolicyContract.FixedRulePrecedence)
        {
            error = new PolicyEditorSyntaxError(
                $"Unsupported rulePrecedence '{document.Enforcement.RulePrecedence}'. Expected '{PolicyEditorPolicyContract.FixedRulePrecedence}'.",
                "/enforcement/rulePrecedence");
            return false;
        }

        if (document.Metadata is null)
        {
            error = new PolicyEditorSyntaxError("Missing metadata block.", "/metadata");
            return false;
        }

        error = null;
        return true;
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
