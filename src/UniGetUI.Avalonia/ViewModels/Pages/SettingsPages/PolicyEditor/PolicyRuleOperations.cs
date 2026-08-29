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

    /// <summary>Creates a new, empty, enabled rule that matches nothing until edited.</summary>
    public static PolicyEditorDraftRule CreateBlank(string? id = null) => new()
    {
        Id = id ?? CreateRuleId(),
        Enabled = true,
        Priority = 0,
        Decision = Decision.Deny,
        Reason = null,
        Match = new PolicyEditorDraftMatch(),
        Constraints = null,
    };
}

/// <summary>
/// Pure, UI-independent mutation operations over a rule list, covering add/edit/duplicate(new
/// ID)/enable/disable/delete/reorder/priority. Every operation validates rule-identity uniqueness and
/// existence up front and throws rather than silently no-op, so callers (namely
/// <see cref="PolicyEditorSession"/>) never end up with a list in an inconsistent state.
/// </summary>
public static class PolicyRuleListOperations
{
    public static void Add(List<PolicyEditorDraftRule> rules, PolicyEditorDraftRule rule)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rule);
        EnsureIdIsUnique(rules, rule.Id);
        rules.Add(rule);
    }

    public static void Edit(List<PolicyEditorDraftRule> rules, string id, Action<PolicyEditorDraftRule> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate(Find(rules, id));
    }

    /// <summary>Duplicates a rule, always assigning the copy a new identifier distinct from every
    /// existing rule. Returns the new rule's id.</summary>
    public static string Duplicate(List<PolicyEditorDraftRule> rules, string id, string? newId = null)
    {
        PolicyEditorDraftRule source = Find(rules, id);
        string generatedId = newId ?? PolicyRuleFactory.CreateRuleId();
        EnsureIdIsUnique(rules, generatedId);

        PolicyEditorDraftRule copy = source.CloneWithNewId(generatedId);
        int index = rules.IndexOf(source);
        rules.Insert(index + 1, copy);
        return generatedId;
    }

    public static void SetEnabled(List<PolicyEditorDraftRule> rules, string id, bool enabled) =>
        Find(rules, id).Enabled = enabled;

    public static void Delete(List<PolicyEditorDraftRule> rules, string id) =>
        rules.Remove(Find(rules, id));

    /// <summary>Moves a rule to a new position in document order. <paramref name="newIndex"/> is
    /// clamped to the valid range.</summary>
    public static void Move(List<PolicyEditorDraftRule> rules, string id, int newIndex)
    {
        PolicyEditorDraftRule rule = Find(rules, id);
        int clamped = Math.Clamp(newIndex, 0, rules.Count - 1);
        rules.Remove(rule);
        rules.Insert(clamped, rule);
    }

    public static void SetPriority(List<PolicyEditorDraftRule> rules, string id, uint priority) =>
        Find(rules, id).Priority = priority;

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
}
