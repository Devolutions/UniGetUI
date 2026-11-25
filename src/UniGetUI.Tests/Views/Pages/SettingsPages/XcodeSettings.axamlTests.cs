#if UNIGETUI_XCODE_EXTENSION
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;

namespace UniGetUI.Tests;

public sealed class XcodeSettingsTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(XcodeSettingsTests).Assembly);

    [Theory]
    [InlineData("XcodeInstallDirectoryCard", "Installation directory")]
    [InlineData("XcodeArchiveDirectoryCard", "Archive directory")]
    public Task DirectoryCards_KeepTheirCardLayoutAroundTheEditor(string name, string header) => _session.Dispatch(() =>
    {
        var page = new XcodeSettings();
        var card = page.FindControl<SettingsCard>(name) ?? throw new InvalidOperationException($"Missing {name}.");

        var frame = Assert.IsType<Border>(card.GetValue(ContentControl.ContentProperty));
        Assert.Contains("settings-card", frame.Classes);
        Assert.Equal(header, card.Header);
        Assert.Contains(card.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == header);
        Assert.Contains(frame.GetLogicalDescendants().OfType<TextBox>(), box => box.Parent is StackPanel);
    }, CancellationToken.None);
}
#endif
