using Devolutions.Now.Policy.Model;
using Devolutions.Now.Policy.Api;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>
/// Explicit, reflection-free, NativeAOT-safe field-by-field mapping between the wire model
/// (<see cref="PolicyDocument"/> and friends, from Devolutions.Now.Policy.Model) and the editor's
/// draft model (<see cref="PolicyEditorDraftDocument"/> and friends). Every mapping here also produces a
/// deep copy: no list or nested object is shared between the source and the result, so mutating one
/// side after mapping never affects the other.
/// </summary>
public static class PolicyEditorMapper
{
    // ---- PolicyDocument <-> PolicyEditorDraftDocument -------------------------------------------------

    public static PolicyEditorDraftDocument ToDraft(PolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new PolicyEditorDraftDocument
        {
            PolicyVersion = document.PolicyVersion,
            Metadata = ToDraft(document.Metadata),
            Enforcement = ToDraft(document.Enforcement),
            Rules = document.Rules.Select(ToDraft).ToList(),
        };
    }

    public static PolicyEditorDraftDocument ToDraft(PolicyDraftDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new PolicyEditorDraftDocument
        {
            PolicyVersion = document.PolicyVersion,
            Metadata = new PolicyEditorDraftMetadata
            {
                Id = document.Metadata.Id,
                Publisher = document.Metadata.Publisher,
                ValidFrom = document.Metadata.ValidFrom,
                ValidUntil = document.Metadata.ValidUntil,
                Description = document.Metadata.Description,
                SupportUrl = document.Metadata.SupportUrl,
            },
            Enforcement = ToDraft(document.Enforcement),
            Rules = document.Rules.Select(ToDraft).ToList(),
        };
    }

    public static PolicyDraftDocument ToSharedDraft(PolicyEditorDraftDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new PolicyDraftDocument
        {
            Schema = PolicyEditorPolicyContract.DraftSchema,
            PolicyVersion = draft.PolicyVersion,
            PolicyType = PolicyEditorPolicyContract.PolicyType,
            Metadata = new PolicyDraftMetadata
            {
                Id = draft.Metadata.Id,
                Publisher = draft.Metadata.Publisher,
                ValidFrom = draft.Metadata.ValidFrom,
                ValidUntil = draft.Metadata.ValidUntil,
                Description = draft.Metadata.Description,
                SupportUrl = draft.Metadata.SupportUrl,
            },
            Enforcement = ToDocument(draft.Enforcement),
            Rules = draft.Rules.Select(ToDocument).ToList(),
        };
    }

    /// <summary>Builds a committed document only from authoritative server metadata.</summary>
    public static PolicyDocument ToDocument(
        PolicyEditorDraftDocument draft,
        uint revision,
        DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new PolicyDocument
        {
            Schema = PolicyEditorPolicyContract.CommittedSchema,
            PolicyType = PolicyEditorPolicyContract.PolicyType,
            PolicyVersion = draft.PolicyVersion,
            Metadata = ToDocument(draft.Metadata, revision, publishedAt),
            Enforcement = ToDocument(draft.Enforcement),
            Rules = draft.Rules.Select(ToDocument).ToList(),
        };
    }

    /// <summary>Deep-clones a wire-model <see cref="PolicyDocument"/> without going through the draft
    /// (so <see cref="PolicyMetadata.Revision"/>/<see cref="PolicyMetadata.PublishedAt"/> survive intact).
    /// Used for origin snapshots and conflict captures.</summary>
    public static PolicyDocument CloneDocument(PolicyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new PolicyDocument
        {
            Schema = document.Schema,
            PolicyType = document.PolicyType,
            PolicyVersion = document.PolicyVersion,
            Metadata = CloneMetadata(document.Metadata),
            Enforcement = CloneEnforcement(document.Enforcement),
            Rules = document.Rules.Select(CloneRule).ToList(),
        };
    }

    public static PolicyDraftDocument CloneDraftDocument(PolicyDraftDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return PolicySerializer.DeserializePolicyDraftDocumentStrict(PolicySerializer.Serialize(document))
            ?? throw new InvalidOperationException("Serialized policy draft unexpectedly deserialized as null.");
    }

    public static PolicyManagementSnapshot CloneManagementSnapshot(
        PolicyManagementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PolicyManagementSnapshot
        {
            State = snapshot.State,
            ConfiguredPath = snapshot.ConfiguredPath,
            StoreToken = snapshot.StoreToken,
            Source = snapshot.Source,
            WriteCapability = snapshot.WriteCapability,
            ReadOnlyReason = snapshot.ReadOnlyReason,
            ElevationRequired = snapshot.ElevationRequired,
            Policy = snapshot.Policy is null ? null : CloneDocument(snapshot.Policy),
            // Editor concurrency only needs state, token, capability, and active identity.
            // Invalid diagnostics are presented through the separately bounded management view.
            InvalidDiagnostics = null,
        };
    }

    // ---- Metadata ----------------------------------------------------------------------------

    private static PolicyEditorDraftMetadata ToDraft(PolicyMetadata metadata) => new()
    {
        Id = metadata.Id,
        Publisher = metadata.Publisher,
        ValidFrom = metadata.ValidFrom,
        ValidUntil = metadata.ValidUntil,
        Description = metadata.Description,
        SupportUrl = metadata.SupportUrl,
    };

    private static PolicyMetadata ToDocument(PolicyEditorDraftMetadata draft, uint revision, DateTimeOffset publishedAt) => new()
    {
        Id = draft.Id,
        Publisher = draft.Publisher,
        Revision = revision,
        PublishedAt = publishedAt,
        ValidFrom = draft.ValidFrom,
        ValidUntil = draft.ValidUntil,
        Description = draft.Description,
        SupportUrl = draft.SupportUrl,
    };

    private static PolicyMetadata CloneMetadata(PolicyMetadata metadata) => new()
    {
        Id = metadata.Id,
        Publisher = metadata.Publisher,
        Revision = metadata.Revision,
        PublishedAt = metadata.PublishedAt,
        ValidFrom = metadata.ValidFrom,
        ValidUntil = metadata.ValidUntil,
        Description = metadata.Description,
        SupportUrl = metadata.SupportUrl,
    };

    // ---- Enforcement -------------------------------------------------------------------------

    private static PolicyEditorDraftEnforcement ToDraft(PolicyEnforcement enforcement) => new()
    {
        DefaultDecision = enforcement.DefaultDecision,
        AuditMode = enforcement.AuditMode,
    };

    private static PolicyEnforcement ToDocument(PolicyEditorDraftEnforcement draft) => new()
    {
        DefaultDecision = draft.DefaultDecision,
        RulePrecedence = PolicyEditorPolicyContract.FixedRulePrecedence,
        AuditMode = draft.AuditMode,
    };

    private static PolicyEnforcement CloneEnforcement(PolicyEnforcement enforcement) => new()
    {
        DefaultDecision = enforcement.DefaultDecision,
        RulePrecedence = enforcement.RulePrecedence,
        AuditMode = enforcement.AuditMode,
    };

    // ---- Rule / Match / Constraints ----------------------------------------------------------

    private static PolicyEditorDraftRule ToDraft(PolicyRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Priority = rule.Priority,
        Decision = rule.Decision,
        Reason = rule.Reason,
        Match = ToDraft(rule.Match),
        Constraints = rule.Constraints is null ? null : ToDraft(rule.Constraints),
    };

    private static PolicyRule ToDocument(PolicyEditorDraftRule draft) => new()
    {
        Id = draft.Id,
        Enabled = draft.Enabled,
        Priority = draft.Priority,
        Decision = draft.Decision,
        Reason = draft.Reason,
        Match = ToDocument(draft.Match),
        Constraints = draft.Constraints is null ? null : ToDocument(draft.Constraints),
    };

    private static PolicyRule CloneRule(PolicyRule rule) => new()
    {
        Id = rule.Id,
        Enabled = rule.Enabled,
        Priority = rule.Priority,
        Decision = rule.Decision,
        Reason = rule.Reason,
        Match = CloneMatch(rule.Match),
        Constraints = rule.Constraints is null ? null : CloneConstraints(rule.Constraints),
    };

    private static PolicyEditorDraftMatch ToDraft(PolicyMatch match) => new()
    {
        Operations = [.. match.Operations],
        Managers = [.. match.Managers],
        Sources = [.. match.Sources],
        PackageIdentifiers = [.. match.PackageIdentifiers],
        PackageNames = [.. match.PackageNames],
        Versions = [.. match.Versions],
        VersionRange = match.VersionRange is null ? null : ToDraft(match.VersionRange),
        Scopes = [.. match.Scopes],
        Architectures = [.. match.Architectures],
        Elevation = [.. match.Elevation],
        Interactive = ToTriState(match.Interactive),
        SkipHashCheck = ToTriState(match.SkipHashCheck),
        PreRelease = ToTriState(match.PreRelease),
        HasCustomParameters = ToTriState(match.HasCustomParameters),
        HasCustomInstallLocation = ToTriState(match.HasCustomInstallLocation),
        HasPrePostCommands = ToTriState(match.HasPrePostCommands),
        HasKillBeforeOperation = ToTriState(match.HasKillBeforeOperation),
        HasUninstallPrevious = ToTriState(match.HasUninstallPrevious),
    };

    private static PolicyMatch ToDocument(PolicyEditorDraftMatch draft) => new()
    {
        Operations = [.. draft.Operations],
        Managers = [.. draft.Managers],
        Sources = [.. draft.Sources],
        PackageIdentifiers = [.. draft.PackageIdentifiers],
        PackageNames = [.. draft.PackageNames],
        Versions = [.. draft.Versions],
        VersionRange = draft.VersionRange is null ? null : ToDocument(draft.VersionRange),
        Scopes = [.. draft.Scopes],
        Architectures = [.. draft.Architectures],
        Elevation = [.. draft.Elevation],
        Interactive = FromTriState(draft.Interactive),
        SkipHashCheck = FromTriState(draft.SkipHashCheck),
        PreRelease = FromTriState(draft.PreRelease),
        HasCustomParameters = FromTriState(draft.HasCustomParameters),
        HasCustomInstallLocation = FromTriState(draft.HasCustomInstallLocation),
        HasPrePostCommands = FromTriState(draft.HasPrePostCommands),
        HasKillBeforeOperation = FromTriState(draft.HasKillBeforeOperation),
        HasUninstallPrevious = FromTriState(draft.HasUninstallPrevious),
    };

    private static PolicyMatch CloneMatch(PolicyMatch match) => new()
    {
        Operations = [.. match.Operations],
        Managers = [.. match.Managers],
        Sources = [.. match.Sources],
        PackageIdentifiers = [.. match.PackageIdentifiers],
        PackageNames = [.. match.PackageNames],
        Versions = [.. match.Versions],
        VersionRange = match.VersionRange is null ? null : CloneVersionRange(match.VersionRange),
        Scopes = [.. match.Scopes],
        Architectures = [.. match.Architectures],
        Elevation = [.. match.Elevation],
        Interactive = [.. match.Interactive],
        SkipHashCheck = [.. match.SkipHashCheck],
        PreRelease = [.. match.PreRelease],
        HasCustomParameters = [.. match.HasCustomParameters],
        HasCustomInstallLocation = [.. match.HasCustomInstallLocation],
        HasPrePostCommands = [.. match.HasPrePostCommands],
        HasKillBeforeOperation = [.. match.HasKillBeforeOperation],
        HasUninstallPrevious = [.. match.HasUninstallPrevious],
    };

    private static PolicyEditorDraftVersionRange ToDraft(VersionRange range) => new()
    {
        MinVersion = range.MinVersion,
        MaxVersion = range.MaxVersion,
        IncludePrerelease = range.IncludePrerelease,
    };

    private static VersionRange ToDocument(PolicyEditorDraftVersionRange draft) => new()
    {
        MinVersion = draft.MinVersion,
        MaxVersion = draft.MaxVersion,
        IncludePrerelease = draft.IncludePrerelease,
    };

    private static VersionRange CloneVersionRange(VersionRange range) => new()
    {
        MinVersion = range.MinVersion,
        MaxVersion = range.MaxVersion,
        IncludePrerelease = range.IncludePrerelease,
    };

    private static PolicyEditorDraftConstraints ToDraft(PolicyConstraints constraints) => new()
    {
        AllowInteractive = constraints.AllowInteractive,
        AllowSkipHashCheck = constraints.AllowSkipHashCheck,
        AllowPreRelease = constraints.AllowPreRelease,
        AllowCustomInstallLocation = constraints.AllowCustomInstallLocation,
        AllowedInstallLocationPatterns = [.. constraints.AllowedInstallLocationPatterns],
        AllowCustomParameters = constraints.AllowCustomParameters,
        AllowedCustomParameters = [.. constraints.AllowedCustomParameters],
        AllowedCustomParameterPatterns = [.. constraints.AllowedCustomParameterPatterns],
        DeniedCustomParameters = [.. constraints.DeniedCustomParameters],
        AllowPrePostCommands = constraints.AllowPrePostCommands,
        AllowKillBeforeOperation = constraints.AllowKillBeforeOperation,
        AllowUninstallPrevious = constraints.AllowUninstallPrevious,
        AllowUpgrade = constraints.AllowUpgrade,
    };

    private static PolicyConstraints ToDocument(PolicyEditorDraftConstraints draft) => new()
    {
        AllowInteractive = draft.AllowInteractive,
        AllowSkipHashCheck = draft.AllowSkipHashCheck,
        AllowPreRelease = draft.AllowPreRelease,
        AllowCustomInstallLocation = draft.AllowCustomInstallLocation,
        AllowedInstallLocationPatterns = [.. draft.AllowedInstallLocationPatterns],
        AllowCustomParameters = draft.AllowCustomParameters,
        AllowedCustomParameters = [.. draft.AllowedCustomParameters],
        AllowedCustomParameterPatterns = [.. draft.AllowedCustomParameterPatterns],
        DeniedCustomParameters = [.. draft.DeniedCustomParameters],
        AllowPrePostCommands = draft.AllowPrePostCommands,
        AllowKillBeforeOperation = draft.AllowKillBeforeOperation,
        AllowUninstallPrevious = draft.AllowUninstallPrevious,
        AllowUpgrade = draft.AllowUpgrade,
    };

    private static PolicyConstraints CloneConstraints(PolicyConstraints constraints) => new()
    {
        AllowInteractive = constraints.AllowInteractive,
        AllowSkipHashCheck = constraints.AllowSkipHashCheck,
        AllowPreRelease = constraints.AllowPreRelease,
        AllowCustomInstallLocation = constraints.AllowCustomInstallLocation,
        AllowedInstallLocationPatterns = [.. constraints.AllowedInstallLocationPatterns],
        AllowCustomParameters = constraints.AllowCustomParameters,
        AllowedCustomParameters = [.. constraints.AllowedCustomParameters],
        AllowedCustomParameterPatterns = [.. constraints.AllowedCustomParameterPatterns],
        DeniedCustomParameters = [.. constraints.DeniedCustomParameters],
        AllowPrePostCommands = constraints.AllowPrePostCommands,
        AllowKillBeforeOperation = constraints.AllowKillBeforeOperation,
        AllowUninstallPrevious = constraints.AllowUninstallPrevious,
        AllowUpgrade = constraints.AllowUpgrade,
    };

    // ---- Tri-state boolean-match conversion --------------------------------------------------

    /// <summary>
    /// Converts the contract's empty-or-single-value boolean match into a tri-state.
    /// </summary>
    internal static TriState ToTriState(IReadOnlyCollection<bool> values) => values.Count switch
    {
        0 => TriState.Omitted,
        1 => values.First() ? TriState.True : TriState.False,
        _ => throw new InvalidDataException("Boolean policy match arrays may contain at most one value."),
    };

    internal static List<bool> FromTriState(TriState state) => state switch
    {
        TriState.Omitted => [],
        TriState.True => [true],
        TriState.False => [false],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown tri-state value."),
    };
}
