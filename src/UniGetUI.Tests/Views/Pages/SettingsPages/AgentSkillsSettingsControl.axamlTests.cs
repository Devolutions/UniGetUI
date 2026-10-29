using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Devolutions.AgentSkills;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using static UniGetUI.Tests.AgentSkillsSettingsTestFixture;

namespace UniGetUI.Tests;

public sealed class AgentSkillsSettingsControlTests
{
    [Fact]
    public Task SkillsShToggle_FailedDiskSaveShowsLiveErrorRollsBackAndKeyboardRetrySucceeds() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service(hybrid: true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var toggle = Named<ToggleSwitch>(control, "SkillsShDiscoveryToggle");
        var error = Named<TextBlock>(control, "SkillsShDiscoveryErrorText");
        Assert.False(toggle.IsChecked);
        Assert.False(error.IsVisible);
        service.AssertNoHydrationWrites();
        string settingFile = Path.Combine(UniGetUI.Core.Data.CoreData.UniGetUIUserConfigurationDirectory,
            UniGetUI.Core.SettingsEngine.Settings.ResolveKey(UniGetUI.Core.SettingsEngine.Settings.K.SkillsShDiscoveryEnabled));
        Directory.CreateDirectory(settingFile);

        host.Space(toggle);

        Assert.False(toggle.IsChecked);
        Assert.False(vm.SkillsShDiscoveryEnabled);
        Assert.False(UniGetUI.Core.SettingsEngine.Settings.Get(UniGetUI.Core.SettingsEngine.Settings.K.SkillsShDiscoveryEnabled));
        Assert.False(File.Exists(settingFile));
        Assert.Equal([true], service.SkillsShDiscoveryWrites);
        Assert.Equal("The skills.sh discovery preference could not be saved. Check your settings folder permissions and try again.",
            error.Text);
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
        Directory.Delete(settingFile);

        host.Space(toggle);

        Assert.True(toggle.IsChecked);
        Assert.True(vm.SkillsShDiscoveryEnabled);
        Assert.True(File.Exists(settingFile));
        Assert.Equal([true, true], service.SkillsShDiscoveryWrites);
        Assert.False(error.IsVisible);
        Assert.Equal("", error.Text);
        Assert.Empty(service.TargetWrites);
        Assert.Empty(service.NotificationWrites);
        Assert.Empty(service.VersionWrites);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SkillsShToggle_KeyboardPersistsAcrossReopenWithAccessiblePrivacyAndRetentionHelp(bool enabled) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"], disabled: true);
        UniGetUI.Core.SettingsEngine.Settings.Set(UniGetUI.Core.SettingsEngine.Settings.K.SkillsShDiscoveryEnabled, enabled);
        var service = fixture.Service(hybrid: true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using (var host = new AgentSkillsSettingsTestWindow(control))
        {
            var toggle = Named<ToggleSwitch>(control, "SkillsShDiscoveryToggle");
            Assert.Equal(enabled, toggle.IsChecked);
            Assert.Equal("Search skills.sh", AutomationProperties.GetName(toggle));
            Assert.True(toggle.Focusable);
            Assert.True(toggle.IsEffectivelyVisible);
            service.AssertNoHydrationWrites();
            var discoveryHelp = Assert.Single(control.GetVisualDescendants().OfType<Expander>(),
                expander => Equals(expander.Header, "How discovery works"));
            Assert.False(discoveryHelp.IsExpanded);
            discoveryHelp.IsExpanded = true;
            host.Pump();
            foreach (string help in new[]
            {
                "Off by default. When enabled, searches of at least two characters also send the search text to skills.sh. No sign-in is required.",
                "Installing a result saves its source so updates keep working when skills.sh discovery is off. Review skills before installing them.",
            })
            {
                var text = Assert.Single(control.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == help);
                Assert.True(text.IsEffectivelyVisible);
                Assert.True(text.Bounds.Height > 0);
            }

            host.Space(toggle);

            Assert.Equal(!enabled, toggle.IsChecked);
            Assert.Equal(!enabled, vm.SkillsShDiscoveryEnabled);
            Assert.Equal([!enabled], service.SkillsShDiscoveryWrites);
            Assert.Equal(!enabled, UniGetUI.Core.SettingsEngine.Settings.Get(
                UniGetUI.Core.SettingsEngine.Settings.K.SkillsShDiscoveryEnabled));
            Assert.Empty(service.TargetWrites);
            Assert.Empty(service.NotificationWrites);
            Assert.Empty(service.VersionWrites);
            Assert.Equal(0, service.SignOutCalls);
            Assert.Equal(0, service.SourceRefreshes);
            Assert.Equal(["detected-a"], service.TargetAgents);
            Assert.True(Disabled);
            AssertOtherPreferences();
        }
        var reopenedService = fixture.Service(hybrid: true);
        var reopened = new AgentSkillsSettingsControl(new AgentSkillsSettingsViewModel(reopenedService));
        using var reopenedHost = new AgentSkillsSettingsTestWindow(reopened);
        Assert.Equal(!enabled, Named<ToggleSwitch>(reopened, "SkillsShDiscoveryToggle").IsChecked);
        reopenedService.AssertNoHydrationWrites();
        AssertOtherPreferences();
    }, CancellationToken.None);

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentSkillsSettingsControlTests).Assembly);

    private static T Named<T>(Control control, string name) where T : Control =>
        control.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing named control {name}.");

    private static CheckBox Checkbox(Control control, string id) =>
        Assert.Single(control.GetVisualDescendants().OfType<CheckBox>(),
            box => box.DataContext is SkillTargetViewModel row && row.Id == id);

    private static (Control Card, SourceManagerCardViewModel Sources, AgentSkillsSettingsLocalManager Manager) SourceCard()
    {
        var manager = new AgentSkillsSettingsLocalManager();
        var sources = new SourceManagerCardViewModel(manager);
        Assert.False(sources.IsLoading);
        return (new Border
        {
            DataContext = sources,
            Child = new TextBlock { Text = "Local repository source", TextWrapping = TextWrapping.Wrap },
        }, sources, manager);
    }

    [Fact]
    public Task DedicatedControl_RendersOrderedSectionsAndInjectedSource() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var source = SourceCard();
        var control = new AgentSkillsSettingsControl(vm, source.Card);
        using var host = new AgentSkillsSettingsTestWindow(control);
        Assert.Same(source.Card, Named<ContentControl>(control, "SourcesHolder").Content);
        var sections = Assert.IsType<StackPanel>(control.Content).Children
            .Where(child => child is Expander || child is TextBlock text && text.Classes.Contains("section-heading"))
            .Select(child => child is TextBlock text ? text.Text : AutomationProperties.GetName(child)).ToArray();
        Assert.Equal(["Sources", "Online discovery", "Installation targets", "Notifications", "Notion integration (optional)"], sections);
        Assert.Single(control.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Sources");
        var cards = Assert.IsType<StackPanel>(control.Content).Children.OfType<Border>().ToArray();
        Assert.Equal(4, cards.Length);
        Assert.All(cards, card =>
        {
            Assert.Equal(cards[0].Bounds.X, card.Bounds.X);
            Assert.Equal(cards[0].Bounds.Width, card.Bounds.Width);
            Assert.True(card.Bounds.Width > 0);
        });
        var notion = Named<Expander>(control, "NotionExpander");
        var advanced = Named<Expander>(control, "AdvancedExpander");
        Assert.Contains(notion, advanced.GetLogicalAncestors());
        Assert.Contains(advanced, Named<ComboBox>(control, "VersionPolicySelector").GetLogicalAncestors());
        Assert.False(advanced.IsExpanded);
        Assert.DoesNotContain(advanced, control.GetVisualDescendants());
        Assert.Empty(control.GetVisualDescendants().OfType<InstallOptionsPanel>());
        Assert.Empty(control.GetVisualDescendants().OfType<CheckboxCard_Dict>());
        Assert.DoesNotContain(control.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetName(combo) == "Minimum age for updates");
        Assert.False(Named<Expander>(control, "NotionExpander").IsExpanded);
        var summary = Assert.Single(Named<Expander>(control, "NotionExpander").GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Not installed. Only Notion sources need this integration.");
        Assert.True(summary.IsEffectivelyVisible);
        Assert.True(summary.Bounds.Height > 0);
        Assert.True(source.Card.IsEffectivelyVisible);
        Assert.Equal(1, service.StatusReads);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NoAvailableTargets_ShowsAutomaticSharedDirectoryExplanation(bool knownUndetected) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.Catalogue = knownUndetected ? [fixture.Catalogue[2]] : [];
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var selector = Named<ComboBox>(control, "TargetModeSelector");
        Assert.Equal(["Automatic (recommended)"], selector.Items.Cast<string>());
        Assert.Equal(0, selector.SelectedIndex);
        Assert.Empty(control.GetVisualDescendants().OfType<CheckBox>());
        var explanation = Assert.Single(control.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "No coding agents were detected. Automatic mode uses the shared .agents/skills folder.");
        Assert.True(explanation.IsEffectivelyVisible);
        Assert.True(explanation.Bounds.Height > 0);
        service.AssertNoHydrationWrites();
    }, CancellationToken.None);

    [Fact]
    public Task TargetControls_KeyboardEditsPersistAndRecoverEmptyDraft() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        // SelectedIndex changes deliberately test the selector's two-way binding.
        var selector = Named<ComboBox>(control, "TargetModeSelector");
        selector.SelectedIndex = 0;
        host.Pump();
        Assert.Empty(service.TargetAgents);
        selector.SelectedIndex = 1;
        host.Pump();
        Assert.True(vm.ChooseAgents);
        Assert.Equal(["detected-a"], service.TargetAgents);
        host.Space(Checkbox(control, "detected-a")); // Actual keyboard input, not a raised event.
        Assert.Empty(Selected(vm));
        Assert.Equal(["detected-a"], service.TargetAgents);
        var error = Named<TextBlock>(control, "TargetErrorText");
        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(TargetMessage, error.Text);
        host.Space(Checkbox(control, "detected-b"));
        Assert.Equal(["detected-b"], Selected(vm));
        Assert.Equal(["detected-b"], service.TargetAgents);
        Assert.False(error.IsVisible);
        Assert.Equal("", error.Text);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NotificationToggle_KeyboardPersistsInversePreferenceAcrossReopen(bool disabled) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(disabled: disabled);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using (var host = new AgentSkillsSettingsTestWindow(control))
        {
            var toggle = Named<ToggleSwitch>(control, "NotificationsToggle");
            Assert.Equal(!disabled, toggle.IsChecked);
            service.AssertNoHydrationWrites();
            host.Space(toggle);
            Assert.Equal(disabled, toggle.IsChecked);
            Assert.Equal(disabled, vm.NotificationsEnabled);
            Assert.Equal(!disabled, Disabled);
            Assert.Equal([disabled], service.NotificationWrites);
            AssertOtherPreferences();
        }
        var reopenedService = fixture.Service(true);
        var reopened = new AgentSkillsSettingsControl(new AgentSkillsSettingsViewModel(reopenedService));
        using var reopenedHost = new AgentSkillsSettingsTestWindow(reopened);
        Assert.Equal(disabled, Named<ToggleSwitch>(reopened, "NotificationsToggle").IsChecked);
        reopenedService.AssertNoHydrationWrites();
        AssertOtherPreferences();
    }, CancellationToken.None);

    [Fact]
    public Task VersionEditor_KeyboardValidationControlsPersistenceAndInstall() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed();
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service) { IsNotionExpanded = true, IsAdvancedExpanded = true };
        int installs = 0;
        var control = new AgentSkillsSettingsControl(vm, install: () => { installs++; return Task.CompletedTask; });
        using var host = new AgentSkillsSettingsTestWindow(control);
        var selector = Named<ComboBox>(control, "VersionPolicySelector");
        // Programmatic selector binding check; editing below uses real text input.
        selector.SelectedIndex = 0;
        selector.SelectedIndex = 1;
        host.Pump();
        var input = Named<TextBox>(control, "VersionInput");
        Assert.True(input.IsEffectivelyVisible);
        host.EnterText(input, "0.23.8");
        service.VersionWrites.Clear();
        var before = Snapshot();
        host.EnterText(input, "latest");
        Assert.Equal("latest", vm.VersionText);
        Assert.Equal(VersionMessage, Named<TextBlock>(control, "VersionErrorText").Text);
        Assert.True(Named<TextBlock>(control, "VersionErrorText").IsEffectivelyVisible);
        var install = Named<Button>(control, "InstallNotionButton");
        Assert.False(install.IsEnabled);
        int reads = service.StatusReads;
        host.Click(install); // Disabled input must not invoke the Click handler.
        Assert.Equal(0, installs);
        Assert.Equal(reads, service.StatusReads);
        Assert.Equal(0, service.SourceRefreshes);
        Assert.Empty(service.VersionWrites);
        before.AssertUnchanged();
        host.EnterText(input, "0.23.9");
        Assert.Equal(["0.23.9"], service.VersionWrites);
        Assert.Equal("0.23.9", service.SavedVersion);
        Assert.Equal("0.23.9", vm.VersionSummary);
        Assert.False(Named<TextBlock>(control, "VersionErrorText").IsVisible);
        Assert.True(install.IsEnabled);
    }, CancellationToken.None);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task NotionActionControls_InvokeOnceRefreshAndRecoverFromFailure(int kind) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.HasNotionSources = true;
        service.DefaultStatus = Status(kind == 0 ? NotionCliState.NotInstalled :
            kind == 1 ? NotionCliState.SignedOut : NotionCliState.SignedIn, "Initial");
        var vm = new AgentSkillsSettingsViewModel(service) { IsNotionExpanded = true };
        var gate = Gate<bool>();
        int callbacks = 0;
        Task Action()
        {
            callbacks++;
            if (kind != 2) service.Operations.Add("callback");
            return callbacks == 1 ? gate.Task : Task.CompletedTask;
        }
        service.SignOut = Action;
        var control = new AgentSkillsSettingsControl(vm, install: Action, signIn: Action);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var action = Named<Button>(control, kind == 0 ? "InstallNotionButton" : kind == 1 ? "SignInButton" : "SignOutButton");
        var refresh = Named<Button>(control, "RefreshNotionButton");
        try
        {
            Assert.True(action.IsEffectivelyVisible);
            service.Operations.Clear();
            host.Space(action);
            Assert.True(vm.IsBusy);
            Assert.False(action.IsEnabled);
            Assert.False(refresh.IsEnabled);
            foreach (string name in new[] { "InstallNotionButton", "SignInButton", "SignOutButton" })
                Assert.False(Named<Button>(control, name).IsEnabled);
            host.Click(action);
            host.Click(refresh);
            Assert.Equal(1, callbacks);
            Assert.Equal(kind == 2 ? 1 : 0, service.SignOutCalls);
            Assert.Equal(1, service.StatusReads);
            Assert.Equal(0, service.SourceRefreshes);
            Task failed = WhenIdle(vm); // Observe the async-void handler's completion boundary.
            gate.SetException(new InvalidOperationException("UI action failed"));
            await failed;
            host.Pump();
            var error = Named<TextBlock>(control, "NotionErrorText");
            Assert.Equal("Could not complete the Notion action: UI action failed", error.Text);
            Assert.True(error.IsEffectivelyVisible);
            Assert.True(action.IsEnabled);
            service.DefaultStatus = Status(kind == 2 ? NotionCliState.SignedOut : NotionCliState.SignedIn, "Recovered");
            service.Operations.Clear();
            host.Space(action);
            await WhenIdle(vm);
            host.Pump();
            Assert.Equal(2, callbacks);
            Assert.Equal(kind == 2 ? 2 : 0, service.SignOutCalls);
            Assert.Equal([kind == 2 ? "sign-out" : "callback", "sources", "status"], service.Operations);
            Assert.Equal(1, service.SourceRefreshes);
            Assert.Equal(2, service.StatusReads);
            Assert.False(error.IsVisible);
            Assert.Equal(kind == 2, Named<Button>(control, "SignInButton").IsVisible);
            Assert.Equal(kind != 2, Named<Button>(control, "SignOutButton").IsVisible);
            host.Space(refresh);
            await WhenIdle(vm);
            Assert.Equal(3, service.StatusReads);
            Assert.Equal(2, callbacks);
        }
        finally
        {
            gate.TrySetResult(true);
            await WhenIdle(vm);
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditVersion_OpensAdvancedAndFocusesCorrectEditor(bool specific) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.StoredVersion = specific ? "invalid pin" : "";
        var vm = new AgentSkillsSettingsViewModel(service) { IsNotionExpanded = true, IsAdvancedExpanded = false };
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        Assert.False(Named<Expander>(control, "AdvancedExpander").IsExpanded);
        if (specific) Assert.False(Named<Button>(control, "InstallNotionButton").IsEnabled);
        host.Space(Named<Button>(control, "EditVersionButton"));
        Assert.True(vm.IsAdvancedExpanded);
        Assert.True(vm.IsNotionExpanded);
        Assert.True(Named<Expander>(control, "NotionExpander").IsExpanded);
        Assert.True(Named<Expander>(control, "AdvancedExpander").IsExpanded);
        Assert.True(specific ? Named<TextBox>(control, "VersionInput").IsFocused :
            Named<ComboBox>(control, "VersionPolicySelector").IsFocused);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task VersionEditRequest_ExpandsBothCollapsedSectionsAndFocusesEditor(bool specific) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.StoredVersion = specific ? "0.23.9" : "";
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        Assert.False(vm.IsNotionExpanded);
        Assert.False(vm.IsAdvancedExpanded);
        // Exercise the routed edit request before attachment, when both ancestors are collapsed.
        // Real keyboard activation of the visible edit button is covered separately above.
        Named<Button>(control, "EditVersionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var host = new AgentSkillsSettingsTestWindow(control);
        Assert.True(vm.IsNotionExpanded);
        Assert.True(vm.IsAdvancedExpanded);
        Assert.True(Named<Expander>(control, "NotionExpander").IsExpanded);
        Assert.True(Named<Expander>(control, "AdvancedExpander").IsExpanded);
        Assert.True(specific ? Named<TextBox>(control, "VersionInput").IsFocused :
            Named<ComboBox>(control, "VersionPolicySelector").IsFocused);
        service.AssertNoHydrationWrites();
    }, CancellationToken.None);

    [Fact]
    public Task DetachedEditVersion_DoesNotApplyPostedFocus() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var vm = new AgentSkillsSettingsViewModel(fixture.Service()) { IsNotionExpanded = true };
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var edit = Named<Button>(control, "EditVersionButton");
        edit.BringIntoView();
        host.Pump();
        Assert.True(edit.Focus());
        host.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        host.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Assert.True(vm.IsAdvancedExpanded);
        // Detach before running the focus callback posted by the real input handler.
        host.Scroll.Content = null;
        var keeper = new TextBox { Text = "New focus owner" };
        host.Window.Content = keeper;
        host.Window.UpdateLayout();
        Assert.True(keeper.Focus());
        host.Pump();
        Assert.True(keeper.IsFocused);
        Assert.False(Named<TextBox>(control, "VersionInput").IsFocused);
        Assert.False(Named<ComboBox>(control, "VersionPolicySelector").IsFocused);
        Assert.False(vm.CanAct);
    }, CancellationToken.None);

    [Fact]
    public Task AccessibleControls_ExposeExactNamesAndAssertiveErrors() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service) { IsNotionExpanded = true, IsAdvancedExpanded = true };
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        (string Name, string Expected)[] names =
        [
            ("TargetModeSelector", "Installation targets"),
            ("NotionExpander", "Notion integration (optional)"),
            ("InstallNotionButton", "Install the Notion CLI"),
            ("SignInButton", "Sign in"),
            ("SignOutButton", "Sign out"),
            ("RefreshNotionButton", "Check connection"),
            ("EditVersionButton", "Notion CLI version policy"),
            ("VersionPolicySelector", "Notion CLI version policy"),
            ("NotificationsToggle", "Notify me about skill updates"),
            ("AdvancedExpander", "Advanced options"),
            ("VersionInput", "Specific Notion CLI version"),
        ];
        foreach (var (name, expected) in names)
            Assert.Equal(expected, AutomationProperties.GetName(Named<Control>(control, name)));
        Assert.Equal("Detected A", AutomationProperties.GetName(Checkbox(control, "detected-a")));
        var announcements = new List<AccessibilityAnnouncement>();
        void Collect(object? sender, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            Row(vm, "detected-a").IsSelected = false;
            vm.VersionText = "!";
            service.StatusQueue.Enqueue(Task.FromException<NotionStatus>(new InvalidOperationException("connection failed")));
            await vm.RefreshAsync();
            host.Pump();
            string[] errors = [TargetMessage, VersionMessage, "connection failed"];
            string[] controls = ["TargetErrorText", "VersionErrorText", "NotionErrorText"];
            for (int i = 0; i < controls.Length; i++)
            {
                var text = Named<TextBlock>(control, controls[i]);
                Assert.Equal(errors[i], text.Text);
                Assert.True(text.IsEffectivelyVisible);
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(text));
            }
            Assert.Equal(errors, announcements.Select(item => item.Message));
            Assert.All(announcements, item => Assert.Equal(AutomationLiveSetting.Assertive, item.LiveSetting));
            host.Detach();
            announcements.Clear();
            vm.TargetError = "detached target";
            vm.VersionError = "detached version";
            vm.NotionError = "detached notion";
            host.Pump();
            Assert.Empty(announcements);
        }
        finally { AccessibilityAnnouncementService.AnnouncementRequested -= Collect; }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SourceEvents_RefreshOnlyAfterLoadingAndWhileAttached() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var source = SourceCard();
        var control = new AgentSkillsSettingsControl(vm, source.Card);
        using var host = new AgentSkillsSettingsTestWindow(control);
        Assert.Equal(1, service.StatusReads);
        source.Sources.IsLoading = true;
        source.Sources.Sources.Add(source.Manager.DefaultSource);
        host.Pump();
        Assert.Equal(1, service.StatusReads);
        source.Sources.IsLoading = false;
        host.Pump();
        Assert.Equal(2, service.StatusReads);
        source.Sources.Sources.Add(source.Manager.DefaultSource);
        host.Pump();
        Assert.Equal(3, service.StatusReads);
        source.Sources.Sources.RemoveAt(0);
        host.Pump();
        Assert.Equal(4, service.StatusReads);
        source.Sources.Sources.Add(source.Manager.DefaultSource); // Queue, then detach before dispatch.
        host.Detach();
        Assert.Equal(4, service.StatusReads);
        source.Sources.Sources.Clear();
        source.Sources.IsLoading = true;
        source.Sources.IsLoading = false;
        host.Pump();
        Assert.Equal(4, service.StatusReads);
        Assert.False(vm.CanAct);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DetachDuringPendingWork_DropsEventsAnnouncementsAndLateResults(bool action) => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var source = SourceCard();
        var control = new AgentSkillsSettingsControl(vm, source.Card);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var statusGate = Gate<NotionStatus>();
        var actionGate = Gate<bool>();
        if (!action) service.StatusQueue.Enqueue(statusGate.Task);
        Task pending = action ? vm.RunNotionActionAsync(() => actionGate.Task) : vm.RefreshAsync();
        var announcements = new List<AccessibilityAnnouncement>();
        void Collect(object? sender, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            if (!action)
            {
                host.Pump();
                var checking = Assert.Single(Named<Expander>(control, "NotionExpander").GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Checking the Notion CLI…");
                Assert.True(checking.IsEffectivelyVisible);
                Assert.True(checking.Bounds.Height > 0);
            }
            await vm.RefreshAsync(); // A queued refresh must not survive detachment either.
            host.Detach();
            string summary = vm.NotionSummary;
            var state = vm.NotionState;
            int reads = service.StatusReads;
            source.Sources.Sources.Add(source.Manager.DefaultSource);
            source.Sources.IsLoading = true;
            source.Sources.IsLoading = false;
            vm.NotionError = "detached error";
            statusGate.TrySetResult(Status(NotionCliState.SignedIn, "Stale workspace"));
            actionGate.TrySetResult(true);
            await pending;
            host.Pump();
            Assert.Equal(summary, vm.NotionSummary);
            Assert.Equal(state, vm.NotionState);
            Assert.Equal("detached error", vm.NotionError);
            Assert.Equal(reads, service.StatusReads);
            Assert.Equal(0, service.SourceRefreshes);
            Assert.Empty(announcements);
            Assert.False(vm.IsBusy);
            Assert.False(vm.CanAct);
            Assert.False(vm.CanInstall);
        }
        finally
        {
            statusGate.TrySetResult(Status());
            actionGate.TrySetResult(true);
            await pending;
            AccessibilityAnnouncementService.AnnouncementRequested -= Collect;
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Reattach_RefreshesAndSubscribesExactlyOnce() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        var vm = new AgentSkillsSettingsViewModel(service);
        var source = SourceCard();
        var control = new AgentSkillsSettingsControl(vm, source.Card);
        using var host = new AgentSkillsSettingsTestWindow(control);
        var gate = Gate<NotionStatus>();
        service.StatusQueue.Enqueue(gate.Task);
        Task old = vm.RefreshAsync();
        var announcements = new List<AccessibilityAnnouncement>();
        void Collect(object? sender, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            host.Detach();
            source.Sources.Sources.Add(source.Manager.DefaultSource);
            host.Pump();
            Assert.Equal(2, service.StatusReads);
            service.DefaultStatus = Status(NotionCliState.SignedIn, "Reattached");
            host.Attach(control);
            Assert.Equal(3, service.StatusReads);
            Assert.Equal("Connected to Reattached, CLI 0.23.9", vm.NotionSummary);
            gate.SetResult(Status(NotionCliState.SignedOut));
            await old;
            host.Pump();
            Assert.Equal("Connected to Reattached, CLI 0.23.9", vm.NotionSummary);
            Assert.True(vm.ShowSignOut);
            source.Sources.Sources.Add(source.Manager.DefaultSource);
            host.Pump();
            Assert.Equal(4, service.StatusReads);
            vm.NotionError = "one attached announcement";
            host.Pump();
            Assert.Equal(["one attached announcement"], announcements.Select(item => item.Message));
            host.Detach();
            vm.NotionError = "no detached announcement";
            source.Sources.IsLoading = true;
            source.Sources.IsLoading = false;
            host.Pump();
            Assert.Equal(4, service.StatusReads);
            Assert.Single(announcements);
        }
        finally
        {
            gate.TrySetResult(Status());
            await old;
            AccessibilityAnnouncementService.AnnouncementRequested -= Collect;
        }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(720, false)]
    [InlineData(720, true)]
    [InlineData(1200, false)]
    [InlineData(1200, true)]
    public Task Layout_ExpandedContentFitsViewportAcrossWidthsAndThemes(int width, bool dark) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"]);
        var service = fixture.Service(true);
        service.Catalogue =
        [
            fixture.Catalogue[0] with { DisplayName = "Detected coding agent with a long descriptive name for enterprise workspaces" },
            fixture.Catalogue[1],
        ];
        service.DefaultStatus = Status(NotionCliState.SignedIn,
            "A long workspace name with several teams and projects that must remain readable at a narrow window width");
        var vm = new AgentSkillsSettingsViewModel(service) { IsNotionExpanded = true, IsAdvancedExpanded = true };
        var source = new TextBlock
        {
            Text = "A local repository source with a long descriptive title and explanatory text that wraps without horizontal scrolling",
            TextWrapping = TextWrapping.Wrap,
        };
        var control = new AgentSkillsSettingsControl(vm, source);
        using var host = new AgentSkillsSettingsTestWindow(control, width, dark ? ThemeVariant.Dark : ThemeVariant.Light);
        vm.TargetError = "Select at least one coding agent, or choose Automatic.";
        vm.VersionText = "not a version";
        vm.NotionError = "Connection details are unavailable. Retry the connection check or inspect Agent Skills logs for recovery.";
        host.Pump();
        Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, control.ActualThemeVariant);
        Assert.True(host.Scroll.Viewport.Width > 0);
        Assert.True(host.Scroll.Extent.Height > host.Scroll.Viewport.Height);
        string[] names = ["TargetModeSelector", "TargetErrorText", "NotionExpander", "NotionErrorText",
            "NotificationsToggle", "AdvancedExpander", "VersionPolicySelector", "VersionInput", "VersionErrorText"];
        foreach (string name in names)
            AssertFits(Named<Control>(control, name), host.Scroll);
        AssertFits(source, host.Scroll);
        AssertFits(Checkbox(control, "detected-a"), host.Scroll);
        var wrapped = control.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible &&
                (text.Classes.Contains("help") || text.Classes.Contains("validation-error"))).ToArray();
        Assert.NotEmpty(wrapped);
        foreach (var text in wrapped)
        {
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            AssertFits(text, host.Scroll);
            // DesiredSize includes the outer margin; Bounds is the allocated content box.
            double desiredContentHeight = text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom;
            Assert.True(text.Bounds.Height >= desiredContentHeight - 1,
                $"Wrapped text was vertically clipped: {text.Text}; bounds={text.Bounds.Height}, content={desiredContentHeight}");
            Assert.True(text.Bounds.Height >= text.TextLayout.Height + text.Padding.Top + text.Padding.Bottom - 1,
                $"Wrapped lines exceed the text content box: {text.Text}");
        }
        var input = Named<TextBox>(control, "VersionInput");
        input.BringIntoView();
        host.Pump();
        Assert.True(input.Focus());
        Assert.True(input.IsFocused);
        Point inputPosition = input.TranslatePoint(default, host.Scroll)!.Value;
        Assert.InRange(inputPosition.Y, -1, host.Scroll.Viewport.Height - input.Bounds.Height + 1);
        var refresh = Named<Button>(control, "RefreshNotionButton");
        refresh.BringIntoView();
        host.Pump();
        Assert.True(refresh.IsEnabled);
        Assert.True(refresh.Focus());
        Assert.True(refresh.IsFocused);
    }, CancellationToken.None);

    private static void AssertFits(Control control, ScrollViewer viewport)
    {
        Assert.True(control.IsEffectivelyVisible, $"Not visible: {control.Name}");
        Assert.True(double.IsFinite(control.Bounds.Width) && control.Bounds.Width > 0, $"Invalid width: {control.Name}");
        Assert.True(double.IsFinite(control.Bounds.Height) && control.Bounds.Height > 0, $"Invalid height: {control.Name}");
        Point position = control.TranslatePoint(default, viewport)!.Value;
        Assert.True(position.X >= -1 && position.X + control.Bounds.Width <= viewport.Viewport.Width + 1,
            $"{control.Name ?? control.GetType().Name} exceeds viewport: x={position.X}, width={control.Bounds.Width}, viewport={viewport.Viewport.Width}");
    }
}
