using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using static UniGetUI.Tests.AgentSkillsSettingsTestFixture;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;
using SkillsManager = UniGetUI.PackageEngine.Managers.SkillsManager.AgentSkills;

namespace UniGetUI.Tests;

public sealed class AgentSkillsSettingsCompositionTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentSkillsSettingsCompositionTests).Assembly);

    [Fact]
    public Task SkillsManagerPage_UsesInjectedSettingsAndRetainsOnlyRelevantShellControls() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var before = Snapshot();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var source = new TextBlock { Text = "Injected local source" };
        var settings = new AgentSkillsSettingsControl(vm, source);
        // Constructing the manager is inert; its concrete type selects the production Skills branch.
        // The injected control keeps agent discovery and all Notion work behind the recording fake.
        var manager = new SkillsManager();
        var page = new PackageManagerPage(manager, settings);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        Assert.Same(manager, Assert.IsType<PackageManagerViewModel>(page.DataContext).Manager);
        Assert.Equal("Agent Skills settings", page.ShortTitle);
        var enable = page.FindControl<CheckboxCard_Dict>("EnableManager")!;
        Assert.True(enable.IsEffectivelyVisible);
        Assert.True(enable._checkbox.IsChecked);
        Assert.False(CoreSettings.GetDictionaryItem<string, bool>(CoreSettings.K.DisabledManagers, "Skills"));
        Assert.Equal("Enable Agent Skills", enable._textblock.Text);
        Assert.True(page.FindControl<Border>("StatusBar")!.IsEffectivelyVisible);
        Assert.False(page.FindControl<ContentControl>("ExecutableHolder")!.IsVisible);
        Assert.False(page.FindControl<ContentControl>("PathHolder")!.IsVisible);
        Assert.False(page.FindControl<TextBlock>("InstallOptionsHeading")!.IsVisible);
        var installHolder = page.FindControl<ContentControl>("InstallOptionsHolder")!;
        Assert.False(installHolder.IsVisible);
        Assert.Null(installHolder.Content);
        Assert.False(page.FindControl<TextBlock>("ManagerSettingsHeading")!.IsVisible);
        Assert.Same(settings, Assert.Single(page.FindControl<StackPanel>("ExtraControls")!.Children));
        Assert.Same(settings, Assert.Single(page.GetVisualDescendants().OfType<AgentSkillsSettingsControl>()));
        Assert.Same(source, settings.FindControl<ContentControl>("SourcesHolder")!.Content);
        Assert.Empty(page.GetVisualDescendants().OfType<InstallOptionsPanel>());
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Update security");
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetName(combo) == "Minimum age for updates");
        Assert.Same(enable, Assert.Single(page.GetVisualDescendants().OfType<CheckboxCard_Dict>()));
        var positive = settings.FindControl<ToggleSwitch>("NotificationsToggle")!;
        Assert.True(positive.IsEffectivelyVisible);
        Assert.True(positive.IsChecked);
        var logs = page.FindControl<SettingsPageButton>("ManagerLogs")!;
        Assert.True(logs.IsEffectivelyVisible);
        Assert.Equal("View Agent Skills logs", logs.Header);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Related settings" && text.IsEffectivelyVisible);
        Assert.Equal(1, service.StatusReads);
        service.AssertNoHydrationWrites();
        before.AssertUnchanged();
        AssertOtherPreferences();
    }, CancellationToken.None);

    [Fact]
    public Task OrdinaryManagerPage_PreservesGenericControlsAndNegativeNotificationSemantics() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var manager = new AgentSkillsSettingsLocalManager();
        var page = new PackageManagerPage(manager);
        using var host = new AgentSkillsSettingsTestWindow(page, 1100);
        var heading = page.FindControl<TextBlock>("InstallOptionsHeading")!;
        var holder = page.FindControl<ContentControl>("InstallOptionsHolder")!;
        var options = Assert.IsType<InstallOptionsPanel>(holder.Content);
        var optionsVm = Assert.IsType<InstallOptionsPanelViewModel>(options.DataContext);
        // Production loads persisted defaults asynchronously; observe completion, never sleep.
        await When(optionsVm, () => !optionsVm.IsLoading);
        host.Pump();
        Assert.True(heading.IsEffectivelyVisible);
        Assert.Equal("Default installation options for Settings test manager packages", heading.Text);
        Assert.True(holder.IsEffectivelyVisible);
        Assert.False(optionsVm.HasChanges);
        Assert.True(optionsVm.CliDisabledWarningVisible);
        Assert.False(optionsVm.CliSectionEnabled);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Update security" && text.IsEffectivelyVisible);
        var minimumAge = Assert.Single(page.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetName(combo) == "Minimum age for updates");
        Assert.True(minimumAge.IsEffectivelyVisible);
        Assert.True(minimumAge.IsEnabled);
        // Hidden cards are not guaranteed to realize their content in the visual tree.
        // Inspect the actual card's Content, then prove it becomes visible on selection.
        var customAgeCard = Assert.Single(page.FindControl<StackPanel>("ExtraControls")!.Children.OfType<SettingsCard>(),
            card => Equals(card.Header, "Custom minimum age (days)"));
        Assert.False(customAgeCard.IsVisible);
        var customAge = Assert.IsType<TextBox>(customAgeCard.Content);
        Assert.Equal("Custom minimum age (days)", AutomationProperties.GetName(customAge));
        minimumAge.SelectedIndex = 7; // Binding/event check for the existing Custom choice.
        host.Pump();
        Assert.True(customAge.IsEffectivelyVisible);
        Assert.Equal("custom", CoreSettings.GetDictionaryItem<string, string>(CoreSettings.K.PerManagerMinimumUpdateAge, manager.Name));
        var negative = Assert.Single(page.GetVisualDescendants().OfType<CheckboxCard_Dict>(),
            card => card._textblock.Text == "Ignore packages from Settings test manager when showing a notification about updates");
        Assert.True(negative.ForceInversion);
        Assert.False(negative._checkbox.IsChecked);
        host.Space(negative._checkbox);
        Assert.True(negative._checkbox.IsChecked);
        Assert.True(CoreSettings.GetDictionaryItem<string, bool>(CoreSettings.K.DisabledPackageManagerNotifications, manager.Name));
        host.Space(negative._checkbox);
        Assert.False(negative._checkbox.IsChecked);
        Assert.False(CoreSettings.GetDictionaryItem<string, bool>(CoreSettings.K.DisabledPackageManagerNotifications, manager.Name));
        AssertOtherPreferences();
        Assert.Empty(page.GetVisualDescendants().OfType<AgentSkillsSettingsControl>());
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<ToggleSwitch>(),
            toggle => AutomationProperties.GetName(toggle) == "Notify me about skill updates");
        return true;
    }, CancellationToken.None);
}
