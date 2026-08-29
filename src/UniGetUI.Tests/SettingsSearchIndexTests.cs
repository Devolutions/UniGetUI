using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;

namespace UniGetUI.Tests;

public class SettingsSearchIndexTests
{
    [Theory]
    [InlineData("policy")]
    [InlineData("package broker")]
    [InlineData("devolutions agent")]
    [InlineData("rules")]
    [InlineData("enforcement")]
    public void Search_ReturnsPolicyInspectorOnWindows(string query)
    {
        // The base "Inspect active package broker policy" entry (Anchor == null) must remain the
        // unique unanchored match. Phase 2 policy-management action entries (Edit/Create/Repair/
        // Replace identity, all anchored) intentionally also match the "policy" query, so we only
        // assert uniqueness of the unanchored base entry rather than of the whole result set.
        SettingsSearchResult result = Assert.Single(
            SettingsSearchIndex.Search(query, limit: 100, isWindows: true),
            result => result.PageType == typeof(AgentPolicyInspector) && result.Anchor is null);

        Assert.Equal("Active package broker policy", result.PageTitle);
    }

    [Fact]
    public void Search_HidesPolicyInspectorOffWindows()
    {
        IReadOnlyList<SettingsSearchResult> results =
            SettingsSearchIndex.Search("package broker policy", limit: 100, isWindows: false);

        Assert.DoesNotContain(results, result => result.PageType == typeof(AgentPolicyInspector));
    }
}
