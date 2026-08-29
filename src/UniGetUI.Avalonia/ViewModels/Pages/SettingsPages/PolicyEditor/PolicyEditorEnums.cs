namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Tri-state representation of a boolean policy match criterion. The wire format represents these
/// as a <c>List&lt;bool&gt;</c> (see <c>PolicyMatch.Interactive</c> and its siblings), where an empty
/// list means "don't care" and a single-element list pins the criterion to that value. The editor
/// only ever produces these three states. The shared contract rejects lists with more than one item.
/// </summary>
public enum TriState
{
    Omitted,
    False,
    True,
}

/// <summary>
/// Which editing surface currently owns the source of truth for a <see cref="PolicyEditorSession"/>.
/// </summary>
public enum PolicyEditorMode
{
    /// <summary>The structured <see cref="PolicyEditorSession.Draft"/> is authoritative.</summary>
    Structured,

    /// <summary>The free-form <see cref="PolicyEditorSession.RawBuffer"/> text is authoritative.</summary>
    Raw,
}

/// <summary>
/// The operation a <see cref="PolicyEditorSession"/> was opened to perform. This reflects user intent
/// at session-open time; it is distinct from the state-derived retry operation computed by
/// <see cref="PolicyEditorRetryResolver"/> when a save is attempted against a possibly-stale origin.
/// </summary>
public enum PolicyEditorOperationKind
{
    Update,
    ReplaceIdentity,
    Create,
    Repair,
}

/// <summary>
/// Severity of a <see cref="PolicyValidationFinding"/>, as reported by the external (Agent-side)
/// semantic validator.
/// </summary>
public enum PolicyValidationSeverity
{
    Info,
    Warning,
    Error,
}

public enum PolicyEditorConfirmationKind
{
    Warnings,
    ReplaceIdentity,
    Create,
    Repair,
    ConfirmOverwrite,
    DiscardChanges,
}
