using Devolutions.Now.Policy.Model;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

/// <summary>Creates blank rules with fresh, contract-valid identifiers.</summary>
public static class PolicyRuleFactory
{
    /// <summary>
    /// Generates a new rule identifier. The format (lowercase hex GUID with a readable prefix) only
    /// uses ASCII letters, digits, and hyphens, satisfying the broker's resource-id contract.
    /// </summary>
    public static string CreateRuleId() => $"rule-{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a new disabled deny rule. Every match criterion is initially unrestricted, so the rule
    /// has no effect until an administrator narrows and enables it.
    /// </summary>
    public static PolicyEditorDraftRule CreateBlank(string? id = null) => new()
    {
        Id = id ?? CreateRuleId(),
        Enabled = false,
        Priority = 0,
        Decision = Decision.Deny,
        Reason = null,
        Match = new PolicyEditorDraftMatch(),
        Constraints = null,
    };
}

internal static class PolicyEditorRuleSemantics
{
    private static readonly HashSet<ManagerName> SourceCapableManagers =
    [
        ManagerName.Winget,
        ManagerName.PowerShell,
        ManagerName.PowerShell7,
        ManagerName.Chocolatey,
        ManagerName.Flatpak,
        ManagerName.Homebrew,
        ManagerName.Scoop,
        ManagerName.Vcpkg,
    ];

    public static bool HasConfiguredSafetyLimits(PolicyEditorDraftConstraints? constraints) =>
        constraints is not null
        && (!constraints.AllowInteractive
            || !constraints.AllowSkipHashCheck
            || !constraints.AllowPreRelease
            || !constraints.AllowCustomInstallLocation
            || constraints.AllowedInstallLocationPatterns.Count > 0
            || !constraints.AllowCustomParameters
            || constraints.AllowedCustomParameters.Count > 0
            || constraints.AllowedCustomParameterPatterns.Count > 0
            || constraints.DeniedCustomParameters.Count > 0
            || !constraints.AllowPrePostCommands
            || !constraints.AllowKillBeforeOperation
            || !constraints.AllowUninstallPrevious
            || !constraints.AllowUpgrade);

    public static bool IsCatchAll(PolicyEditorDraftMatch match) =>
        !HasVersionCriterion(match)
        && !HasPackageIdentifierCriterion(match)
        && match.Operations.Count == 0
        && match.Managers.Count == 0
        && match.SourceNames.Count == 0
        && match.Scopes.Count == 0
        && match.Architectures.Count == 0
        && match.ExecutionElevation.Count == 0
        && match.Interactive == TriState.Omitted
        && match.SkipHashCheck == TriState.Omitted
        && match.PreRelease == TriState.Omitted
        && match.HasCustomParameters == TriState.Omitted
        && match.HasCustomInstallLocation == TriState.Omitted
        && match.HasPrePostCommands == TriState.Omitted
        && match.HasKillBeforeOperation == TriState.Omitted
        && match.HasUninstallPrevious == TriState.Omitted;

    public static bool SupportsSourceNames(ManagerName manager) =>
        SourceCapableManagers.Contains(manager);

    public static bool HasPackageIdentifierCriterion(PolicyEditorDraftMatch match) =>
        match.PackageIdentifierMode switch
        {
            PackageIdentifierMode.Exact => match.ExactPackageIdentifiers.Count > 0,
            PackageIdentifierMode.Patterns => match.PackageIdentifierPatterns.Count > 0,
            _ => false,
        };

    public static bool HasVersionCriterion(PolicyEditorDraftMatch match) =>
        match.VersionMode switch
        {
            PackageVersionMode.Exact => match.ExactVersions.Count > 0,
            PackageVersionMode.Range =>
                !string.IsNullOrEmpty(match.VersionRange?.MinVersion)
                || !string.IsNullOrEmpty(match.VersionRange?.MaxVersion),
            _ => false,
        };

}

/// <summary>
/// Pure, UI-independent mutation operations over a rule list, covering add/edit/duplicate(new
/// ID)/enable/disable/delete/reorder/priority. UI actions can target the selected rule instance so
/// temporary duplicate IDs cannot redirect an action to a different row; ID-based overloads remain
/// available for validated programmatic callers.
/// </summary>
public static class PolicyRuleListOperations
{
    public static void Add(List<PolicyEditorDraftRule> rules, PolicyEditorDraftRule rule)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rule);
        EnsureIdIsUnique(rules, rule.Id);
        rules.Add(rule);
        NormalizePriorities(rules);
    }

    public static void Edit(List<PolicyEditorDraftRule> rules, string id, Action<PolicyEditorDraftRule> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate(Find(rules, id));
    }

    /// <summary>Duplicates a rule, always assigning the copy a new identifier distinct from every
    /// existing rule. Returns the new rule's id.</summary>
    public static string Duplicate(List<PolicyEditorDraftRule> rules, string id, string? newId = null)
        => Duplicate(rules, Find(rules, id), newId);

    public static string Duplicate(
        List<PolicyEditorDraftRule> rules,
        PolicyEditorDraftRule rule,
        string? newId = null)
    {
        PolicyEditorDraftRule source = Find(rules, rule);
        string generatedId = newId ?? PolicyRuleFactory.CreateRuleId();
        EnsureIdIsUnique(rules, generatedId);

        PolicyEditorDraftRule copy = source.CloneWithNewId(generatedId);
        int index = rules.IndexOf(source);
        rules.Insert(index + 1, copy);
        NormalizePriorities(rules);
        return generatedId;
    }

    public static void SetEnabled(List<PolicyEditorDraftRule> rules, string id, bool enabled) =>
        Find(rules, id).Enabled = enabled;

    public static void SetEnabled(
        List<PolicyEditorDraftRule> rules,
        PolicyEditorDraftRule rule,
        bool enabled) =>
        Find(rules, rule).Enabled = enabled;

    public static void Delete(List<PolicyEditorDraftRule> rules, string id)
    {
        rules.Remove(Find(rules, id));
        NormalizePriorities(rules);
    }

    public static void Delete(List<PolicyEditorDraftRule> rules, PolicyEditorDraftRule rule)
    {
        rules.Remove(Find(rules, rule));
        NormalizePriorities(rules);
    }

    /// <summary>Moves a rule to a new position in document order. <paramref name="newIndex"/> is
    /// clamped to the valid range.</summary>
    public static void Move(List<PolicyEditorDraftRule> rules, string id, int newIndex)
        => Move(rules, Find(rules, id), newIndex);

    public static void Move(
        List<PolicyEditorDraftRule> rules,
        PolicyEditorDraftRule rule,
        int newIndex)
    {
        rule = Find(rules, rule);
        int clamped = Math.Clamp(newIndex, 0, rules.Count - 1);
        rules.Remove(rule);
        rules.Insert(clamped, rule);
        NormalizePriorities(rules);
    }

    internal static void NormalizePriorities(List<PolicyEditorDraftRule> rules)
    {
        for (int index = 0; index < rules.Count; index++)
        {
            rules[index].Priority = checked((uint)index);
        }
    }

    private static void EnsureIdIsUnique(List<PolicyEditorDraftRule> rules, string id)
    {
        if (rules.Any(rule => string.Equals(rule.Id, id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A rule with id '{id}' already exists.");
        }
    }

    private static PolicyEditorDraftRule Find(List<PolicyEditorDraftRule> rules, string id)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"No rule with id '{id}' exists.");
    }

    private static PolicyEditorDraftRule Find(
        List<PolicyEditorDraftRule> rules,
        PolicyEditorDraftRule rule)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rule);
        return rules.FirstOrDefault(candidate => ReferenceEquals(candidate, rule))
            ?? throw new KeyNotFoundException("The selected rule no longer exists.");
    }
}
