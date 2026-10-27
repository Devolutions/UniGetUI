using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages;

namespace UniGetUI.Tests;

public sealed class ToolbarSelectionStateTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ToolbarSelectionStateTests).Assembly);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"toolbar-selection-tests-{Guid.NewGuid():N}");

    public ToolbarSelectionStateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_directory, true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return;
            }
        }
    }

    private static CatalogEntry Entry(string id) => new()
    {
        Name = id,
        Id = id,
        ManagerName = "WinGet",
        Source = "winget",
    };

    [Fact]
    public async Task SinglePackageActionsNeedExactlyOnePackage()
    {
        string path = Path.Combine(_directory, "catalog.json");
        await SoftwareCatalog.SaveAsync(path,
            [new() { Id = "tools", Name = "Tools", Packages = [Entry("First"), Entry("Second")] }]);

        await _session.Dispatch(async () =>
        {
            var document = new CatalogEditorViewModel();
            Assert.True(await document.LoadAsync(path));
            var page = new CatalogEditorPage(document);
            var window = new Window { Width = 1200, Height = 800, Content = page };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Control Button(string label) => page.ViewModel.ToolbarEntries.Single(e => e.Label == label).Control;
            var details = Button("Package details");
            var pin = Button("Pin version");
            var remove = Button("Remove selection from catalog");
            var packages = page.ViewModel.FilteredPackages;
            Assert.Equal(2, packages.Count);

            Assert.False(details.IsEnabled);
            Assert.False(pin.IsEnabled);
            Assert.False(remove.IsEnabled);

            packages[0].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(details.IsEnabled);
            Assert.True(pin.IsEnabled);
            Assert.True(remove.IsEnabled);

            packages[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(details.IsEnabled);
            Assert.False(pin.IsEnabled);
            Assert.True(remove.IsEnabled);

            packages.ClearSelection();
            Dispatcher.UIThread.RunJobs();
            Assert.False(details.IsEnabled);
            Assert.False(remove.IsEnabled);

            window.Close();
        }, CancellationToken.None);
    }
}
