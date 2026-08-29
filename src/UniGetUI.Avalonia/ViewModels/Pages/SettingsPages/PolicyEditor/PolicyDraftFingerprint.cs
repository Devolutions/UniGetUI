namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// A content-based fingerprint of a <see cref="PolicyEditorDraftDocument"/>, used to detect whether the
/// draft has actually changed (dirty tracking) and to tie a warning acknowledgement to the exact
/// draft state it was granted against (see <see cref="PolicyEditorWarningAcknowledgement"/>).
/// Computed from the canonical draft JSON, which omits server-managed metadata.
/// </summary>
public readonly struct PolicyEditorDraftFingerprint : IEquatable<PolicyEditorDraftFingerprint>
{
    private readonly string _canonicalJson;

    private PolicyEditorDraftFingerprint(string canonicalJson)
    {
        _canonicalJson = canonicalJson;
    }

    public static PolicyEditorDraftFingerprint Compute(PolicyEditorDraftDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new PolicyEditorDraftFingerprint(PolicyEditorRawSyntax.ToCanonicalRaw(draft));
    }

    public bool Equals(PolicyEditorDraftFingerprint other) =>
        string.Equals(_canonicalJson, other._canonicalJson, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PolicyEditorDraftFingerprint other && Equals(other);

    public override int GetHashCode() =>
        _canonicalJson is null ? 0 : StringComparer.Ordinal.GetHashCode(_canonicalJson);

    public static bool operator ==(PolicyEditorDraftFingerprint left, PolicyEditorDraftFingerprint right) => left.Equals(right);

    public static bool operator !=(PolicyEditorDraftFingerprint left, PolicyEditorDraftFingerprint right) => !left.Equals(right);
}
