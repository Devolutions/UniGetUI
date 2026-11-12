using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Interface;

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
        // unique unanchored match. The anchored policy-management action entries (Edit/Create/
        // Replace identity) intentionally also match the "policy" query, so we only assert
        // uniqueness of the unanchored base entry rather than of the whole result set.
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

    [Fact]
    public void Search_DoesNotExposePolicyRepairAction()
    {
        IReadOnlyList<SettingsSearchResult> results =
            SettingsSearchIndex.Search("repair policy", limit: 100, isWindows: true);

        Assert.DoesNotContain(results, result =>
            result.PageType == typeof(AgentPolicyInspector)
            && result.Anchor == "RepairPolicyButton");
        Assert.DoesNotContain(results, result =>
            result.Title.Equals("Repair the stored policy", StringComparison.Ordinal));
    }

#if UNIGETUI_XCODE_EXTENSION
    [Fact]
    public void Search_ExposesXcodeSettingsOnlyOnMac()
    {
        Assert.Contains(
            SettingsSearchIndex.Search("xcode", limit: 100, isWindows: false, isMacOS: true),
            result => result.PageType == typeof(XcodeSettings));
        Assert.DoesNotContain(
            SettingsSearchIndex.Search("xcode", limit: 100, isWindows: false, isMacOS: false),
            result => result.PageType == typeof(XcodeSettings));
        Assert.DoesNotContain(
            SettingsSearchIndex.Search("xcode", limit: 100, isWindows: true),
            result => result.PageType == typeof(XcodeSettings));
    }

    [Theory]
    [InlineData("xcode install directory", "XcodeInstallDirectoryCard")]
    [InlineData("xcode archive directory", "XcodeArchiveDirectoryCard")]
    [InlineData("different apple id", "SignOutCard")]
    [InlineData("xcode automatic install", "XcodeAutoInstallCard")]
    public void Search_FindsXcodeDirectoryAndAccountSettings(string query, string anchor)
    {
        Assert.Contains(SettingsSearchIndex.Search(query, limit: 100, isWindows: false, isMacOS: true),
            result => result.PageType == typeof(XcodeSettings) && result.Anchor == anchor);
    }
#else
    [Fact]
    public async Task NonMacBuildsExcludeXcodeExtensionSettingsNavigationAndIpc()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), nameof(SettingsSearchIndexTests), Guid.NewGuid().ToString("N"));
        UniGetUI.Core.Data.CoreData.TEST_DataDirectoryOverride = Path.Combine(testRoot, "Data");
        Directory.CreateDirectory(UniGetUI.Core.Data.CoreData.UniGetUIUserConfigurationDirectory);
        UniGetUI.Core.SettingsEngine.Settings.ResetSettings();
        try
        {
            UniGetUI.Core.SettingsEngine.Settings.Set(UniGetUI.Core.SettingsEngine.Settings.K.EnableXcode, true);

            Assert.DoesNotContain(AppExtensions.All, extension => extension.Id == "xcode");
            Assert.Empty(SettingsSearchIndex.Search("xcode", limit: 100, isWindows: true, isMacOS: true));
            Assert.DoesNotContain(
                NavigationPageOrder.DefaultPages(catalogEnabled: true, gitRepositoriesEnabled: true, xcodeEnabled: true),
                page => page == PageType.Xcode);
            Assert.DoesNotContain(
                NavigationPageOrder.Resolve("Xcode,Discover", catalogEnabled: true, gitRepositoriesEnabled: true, xcodeEnabled: true),
                page => page == PageType.Xcode);
            Assert.DoesNotContain("xcode", IpcAppPages.SupportedPages);
            Assert.Throws<InvalidOperationException>(() => IpcAppPages.NormalizePageName("xcode"));

            global::Avalonia.Headless.HeadlessUnitTestSession session =
                global::Avalonia.Headless.HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsSearchIndexTests).Assembly);
            await session.Dispatch(() =>
            {
                var sidebarVm = new UniGetUI.Avalonia.ViewModels.SidebarViewModel();
                Assert.DoesNotContain(PageType.Xcode, sidebarVm.NavigationPages);
                var sidebar = new SidebarView { DataContext = sidebarVm };
                Assert.DoesNotContain(
                    sidebar.FindControl<global::Avalonia.Controls.ListBox>("NavListBox")!.Items.OfType<global::Avalonia.Controls.ListBoxItem>(),
                    item => Equals(item.Tag, "Xcode"));
                var extensionsPage = new ExtensionsHomepage();
                Assert.DoesNotContain(
                    global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(extensionsPage)
                        .OfType<global::Avalonia.Controls.TextBlock>(),
                    block => string.Equals(block.Text, "Xcode", StringComparison.Ordinal));
            }, CancellationToken.None);
        }
        finally
        {
            UniGetUI.Core.SettingsEngine.Settings.ResetSettings();
            UniGetUI.Core.Data.CoreData.TEST_DataDirectoryOverride = null;
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
#endif
}
