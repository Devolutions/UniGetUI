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
        SettingsSearchResult result = Assert.Single(
            SettingsSearchIndex.Search(query, limit: 100, isWindows: true),
            result => result.PageType == typeof(AgentPolicyInspector));

        Assert.Null(result.Anchor);
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
