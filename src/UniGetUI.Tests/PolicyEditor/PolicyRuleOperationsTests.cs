using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Model;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages.PolicyEditor;

namespace UniGetUI.Tests.PolicyEditor;

public class PolicyRuleOperationsTests
{
    private static PolicyEditorSession StartCreateSession(string id = "id-1", string publisher = "Contoso") =>
        PolicyEditorSession.StartCreate(
            PolicyEditorTestFixtures.BuildMissingManagement(),
            PolicyEditorTemplates.CreateNew(id, publisher));

    [Fact]
    public void CreateRuleId_ProducesContractSafeIdentifier()
    {
        string id = PolicyRuleFactory.CreateRuleId();

        Assert.Matches("^[A-Za-z0-9][A-Za-z0-9._:-]*$", id);
        Assert.True(id.Length <= 128);
    }

    [Fact]
    public void CreateBlank_ProducesDisabledWildcardDenyRule()
    {
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank();

        Assert.False(rule.Enabled);
        Assert.Equal(Devolutions.Now.Policy.Model.Decision.Deny, rule.Decision);
        Assert.Equal(0u, rule.Priority);
        Assert.Null(rule.Constraints);
        Assert.Empty(rule.Match.Operations);
    }

    [Fact]
    public void CreateBlank_WithExplicitId_UsesIt()
    {
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("explicit-id");
        Assert.Equal("explicit-id", rule.Id);
    }

    [Fact]
    public void Add_AppendsRule()
    {
        List<PolicyEditorDraftRule> rules = [];
        PolicyEditorDraftRule rule = PolicyRuleFactory.CreateBlank("a");

        PolicyRuleListOperations.Add(rules, rule);

        Assert.Same(rule, Assert.Single(rules));
    }

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        Assert.Throws<InvalidOperationException>(() => PolicyRuleListOperations.Add(rules, PolicyRuleFactory.CreateBlank("a")));
    }

    [Fact]
    public void Edit_MutatesTheNamedRule()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        PolicyRuleListOperations.Edit(rules, "a", rule => rule.Reason = "Updated reason");

        Assert.Equal("Updated reason", rules[0].Reason);
    }

    [Fact]
    public void Edit_UnknownId_Throws()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        Assert.Throws<KeyNotFoundException>(() => PolicyRuleListOperations.Edit(rules, "missing", _ => { }));
    }

    [Fact]
    public void Duplicate_AssignsNewIdAndInsertsRightAfterSource()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];
        rules[0].Reason = "original";

        string newId = PolicyRuleListOperations.Duplicate(rules, "a");

        Assert.NotEqual("a", newId);
        Assert.Equal(3, rules.Count);
        Assert.Equal("a", rules[0].Id);
        Assert.Equal(newId, rules[1].Id);
        Assert.Equal("b", rules[2].Id);
        Assert.Equal("original", rules[1].Reason);
    }

    [Fact]
    public void Duplicate_WithExplicitNewId_UsesIt()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        string newId = PolicyRuleListOperations.Duplicate(rules, "a", "explicit-new-id");

        Assert.Equal("explicit-new-id", newId);
        Assert.Equal("explicit-new-id", rules[1].Id);
    }

    [Fact]
    public void Duplicate_WithExplicitNewIdAlreadyTaken_Throws()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];

        Assert.Throws<InvalidOperationException>(() => PolicyRuleListOperations.Duplicate(rules, "a", "b"));
    }

    [Fact]
    public void Duplicate_UnknownSourceId_Throws()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        Assert.Throws<KeyNotFoundException>(() => PolicyRuleListOperations.Duplicate(rules, "missing"));
    }

    [Fact]
    public void SetEnabled_TogglesTheNamedRuleOnly()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];

        PolicyRuleListOperations.SetEnabled(rules, "a", true);

        Assert.True(rules[0].Enabled);
        Assert.False(rules[1].Enabled);
    }

    [Fact]
    public void Delete_RemovesTheNamedRule()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];

        PolicyRuleListOperations.Delete(rules, "a");

        Assert.Single(rules);
        Assert.Equal("b", rules[0].Id);
    }

    [Fact]
    public void Delete_UnknownId_Throws()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a")];

        Assert.Throws<KeyNotFoundException>(() => PolicyRuleListOperations.Delete(rules, "missing"));
    }

    [Fact]
    public void Move_ReordersToRequestedIndex()
    {
        List<PolicyEditorDraftRule> rules =
        [
            PolicyRuleFactory.CreateBlank("a"),
            PolicyRuleFactory.CreateBlank("b"),
            PolicyRuleFactory.CreateBlank("c"),
        ];

        PolicyRuleListOperations.Move(rules, "c", 0);

        Assert.Equal(["c", "a", "b"], rules.Select(r => r.Id));
    }

    [Fact]
    public void Move_ClampsOutOfRangeIndex()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];

        PolicyRuleListOperations.Move(rules, "a", 999);

        Assert.Equal(["b", "a"], rules.Select(r => r.Id));
    }

    [Fact]
    public void SetPriority_UpdatesTheNamedRuleOnly()
    {
        List<PolicyEditorDraftRule> rules = [PolicyRuleFactory.CreateBlank("a"), PolicyRuleFactory.CreateBlank("b")];

        PolicyRuleListOperations.SetPriority(rules, "a", 42);

        Assert.Equal(42u, rules[0].Priority);
        Assert.Equal(0u, rules[1].Priority);
    }

    // ---- Enforcement through the session (structured-mode-only gating) -----------------------

    [Fact]
    public void Session_RuleMutations_ThrowInRawMode()
    {
        PolicyEditorSession session = StartCreateSession();
        session.SwitchToRaw();

        Assert.Throws<InvalidOperationException>(() => session.AddRule());
        Assert.Throws<InvalidOperationException>(() => session.DeleteRule("whatever"));
        Assert.Throws<InvalidOperationException>(() => session.SetRuleEnabled("whatever", true));
        Assert.Throws<InvalidOperationException>(() => session.SetRulePriority("whatever", 1));
        Assert.Throws<InvalidOperationException>(() => session.MoveRule("whatever", 0));
        Assert.Throws<InvalidOperationException>(() => session.DuplicateRule("whatever"));
        Assert.Throws<InvalidOperationException>(() => session.EditRule("whatever", _ => { }));
    }

    [Fact]
    public void Session_AddRule_AddsToStructuredDraft()
    {
        PolicyEditorSession session = StartCreateSession();

        PolicyEditorDraftRule added = session.AddRule();

        Assert.Single(session.Draft.Rules);
        Assert.Same(added, session.Draft.Rules[0]);
    }

    [Fact]
    public void Session_DuplicateRule_ProducesDistinctId()
    {
        PolicyEditorSession session = StartCreateSession();
        session.AddRule(PolicyRuleFactory.CreateBlank("rule-a"));

        string newId = session.DuplicateRule("rule-a");

        Assert.NotEqual("rule-a", newId);
        Assert.Equal(2, session.Draft.Rules.Count);
    }

    [Fact]
    public void ViewModelRuleCommands_TargetSelectedInstanceWhenIdsCollide()
    {
        PolicyEditorSession session = StartCreateSession();
        PolicyEditorDraftRule first = session.AddRule(PolicyRuleFactory.CreateBlank("same-id"));
        PolicyEditorDraftRule second = session.AddRule(PolicyRuleFactory.CreateBlank("second-id"));
        second.Id = first.Id;
        using var viewModel = new PolicyEditorSessionViewModel(
            session,
            new FakeValidationClient(),
            new FakeConfirmationPrompt(),
            new FakeWriteClient());

        viewModel.ToggleRuleCommand.Execute(second);
        Assert.False(first.Enabled);
        Assert.True(second.Enabled);

        viewModel.MoveRuleUpCommand.Execute(second);
        Assert.Same(second, session.Draft.Rules[0]);
        Assert.Same(first, session.Draft.Rules[1]);

        viewModel.DuplicateRuleCommand.Execute(second);
        Assert.Equal(3, session.Draft.Rules.Count);
        Assert.Same(second, session.Draft.Rules[0]);
        Assert.NotEqual(second.Id, session.Draft.Rules[1].Id);
        Assert.Same(first, session.Draft.Rules[2]);

        viewModel.DeleteRuleCommand.Execute(second);
        Assert.DoesNotContain(session.Draft.Rules, rule => ReferenceEquals(rule, second));
        Assert.Contains(session.Draft.Rules, rule => ReferenceEquals(rule, first));
    }
}
