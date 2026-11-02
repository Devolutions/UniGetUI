using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class PackageListColumnLayoutTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PackageListColumnLayoutTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(PackageListColumnLayoutTests), Guid.NewGuid().ToString("N"));

    public PackageListColumnLayoutTests()
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

    private static readonly string[] Tags = ["Name", "Id", "Version", "NewVersion"];

    private static (Window Window, DataGrid Grid) Show(double width, bool newVersionVisible = true)
    {
        var grid = new DataGrid { ItemsSource = new[] { "a", "b" } };
        grid.Columns.Add(new DataGridTextColumn { Width = new DataGridLength(40) });
        foreach (string tag in Tags)
            grid.Columns.Add(new DataGridTextColumn
            {
                Tag = tag,
                Header = tag,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                IsVisible = tag != "NewVersion" || newVersionVisible,
            });
        var dataGridTheme = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml");
        var styles = Application.Current!.Styles;
        if (!styles.OfType<StyleInclude>().Any(s => s.Source == dataGridTheme))
            styles.Add(new StyleInclude(new Uri("avares://UniGetUI.Tests")) { Source = dataGridTheme });
        var window = new Window { Width = width, Height = 300, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, grid);
    }

    private static DataGridColumn Column(DataGrid grid, string tag) => grid.Columns.First(c => c.Tag as string == tag);

    private static Dictionary<string, int> Saved() =>
        CoreSettings.GetDictionary<string, int>(CoreSettings.K.PackageListColumnWidths)?.ToDictionary() ?? [];

    [Fact]
    public Task FitToWindowKeepsTheGridsDefaults() => _session.Dispatch(() =>
    {
        var (untouchedWindow, untouched) = Show(1000);
        var (window, grid) = Show(1000);
        PackageListColumnLayout.Apply(grid, fitToWindow: false);
        PackageListColumnLayout.Apply(grid, fitToWindow: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(untouched.HorizontalScrollBarVisibility, grid.HorizontalScrollBarVisibility);
        Assert.Equal(untouched.FrozenColumnCount, grid.FrozenColumnCount);
        foreach (string tag in Tags)
        {
            Assert.True(Column(grid, tag).Width.IsStar);
            Assert.Equal(Column(untouched, tag).MinWidth, Column(grid, tag).MinWidth);
            Assert.Equal(Column(untouched, tag).ActualWidth, Column(grid, tag).ActualWidth);
        }
        window.Close();
        untouchedWindow.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SeedingTakesTheVisibleListsWidthsAndDropsColumnsItDoesNotShow() => _session.Dispatch(() =>
    {
        CoreSettings.SetDictionary(CoreSettings.K.PackageListColumnWidths, new Dictionary<string, int> { ["NewVersion"] = 61, ["Name"] = 61 });
        var (window, grid) = Show(1000, newVersionVisible: false);
        double expected = Column(grid, "Name").ActualWidth;
        Assert.True(expected > PackageListColumnLayout.FixedColumnMinWidth);

        PackageListColumnLayout.SeedFrom(grid);

        var saved = Saved();
        Assert.Equal(["Id", "Name", "Version"], saved.Keys.Order());
        Assert.Equal((int)Math.Round(expected), saved["Name"]);

        PackageListColumnLayout.Apply(grid, fitToWindow: false);
        Assert.Equal(PackageListColumnLayout.DefaultFixedColumnWidth, Column(grid, "NewVersion").Width.Value);
        Assert.Equal(ScrollBarVisibility.Auto, grid.HorizontalScrollBarVisibility);
        Assert.Equal(1, grid.FrozenColumnCount);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SeedingFromAHiddenListStartsEveryColumnAtTheDefault() => _session.Dispatch(() =>
    {
        CoreSettings.SetDictionary(CoreSettings.K.PackageListColumnWidths, new Dictionary<string, int> { ["Name"] = 61 });
        var (window, grid) = Show(1000);
        grid.IsVisible = false;
        Dispatcher.UIThread.RunJobs();

        PackageListColumnLayout.SeedFrom(grid);
        PackageListColumnLayout.Apply(grid, fitToWindow: false);

        Assert.Empty(Saved());
        foreach (string tag in Tags)
            Assert.Equal(PackageListColumnLayout.DefaultFixedColumnWidth, Column(grid, tag).Width.Value);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task FixedWidthsFreezeTheCheckboxColumnEvenAfterAColumnWasMovedInFrontOfIt() => _session.Dispatch(() =>
    {
        var (window, grid) = Show(1000);
        Column(grid, "Version").DisplayIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(0, grid.Columns[0].DisplayIndex);

        PackageListColumnLayout.Apply(grid, fitToWindow: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, grid.Columns[0].DisplayIndex);
        Assert.Equal(1, grid.FrozenColumnCount);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SavedWidthsAreRestoredOnANewGridAndClampedToTheMinimum() => _session.Dispatch(() =>
    {
        CoreSettings.SetDictionary(CoreSettings.K.PackageListColumnWidths, new Dictionary<string, int> { ["Name"] = 321, ["Id"] = 10 });
        var (window, grid) = Show(1000);

        PackageListColumnLayout.Apply(grid, fitToWindow: false);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(321, Column(grid, "Name").ActualWidth);
        Assert.Equal(PackageListColumnLayout.FixedColumnMinWidth, Column(grid, "Id").ActualWidth);
        Assert.Equal(PackageListColumnLayout.DefaultFixedColumnWidth, Column(grid, "Version").ActualWidth);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SavingNotifiesOtherListsOnlyWhenAWidthChanged() => _session.Dispatch(() =>
    {
        var (window, grid) = Show(1000);
        var (otherWindow, other) = Show(1000);
        PackageListColumnLayout.SeedFrom(grid);
        PackageListColumnLayout.Apply(grid, fitToWindow: false);
        PackageListColumnLayout.Apply(other, fitToWindow: false);
        Dispatcher.UIThread.RunJobs();

        int raised = 0;
        void OnSaved()
        {
            raised++;
            PackageListColumnLayout.Apply(other, fitToWindow: false);
        }
        PackageListColumnLayout.WidthsSaved += OnSaved;
        try
        {
            PackageListColumnLayout.SaveFrom(grid);
            Assert.Equal(0, raised);

            Column(grid, "Id").Width = new DataGridLength(123);
            Dispatcher.UIThread.RunJobs();
            PackageListColumnLayout.SaveFrom(grid);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, raised);
            Assert.Equal(123, Saved()["Id"]);
            Assert.Equal(123, Column(other, "Id").ActualWidth);
        }
        finally
        {
            PackageListColumnLayout.WidthsSaved -= OnSaved;
            window.Close();
            otherWindow.Close();
        }
    }, CancellationToken.None);
}
