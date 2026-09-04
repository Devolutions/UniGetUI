using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Editable projection of <see cref="PolicyDocument"/>. Deliberately excludes
/// <see cref="PolicyMetadata.Revision"/> and <see cref="PolicyMetadata.PublishedAt"/> (server/write-path
/// assigned bookkeeping, never user-edited) and exposes <see cref="Schema"/>/<see cref="PolicyType"/> as
/// fixed, read-only values instead of editable fields: see <see cref="PolicyEditorPolicyContract"/>.
/// Use <see cref="PolicyEditorMapper"/> to convert to/from the wire model, and <see cref="Clone"/> for a
/// full, independent deep copy (used for snapshots, undo points, and conflict capture).
/// </summary>
public sealed class PolicyEditorDraftDocument
{
    public string Schema => PolicyEditorPolicyContract.DraftSchema;

    public string PolicyType => PolicyEditorPolicyContract.PolicyType;

    public required string PolicyVersion { get; set; }

    public required PolicyEditorDraftMetadata Metadata { get; set; }

    public required PolicyEditorDraftEnforcement Enforcement { get; set; }

    public List<PolicyEditorDraftRule> Rules { get; set; } = [];

    public PolicyEditorDraftDocument Clone() => new()
    {
        PolicyVersion = PolicyVersion,
        Metadata = Metadata.Clone(),
        Enforcement = Enforcement.Clone(),
        Rules = Rules.Select(rule => rule.Clone()).ToList(),
    };
}

/// <summary>Editable projection of <see cref="PolicyMetadata"/>, minus Revision/PublishedAt.</summary>
public sealed class PolicyEditorDraftMetadata
{
    public required string Id { get; set; }

    public required string Publisher { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }

    public DateTimeOffset? ValidUntil { get; set; }

    public string? Description { get; set; }

    public string? SupportUrl { get; set; }

    public PolicyEditorDraftMetadata Clone() => new()
    {
        Id = Id,
        Publisher = Publisher,
        ValidFrom = ValidFrom,
        ValidUntil = ValidUntil,
        Description = Description,
        SupportUrl = SupportUrl,
    };
}

/// <summary>
/// Editable projection of <see cref="PolicyEnforcement"/>. <see cref="RulePrecedence"/> is fixed
/// (see <see cref="PolicyEditorPolicyContract"/>); only <see cref="DefaultDecision"/> and
/// <see cref="AuditMode"/> are user-editable.
/// </summary>
public sealed class PolicyEditorDraftEnforcement
{
    public required Decision DefaultDecision { get; set; }

    public RulePrecedence RulePrecedence => PolicyEditorPolicyContract.FixedRulePrecedence;

    public bool? AuditMode { get; set; }

    public PolicyEditorDraftEnforcement Clone() => new()
    {
        DefaultDecision = DefaultDecision,
        AuditMode = AuditMode,
    };
}

/// <summary>Editable projection of a single <see cref="PolicyRule"/>.</summary>
public sealed class PolicyEditorDraftRule
{
    public required string Id { get; set; }

    public bool Enabled { get; set; } = true;

    public uint Priority { get; set; }

    public required Decision Decision { get; set; }

    public string? Reason { get; set; }

    public required PolicyEditorDraftMatch Match { get; set; }

    public PolicyEditorDraftConstraints? Constraints { get; set; }

    /// <summary>Deep copy preserving the same rule identity (<see cref="Id"/> included).</summary>
    public PolicyEditorDraftRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Priority = Priority,
        Decision = Decision,
        Reason = Reason,
        Match = Match.Clone(),
        Constraints = Constraints?.Clone(),
    };

    /// <summary>Deep copy under a new rule identity, for use by the "duplicate rule" operation.</summary>
    public PolicyEditorDraftRule CloneWithNewId(string newId)
    {
        PolicyEditorDraftRule clone = Clone();
        clone.Id = newId;
        return clone;
    }
}

/// <summary>
/// Editable projection of <see cref="PolicyMatch"/>. The eight boolean criteria are exposed as
/// <see cref="TriState"/> instead of <c>List&lt;bool&gt;</c>; see <see cref="TriState"/> and
/// <see cref="PolicyEditorMapper"/> for the conversion rules.
/// </summary>
public sealed class PolicyEditorDraftMatch
{
    public List<Operation> Operations { get; set; } = [];

    public List<ManagerName> Managers { get; set; } = [];

    public List<string> Sources { get; set; } = [];

    public List<string> PackageIdentifiers { get; set; } = [];

    public List<string> PackageNames { get; set; } = [];

    public List<string> Versions { get; set; } = [];

    public PolicyEditorDraftVersionRange? VersionRange { get; set; }

    public List<Scope> Scopes { get; set; } = [];

    public List<Architecture> Architectures { get; set; } = [];

    public List<Elevation> Elevation { get; set; } = [];

    public TriState Interactive { get; set; }

    public TriState SkipHashCheck { get; set; }

    public TriState PreRelease { get; set; }

    public TriState HasCustomParameters { get; set; }

    public TriState HasCustomInstallLocation { get; set; }

    public TriState HasPrePostCommands { get; set; }

    public TriState HasKillBeforeOperation { get; set; }

    public TriState HasUninstallPrevious { get; set; }

    public PolicyEditorDraftMatch Clone() => new()
    {
        Operations = [.. Operations],
        Managers = [.. Managers],
        Sources = [.. Sources],
        PackageIdentifiers = [.. PackageIdentifiers],
        PackageNames = [.. PackageNames],
        Versions = [.. Versions],
        VersionRange = VersionRange?.Clone(),
        Scopes = [.. Scopes],
        Architectures = [.. Architectures],
        Elevation = [.. Elevation],
        Interactive = Interactive,
        SkipHashCheck = SkipHashCheck,
        PreRelease = PreRelease,
        HasCustomParameters = HasCustomParameters,
        HasCustomInstallLocation = HasCustomInstallLocation,
        HasPrePostCommands = HasPrePostCommands,
        HasKillBeforeOperation = HasKillBeforeOperation,
        HasUninstallPrevious = HasUninstallPrevious,
    };
}

/// <summary>Editable projection of <see cref="VersionRange"/>.</summary>
public sealed class PolicyEditorDraftVersionRange
{
    public string? MinVersion { get; set; }

    public string? MaxVersion { get; set; }

    public bool IncludePrerelease { get; set; }

    public PolicyEditorDraftVersionRange Clone() => new()
    {
        MinVersion = MinVersion,
        MaxVersion = MaxVersion,
        IncludePrerelease = IncludePrerelease,
    };
}

/// <summary>Editable projection of <see cref="PolicyConstraints"/> (plain booleans, no tri-state).</summary>
public sealed class PolicyEditorDraftConstraints
{
    public bool AllowInteractive { get; set; }

    public bool AllowSkipHashCheck { get; set; }

    public bool AllowPreRelease { get; set; }

    public bool AllowCustomInstallLocation { get; set; }

    public List<string> AllowedInstallLocationPatterns { get; set; } = [];

    public bool AllowCustomParameters { get; set; }

    public List<string> AllowedCustomParameters { get; set; } = [];

    public List<string> AllowedCustomParameterPatterns { get; set; } = [];

    public List<string> DeniedCustomParameters { get; set; } = [];

    public bool AllowPrePostCommands { get; set; }

    public bool AllowKillBeforeOperation { get; set; }

    public bool AllowUninstallPrevious { get; set; }

    public bool AllowUpgrade { get; set; }

    public PolicyEditorDraftConstraints Clone() => new()
    {
        AllowInteractive = AllowInteractive,
        AllowSkipHashCheck = AllowSkipHashCheck,
        AllowPreRelease = AllowPreRelease,
        AllowCustomInstallLocation = AllowCustomInstallLocation,
        AllowedInstallLocationPatterns = [.. AllowedInstallLocationPatterns],
        AllowCustomParameters = AllowCustomParameters,
        AllowedCustomParameters = [.. AllowedCustomParameters],
        AllowedCustomParameterPatterns = [.. AllowedCustomParameterPatterns],
        DeniedCustomParameters = [.. DeniedCustomParameters],
        AllowPrePostCommands = AllowPrePostCommands,
        AllowKillBeforeOperation = AllowKillBeforeOperation,
        AllowUninstallPrevious = AllowUninstallPrevious,
        AllowUpgrade = AllowUpgrade,
    };
}
