// Pinned lock entries (extension; not in the reference CLI).
//
// `skills add --pin <ref>` records `"pinned": true` right after `"ref"` in the
// lock entry. The reference CLI ignores the extra field.

using System.Text.Json.Nodes;

namespace Skills;

internal enum PinActionKind
{
    /// Not pinned: check it as usual.
    Check,

    /// Pinned; skip it and list it in the pinned notice.
    Skip,

    /// Pinned with `--force`: reinstall at the pinned ref (`--pin &lt;ref&gt;`).
    Force,

    /// Pinned with `--unpin`: reinstall from the default branch.
    Unpin,
}

internal sealed record PinAction(PinActionKind Kind, string? Ref = null);

internal static class Pinning
{
    /// The value of `--pin` that resolves the newest release.
    public const string PinLatest = "latest";

    /// The pinned ref of a lock entry: `pinned` is `true` and `ref` is a
    /// non-empty string.
    public static string? PinnedRef(JsonNode? entry) =>
        Json.AsBool(Json.Get(entry, "pinned")) == true ? Json.NonEmpty(entry, "ref") : null;

    public static bool IsPinnedEntry(JsonNode? entry) => PinnedRef(entry) != null;

    /// A copy of the entry without `ref` and `pinned` (reinstall from the
    /// source's default branch).
    public static JsonNode? UnpinnedEntry(JsonNode? entry)
    {
        var e = entry?.DeepClone();
        if (e is JsonObject o)
        {
            o.Remove("ref");
            o.Remove("pinned");
        }
        return e;
    }

    public static PinAction GetPinAction(JsonNode? entry, bool force, bool unpin) => PinnedRef(entry) switch
    {
        null => new PinAction(PinActionKind.Check),
        _ when unpin => new PinAction(PinActionKind.Unpin),
        var r when force => new PinAction(PinActionKind.Force, r),
        var r => new PinAction(PinActionKind.Skip, r),
    };

    /// Validate `skills add --pin &lt;pin&gt;` against the parsed source's kind and
    /// ref; returns the error message, or null when valid.
    public static string? CheckPin(string kind, string? sourceRef, string pin)
    {
        if (kind is not ("github" or "gitlab" or "git")) return "--pin is only supported for GitHub, GitLab and Git sources";
        if (sourceRef != null && sourceRef != pin)
            return $"Conflicting refs: the source selects \"{sourceRef}\" but --pin selects \"{pin}\". Provide one ref.";
        if (pin == PinLatest && kind != "github") return "--pin latest is only supported for GitHub sources";
        return null;
    }
}
