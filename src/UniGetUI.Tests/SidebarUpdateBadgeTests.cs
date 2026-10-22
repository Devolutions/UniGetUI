using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.TextFormatting;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class SidebarUpdateBadgeTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SidebarUpdateBadgeTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(SidebarUpdateBadgeTests), Guid.NewGuid().ToString("N"));

    public SidebarUpdateBadgeTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, recursive: true);
    }

    [Theory]
    [InlineData(3, 1.0)]
    [InlineData(3, 1.25)]
    [InlineData(3, 2.0)]
    [InlineData(42, 1.0)]
    [InlineData(42, 1.5)]
    [InlineData(150, 1.0)]
    [InlineData(150, 2.0)]
    public Task UpdateCountIsHorizontallyCenteredInItsBadge(int count, double scaling) => _session.Dispatch(() =>
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var vm = new SidebarViewModel { UpdatesBadgeCount = count };
        var view = new SidebarView { DataContext = vm };
        var window = new Window { Width = 300, Height = 700, Content = view };
        window.SetRenderScaling(scaling);
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        ListBoxItem updates = view.FindControl<ListBox>("NavListBox")!.Items.OfType<ListBoxItem>()
            .Single(item => (string?)item.Tag == "Updates");
        TextBlock text = updates.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == vm.UpdatesBadgeText);
        var badge = (Border)text.Parent!;
        TextLine line = text.TextLayout.TextLines.Single();

        double textLeft = text.TranslatePoint(new Point(line.Start, 0), badge)!.Value.X;
        double textRight = textLeft + line.WidthIncludingTrailingWhitespace;

        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(badge.Bounds.Width - textRight, textLeft, 0.01);
        window.Close();
    }, CancellationToken.None);
}
