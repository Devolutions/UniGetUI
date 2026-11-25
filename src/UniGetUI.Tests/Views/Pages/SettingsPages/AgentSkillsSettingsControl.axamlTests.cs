using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
            Assert.Empty(control.GetVisualDescendants().OfType<Expander>());
            foreach (string help in new[]
            {
                "Include the public skills.sh catalog in Discover. Search text is sent to skills.sh; no sign-in is required. Off by default.",
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
        var body = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(control.Content).Content);
        var sections = body.Children.OfType<TextBlock>()
            .Where(text => text.Classes.Contains("section-heading")).Select(text => text.Text).ToArray();
        Assert.Equal(["Sources", "Online discovery", "Installation targets", "Notifications", "Notion integration (optional)"], sections);
        Assert.Single(control.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Sources");
        Assert.Equal(["SkillsShDiscoveryCard", "TargetModeCard", "NotificationsCard", "NotionCard", "NotionVersionCard"],
            body.Children.OfType<SettingsCard>().Select(card => card.Name));
        Assert.Empty(control.GetVisualDescendants().OfType<Expander>());
        Assert.All(CardSurfaces(body), card => Assert.True(card.Bounds.Width > 0));
        Assert.Contains(Named<SettingsCard>(control, "NotionVersionCard"),
            Named<ComboBox>(control, "VersionPolicySelector").GetLogicalAncestors());
        Assert.Empty(control.GetVisualDescendants().OfType<InstallOptionsPanel>());
        Assert.Empty(control.GetVisualDescendants().OfType<CheckboxCard_Dict>());
        Assert.DoesNotContain(control.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetName(combo) == "Minimum age for updates");
        var summary = Assert.Single(Named<SettingsCard>(control, "NotionCard").GetVisualDescendants().OfType<TextBlock>(),
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
        var vm = new AgentSkillsSettingsViewModel(service);
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
        var vm = new AgentSkillsSettingsViewModel(service);
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
    public Task VersionEditor_IsShownWithoutExpandingAnything(bool invalid) => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.StoredVersion = invalid ? "invalid pin" : "";
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        Assert.True(Named<ComboBox>(control, "VersionPolicySelector").IsEffectivelyVisible);
        Assert.Equal(invalid, Named<TextBox>(control, "VersionInput").IsEffectivelyVisible);
        Assert.Equal(invalid, Named<TextBlock>(control, "VersionErrorText").IsEffectivelyVisible);
        Assert.Equal(!invalid, Named<Button>(control, "InstallNotionButton").IsEnabled);
        Assert.Empty(control.GetVisualDescendants().OfType<Expander>());
        service.AssertNoHydrationWrites();
    }, CancellationToken.None);

    [Fact]
    public Task AccessibleControls_ExposeExactNamesAndAssertiveErrors() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        fixture.Seed(["detected-a"]);
        var service = fixture.Service(true);
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        (string Name, string Expected)[] names =
        [
            ("TargetModeSelector", "Installation targets"),
            ("SkillsShDiscoveryCard", "Search skills.sh"),
            ("TargetModeCard", "Install for these agents"),
            ("NotificationsCard", "Notify me about skill updates"),
            ("NotionVersionCard", "Notion CLI version policy"),
            ("InstallNotionButton", "Install the Notion CLI"),
            ("SignInButton", "Sign in"),
            ("SignOutButton", "Sign out"),
            ("RefreshNotionButton", "Check connection"),
            ("VersionPolicySelector", "Notion CLI version policy"),
            ("NotificationsToggle", "Notify me about skill updates"),
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
            Assert.Equal(errors, announcements
                .Where(item => item.LiveSetting == AutomationLiveSetting.Assertive).Select(item => item.Message));
            Assert.Equal(["Checking the Notion CLI…", "Could not check the Notion connection."], announcements
                .Where(item => item.LiveSetting == AutomationLiveSetting.Polite).Select(item => item.Message));
            Assert.Equal(5, announcements.Count);
            host.Detach();
            announcements.Clear();
            vm.TargetError = "detached target";
            vm.VersionError = "detached version";
            vm.NotionError = "detached notion";
            vm.NotionSummary = "detached summary";
            host.Pump();
            Assert.Empty(announcements);
        }
        finally { AccessibilityAnnouncementService.AnnouncementRequested -= Collect; }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ConnectionCheck_AnnouncesTheNotionStatusPolitely() => _session.Dispatch(async () =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var service = fixture.Service();
        service.DefaultStatus = Status(NotionCliState.SignedIn, "Workspace");
        var vm = new AgentSkillsSettingsViewModel(service);
        var control = new AgentSkillsSettingsControl(vm);
        using var host = new AgentSkillsSettingsTestWindow(control);
        await WhenIdle(vm);
        host.Pump();
        var announcements = new List<AccessibilityAnnouncement>();
        void Collect(object? sender, AccessibilityAnnouncement announcement) => announcements.Add(announcement);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            var refresh = Named<Button>(control, "RefreshNotionButton");
            host.Space(refresh);
            await WhenIdle(vm);
            host.Pump();
            Assert.Equal(["Checking the Notion CLI…", "Connected to Workspace, CLI 0.23.9"], announcements.Select(item => item.Message));
            Assert.All(announcements, item => Assert.Equal(AutomationLiveSetting.Polite, item.LiveSetting));
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
                var checking = Assert.Single(Named<SettingsCard>(control, "NotionCard").GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Text == "Checking the Notion CLI…");
                Assert.True(checking.IsEffectivelyVisible);
                Assert.True(checking.Bounds.Height > 0);
            }
            await vm.RefreshAsync(); // A queued refresh must not survive detachment either.
            announcements.Clear();
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
            Assert.Equal(["one attached announcement"], announcements
                .Where(item => item.LiveSetting == AutomationLiveSetting.Assertive).Select(item => item.Message));
            Assert.Equal(["Checking the Notion CLI…", "Connected to Reattached, CLI 0.23.9", "Checking the Notion CLI…", "Connected to Reattached, CLI 0.23.9"],
                announcements.Where(item => item.LiveSetting == AutomationLiveSetting.Polite).Select(item => item.Message));
            int attached = announcements.Count;
            host.Detach();
            vm.NotionError = "no detached announcement";
            vm.NotionSummary = "no detached summary";
            source.Sources.IsLoading = true;
            source.Sources.IsLoading = false;
            host.Pump();
            Assert.Equal(4, service.StatusReads);
            Assert.Equal(attached, announcements.Count);
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
        var vm = new AgentSkillsSettingsViewModel(service);
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
        string[] names = ["TargetModeSelector", "TargetErrorText", "NotionCard", "NotionErrorText",
            "NotificationsToggle", "NotionVersionCard", "VersionPolicySelector", "VersionInput", "VersionErrorText"];
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

    [Fact]
    public Task Layout_ScrollsItselfInsideAFixedHeightHostAndAlignsCardsWithSettingsPages() => _session.Dispatch(() =>
    {
        using var fixture = new AgentSkillsSettingsTestFixture();
        var vm = new AgentSkillsSettingsViewModel(fixture.Service());
        var control = new AgentSkillsSettingsControl(vm, new TextBlock { Text = "Source" });
        AgentLibraryTestEvidence.AddApplicationResources(control);
        using var host = new AgentSkillsSettingsTestWindow(control, 900, height: 400);
        host.Pump();
        var scroll = Assert.IsType<ScrollViewer>(control.Content);
        Assert.Equal(host.Scroll.Viewport.Height, scroll.Viewport.Height, 1);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);

        var refresh = Named<Button>(control, "RefreshNotionButton");
        refresh.BringIntoView();
        host.Pump();
        Assert.True(scroll.Offset.Y > 0);
        Point refreshPosition = refresh.TranslatePoint(default, scroll)!.Value;
        Assert.InRange(refreshPosition.Y, -1, scroll.Viewport.Height - refresh.Bounds.Height + 1);

        var body = Assert.IsType<StackPanel>(scroll.Content);
        var surfaces = CardSurfaces(body);
        Assert.Equal(6, surfaces.Length);
        double cardX = surfaces[0].TranslatePoint(default, scroll)!.Value.X;
        Assert.Equal(40, cardX, 1);
        Assert.All(surfaces, surface =>
        {
            Assert.Equal(cardX, surface.TranslatePoint(default, scroll)!.Value.X, 1);
            Assert.Equal(surfaces[0].Bounds.Width, surface.Bounds.Width, 1);
        });
        Assert.All(body.Children.OfType<TextBlock>().Where(text => text.Classes.Contains("section-heading")),
            heading => Assert.Equal(cardX + 4, heading.TranslatePoint(default, scroll)!.Value.X, 1));
        Assert.Empty(body.GetVisualDescendants().OfType<Expander>());
        Control Surface(string card) => CardSurfaces(body).Single(surface => surface.FindAncestorOfType<SettingsCard>()?.Name == card);
        var notion = Surface("NotionCard");
        double notionBottom = notion.TranslatePoint(new Point(0, notion.Bounds.Height), scroll)!.Value.Y;
        Assert.Equal(notionBottom, Surface("NotionVersionCard").TranslatePoint(default, scroll)!.Value.Y, 1);

        string[] anchors = new[] { "skills.sh", "skills", "notion" }
            .SelectMany(query => SettingsSearchIndex.Search(query, limit: 100, isWindows: true))
            .Where(result => result.PageType == typeof(AgentSkillsSettingsControl) && result.Anchor is not null)
            .Select(result => result.Anchor!).Distinct().ToArray();
        Assert.Contains("SkillsShDiscoveryCard", anchors);
        Assert.Contains("TargetModeCard", anchors);
        Assert.Contains("NotionCard", anchors);
        Assert.Equal("Install for these agents", AutomationProperties.GetName(Named<SettingsCard>(control, "TargetModeCard")));
        Assert.All(anchors, anchor => Named<SettingsCard>(control, anchor));

        Assert.Contains("search-field-standalone", Named<TextBox>(control, "VersionInput").Classes);
        foreach (string name in new[] { "SkillsShDiscoveryToggle", "NotificationsToggle" })
        {
            var toggle = Named<ToggleSwitch>(control, name);
            Assert.Null(toggle.OnContent);
            Assert.Null(toggle.OffContent);
        }
    }, CancellationToken.None);

    private static Control[] CardSurfaces(StackPanel body) => body.Children
        .Where(child => child is not TextBlock)
        .SelectMany(child => child is StackPanel group ? group.Children.AsEnumerable() : [child])
        .Select(child => child is SettingsCard card
            ? card.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("settings-card"))
            : child)
        .ToArray();

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
