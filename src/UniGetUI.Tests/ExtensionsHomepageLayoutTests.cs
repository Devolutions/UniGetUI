using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;

namespace UniGetUI.Tests;

public sealed class ExtensionsHomepageLayoutTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ExtensionsHomepageLayoutTests).Assembly);

    [Fact]
    public Task SwitchesStayAlignedWhateverTheStatusWidth() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var page = new ExtensionsHomepage();
        AgentLibraryTestEvidence.AddApplicationResources(page);
        using var host = new AgentSkillsSettingsTestWindow(page, width: 1000);

        StatusBadge[] badges = page.GetVisualDescendants().OfType<StatusBadge>().ToArray();
        Assert.True(badges.Length >= 2);
        string[] labels = ["On", "A much longer status than the others", "No clones yet"];
        for (int i = 0; i < badges.Length; i++)
            badges[i].Text = labels[i % labels.Length];
        host.Pump();
        AgentLibraryTestEvidence.Capture(host, "extensions-homepage-alignment");

        double[] rightEdges = page.GetVisualDescendants().OfType<ToggleSwitch>()
            .Select(toggle => toggle.TranslatePoint(new Point(toggle.Bounds.Width, 0), page)!.Value.X)
            .ToArray();
        Assert.Equal(badges.Length, rightEdges.Length);
        Assert.All(rightEdges, edge => Assert.Equal(rightEdges[0], edge, 0.5));
    }, CancellationToken.None);
}
