using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// Package managers home (desktop <c>ManagersHomepage</c>): one enable toggle and status line per
/// manager, plus a settings button that opens <see cref="ManagerSettingsDialog"/>.
/// </summary>
internal sealed class ManagersPage : UserControl, ITuiPage
{
    private readonly TuiForm _form = new();

    public ManagersPage()
    {
        var root = new DockPanel { LastChildFill = true, Margin = new Avalonia.Thickness(1, 0, 1, 0) };
        var header = new StackPanel();
        header.Children.Add(TuiChrome.PageTitle(CoreTools.Translate("Package managers")));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_form);
        Content = root;
        Build();
    }

    public IReadOnlyList<TuiKeyHint> KeyHints => TuiChrome.Hints(("↑/↓", "Move"), ("Space", "Enable/disable"), ("Enter", "Settings"));

    public IReadOnlyList<TuiAction> Actions =>
        TuiEngine.Managers.Select(m => new TuiAction(CoreTools.Translate("{0} settings", m.DisplayName), null,
            () => ManagerSettingsDialog.ShowAsync(m))).ToList();

    public bool FocusPrimary() => _form.FocusPrimary();

    public void OnShown() => _form.Refresh();

    public void Reload() => _form.Refresh();

    private void Build()
    {
        _form.AddNote(CoreTools.Translate("Enable or disable package managers, and open each one's settings."));
        foreach (IPackageManager manager in TuiEngine.Managers)
        {
            IPackageManager m = manager;
            _form.AddHeader(m.DisplayName);
            _form.AddCheck(CoreTools.Translate("Enable {pm}").Replace("{pm}", m.DisplayName), m.IsEnabled,
                on => _ = SetEnabledAsync(m, on));
            _form.AddLiveNote(() => StatusText(m), TuiPalette.Brand);
            _form.AddButton(CoreTools.Translate("{0} settings", m.DisplayName) + "…", () => ManagerSettingsDialog.ShowAsync(m));
        }
    }

    internal static string StatusText(IPackageManager m)
    {
        if (!m.IsEnabled()) return CoreTools.Translate("Disabled");
        if (m.IsReady())
            return CoreTools.Translate("{pm} is enabled and ready to go").Replace("{pm}", m.DisplayName)
                   + " · " + m.Status.Version.Split('\n')[0].Trim();
        if (!m.Status.Found) return CoreTools.Translate("{pm} was not found!").Replace("{pm}", m.DisplayName);
        return CoreTools.Translate("{pm} could not be loaded").Replace("{pm}", m.DisplayName);
    }

    internal static async Task SetEnabledAsync(IPackageManager manager, bool enabled)
    {
        Settings.SetDictionaryItem(Settings.K.DisabledManagers, manager.Name, !enabled);
        await Task.Run(manager.Initialize);
        _ = InstalledPackagesLoader.Instance.ReloadPackages();
        _ = UpgradablePackagesLoader.Instance.ReloadPackages();
        Dispatcher.UIThread.Post(TuiShell.InvalidateChrome);
        TuiNotifications.Info(manager.DisplayName, StatusText(manager));
    }
}

/// <summary>
/// One package manager's settings (desktop <c>PackageManagerPage</c>): status, executable selection,
/// default install options, update notifications, minimum update age, logs, sources and the
/// manager-specific extras.
/// </summary>
internal sealed class ManagerSettingsDialog : FormDialog
{
    private readonly IPackageManager _manager;
    private IReadOnlyList<IManagerSource> _sources = [];

    private ManagerSettingsDialog(IPackageManager manager)
        : base(CoreTools.Translate("{0} settings", manager.DisplayName), 100, 34)
    {
        _manager = manager;
        Build();
        AddButton(CoreTools.Translate("Close"), () => Close(null));
        _ = LoadSourcesAsync();
    }

    public static Task ShowAsync(IPackageManager manager) => TuiModal.ShowAsync(new ManagerSettingsDialog(manager));

    private void Rebuild() => Form.Rebuild(Build);

    private async Task LoadSourcesAsync()
    {
        if (!_manager.Capabilities.SupportsCustomSources || !_manager.IsReady()) return;
        _sources = await Task.Run(() => _manager.SourcesHelper.GetSources());
        Dispatcher.UIThread.Post(Rebuild);
    }

    private void Build()
    {
        IPackageManager m = _manager;
        Form.AddHeader(CoreTools.Translate("{0} status", m.DisplayName));
        Form.AddCheck(CoreTools.Translate("Enable {pm}").Replace("{pm}", m.DisplayName), m.IsEnabled,
            on => _ = ToggleAsync(on));
        Form.AddLiveNote(() => ManagersPage.StatusText(m), TuiPalette.Brand);
        Form.AddLiveNote(() => CoreTools.Translate("Current executable file:") + " " + (m.Status.ExecutablePath.Length > 0 ? m.Status.ExecutablePath : "—"));
        Form.AddLiveNote(() => CoreTools.Translate("Call arguments") + ": " + m.Status.ExecutableCallArgs);
        Form.AddButton(CoreTools.Translate("Copy path"), () => TuiClipboard.CopyAsync(this, m.DisplayName, m.Status.ExecutablePath));
        Form.AddButton(CoreTools.Translate("Select executable") + "…", SelectExecutableAsync,
            () => SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths));
        if (!SecureSettings.Get(SecureSettings.K.AllowCustomManagerPaths))
            Form.AddNote(CoreTools.Translate("For security reasons, changing the executable file is disabled by default"));
        Form.AddButton(CoreTools.Translate("Reload"), () => ToggleAsync(m.IsEnabled()));

        Form.AddHeader(CoreTools.Translate("Default installation options for {0} packages", m.DisplayName));
        Form.AddButton(CoreTools.Translate("Change default options") + "…", () => InstallOptionsDialog.ShowForManagerAsync(m));

        Form.AddHeader(CoreTools.Translate("{0} settings", m.DisplayName));
        Form.AddCheck(CoreTools.Translate("Ignore packages from {pm} when showing a notification about updates").Replace("{pm}", m.DisplayName),
            () => Settings.GetDictionaryItem<string, bool>(Settings.K.DisabledPackageManagerNotifications, m.Name),
            v => Settings.SetDictionaryItem(Settings.K.DisabledPackageManagerNotifications, m.Name, v));

        Form.AddHeader(CoreTools.Translate("Update security"));
        Form.AddNote(m.Capabilities.KnowsPackageReleaseDate switch
        {
            PackageReleaseDateSupport.No => CoreTools.Translate("{pm} does not provide release dates for its packages, so this setting will have no effect").Replace("{pm}", m.DisplayName),
            PackageReleaseDateSupport.Partial => CoreTools.Translate("{pm} only provides release dates for some of its packages, so this setting will only apply to those packages").Replace("{pm}", m.DisplayName),
            _ => CoreTools.Translate("Override the global minimum update age for this package manager"),
        });
        bool ageSupported = m.Capabilities.KnowsPackageReleaseDate != PackageReleaseDateSupport.No;
        Form.AddChoice(CoreTools.Translate("Minimum age for updates"),
            [
                new(CoreTools.Translate("Use global setting"), ""),
                new(CoreTools.Translate("No minimum age"), "0"),
                new(CoreTools.Translate("1 day"), "1"),
                new(CoreTools.Translate("{0} days", 3), "3"),
                new(CoreTools.Translate("{0} days", 7), "7"),
                new(CoreTools.Translate("{0} days", 14), "14"),
                new(CoreTools.Translate("{0} days", 30), "30"),
                new(CoreTools.Translate("Custom..."), "custom"),
            ],
            () => Settings.GetDictionaryItem<string, string>(Settings.K.PerManagerMinimumUpdateAge, m.Name) ?? "",
            v =>
            {
                if (v.Length == 0) Settings.RemoveDictionaryKey<string, string>(Settings.K.PerManagerMinimumUpdateAge, m.Name);
                else Settings.SetDictionaryItem(Settings.K.PerManagerMinimumUpdateAge, m.Name, v);
            }, () => ageSupported);
        Form.AddText(CoreTools.Translate("Custom minimum age (days)"),
            () => Settings.GetDictionaryItem<string, string>(Settings.K.PerManagerMinimumUpdateAgeCustom, m.Name) ?? "",
            v =>
            {
                string digits = string.Concat(v.Where(char.IsDigit));
                if (digits.Length > 0) Settings.SetDictionaryItem(Settings.K.PerManagerMinimumUpdateAgeCustom, m.Name, digits);
                else Settings.RemoveDictionaryKey<string, string>(Settings.K.PerManagerMinimumUpdateAgeCustom, m.Name);
            }, CoreTools.Translate("e.g. 10"),
            () => ageSupported && Settings.GetDictionaryItem<string, string>(Settings.K.PerManagerMinimumUpdateAge, m.Name) == "custom");

        Form.AddButton(CoreTools.Translate("View {0} logs", m.DisplayName), () =>
        {
            Close(null);
            LogsPage.RequestManager(m);
            TuiShell.Navigate(TuiPageIds.Logs);
        });

        if (m.Capabilities.SupportsCustomSources && m.Name != "vcpkg")
            BuildSources();

        BuildExtras();
    }

    private void BuildSources()
    {
        Form.AddHeader(CoreTools.Translate("Manage sources"));
        if (!_manager.IsReady())
        {
            Form.AddNote(CoreTools.Translate("The manager must be enabled and ready to manage its sources."));
            return;
        }

        if (_sources.Count == 0) Form.AddNote(CoreTools.Translate("Loading…"));
        foreach (IManagerSource source in _sources)
        {
            IManagerSource s = source;
            Form.AddButton($"{CoreTools.Translate("Remove")}  {s.Name}  —  {s.Url}", () => RemoveSourceAsync(s));
        }

        Form.AddButton(CoreTools.Translate("Add source") + "…", AddSourceAsync);
        Form.AddButton(CoreTools.Translate("Reload sources"), LoadSourcesAsync);
    }

    private void BuildExtras()
    {
        switch (_manager.Name)
        {
            case "Winget":
                Form.AddHeader(CoreTools.Translate("Advanced options"));
                Form.AddChoice(CoreTools.Translate("WinGet command-line tool"),
                    [new("Default", "default"), new("WinGet", "winget"), new("Pinget", "pinget")],
                    () => Value(Settings.K.WinGetCliToolPreference, "default"),
                    v =>
                    {
                        Settings.SetValue(Settings.K.WinGetCliToolPreference, v);
                        _ = ToggleAsync(_manager.IsEnabled());
                    });
                Form.AddChoice(CoreTools.Translate("WinGet COM API"),
                    [new("Default", "default"), new("Enabled", "enabled"), new("Disabled", "disabled")],
                    () => Value(Settings.K.WinGetComApiPolicy, "default"),
                    v =>
                    {
                        Settings.SetValue(Settings.K.WinGetComApiPolicy, v);
                        _ = ToggleAsync(_manager.IsEnabled());
                    });
                Form.AddButton(CoreTools.Translate("Reset WinGet") + $" ({CoreTools.Translate("This may help if no packages are listed")})", ResetWinGetAsync);
                Form.AddCheck(CoreTools.Translate("Force install location parameter when updating packages with custom locations"),
                    () => Settings.Get(Settings.K.WinGetForceLocationOnUpdate), v => Settings.Set(Settings.K.WinGetForceLocationOnUpdate, v));
                Form.AddCheck(CoreTools.Translate("Download full package manifest alongside the installer"),
                    () => Settings.Get(Settings.K.WinGetDownloadFullManifest), v => Settings.Set(Settings.K.WinGetDownloadFullManifest, v));
                Form.AddText(CoreTools.Translate("Stop offering an update that never applies after this many attempts"),
                    () => Settings.GetValue(Settings.K.WinGetStuckUpgradeThreshold),
                    v => Settings.SetValue(Settings.K.WinGetStuckUpgradeThreshold, string.Concat(v.Where(char.IsDigit))), "3");
                break;
            case "Scoop":
                Form.AddHeader(CoreTools.Translate("Advanced options"));
                Form.AddButton(CoreTools.AutoTranslated("Install Scoop"), () => TuiScoopMaintenance.RunAsync(ScoopMaintenanceTask.Install));
                Form.AddButton(CoreTools.AutoTranslated("Uninstall Scoop (and its packages)"), () => TuiScoopMaintenance.RunAsync(ScoopMaintenanceTask.Uninstall));
                Form.AddButton(CoreTools.AutoTranslated("Run cleanup and clear cache"), () => TuiScoopMaintenance.RunAsync(ScoopMaintenanceTask.Cleanup));
                Form.AddCheck(CoreTools.AutoTranslated("Clear Scoop download cache on launch"),
                    () => Settings.Get(Settings.K.EnableScoopCleanupCache), v => Settings.Set(Settings.K.EnableScoopCleanupCache, v));
                Form.AddCheck(CoreTools.AutoTranslated("Clean up older Scoop app versions on launch"),
                    () => Settings.Get(Settings.K.EnableScoopCleanupApps), v => Settings.Set(Settings.K.EnableScoopCleanupApps, v));
                break;
            case "Bun":
                Form.AddHeader(CoreTools.Translate("Advanced options"));
                Form.AddCheck(CoreTools.Translate("Prefer latest versions (may include breaking changes) instead of recommended safe updates"),
                    () => Settings.Get(Settings.K.BunPreferLatestVersions), v => Settings.Set(Settings.K.BunPreferLatestVersions, v));
                break;
            case "vcpkg":
                Form.AddHeader(CoreTools.Translate("Advanced options"));
                Form.AddText(CoreTools.Translate("Default vcpkg triplet"), () => Settings.GetValue(Settings.K.DefaultVcpkgTriplet),
                    v => Settings.SetValue(Settings.K.DefaultVcpkgTriplet, v.Trim()));
                Form.AddText(CoreTools.Translate("vcpkg root"),
                    () => Settings.Get(Settings.K.CustomVcpkgRoot) ? Settings.GetValue(Settings.K.CustomVcpkgRoot) : "",
                    v =>
                    {
                        if (string.IsNullOrWhiteSpace(v)) Settings.Set(Settings.K.CustomVcpkgRoot, false);
                        else Settings.SetValue(Settings.K.CustomVcpkgRoot, v.Trim());
                    }, "%VCPKG_ROOT%");
                break;
        }
    }

    private static string Value(Settings.K key, string fallback)
    {
        string v = Settings.GetValue(key);
        return v.Length == 0 ? fallback : v;
    }

    private async Task ToggleAsync(bool enabled)
    {
        await ManagersPage.SetEnabledAsync(_manager, enabled);
        await LoadSourcesAsync();
        Dispatcher.UIThread.Post(Rebuild);
    }

    private async Task SelectExecutableAsync()
    {
        var candidates = await Task.Run(() => _manager.FindCandidateExecutableFiles());
        var choices = candidates.Select(c => new TuiChoice(c)).ToList();
        choices.Add(new TuiChoice(CoreTools.Translate("Browse...")));
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Select the executable to be used. The following list shows the executables found by UniGetUI"), choices);
        if (picked is not int i) return;
        string? path = i < candidates.Count ? candidates[i]
            : await TuiPrompts.AskTextAsync(CoreTools.Translate("Select executable"), CoreTools.Translate("Path to the executable:"), "",
                p => File.Exists(p.Trim().Trim('"')) ? null : CoreTools.Translate("The file does not exist."));
        if (string.IsNullOrWhiteSpace(path)) return;
        Settings.SetDictionaryItem(Settings.K.ManagerPaths, _manager.Name, path.Trim().Trim('"'));
        await ToggleAsync(_manager.IsEnabled());
    }

    private async Task AddSourceAsync()
    {
        var known = _manager.Properties.KnownSources.Where(k => _sources.All(s => s.Name != k.Name)).ToList();
        var choices = known.Select(k => new TuiChoice($"{k.Name}  —  {k.Url}")).ToList();
        choices.Add(new TuiChoice(CoreTools.Translate("Other") + "…"));
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Add source"), choices);
        if (picked is not int i) return;

        IManagerSource source;
        if (i < known.Count)
        {
            source = known[i];
        }
        else
        {
            string? name = await TuiPrompts.AskTextAsync(CoreTools.Translate("Add source"), CoreTools.Translate("Source name:"));
            if (string.IsNullOrWhiteSpace(name)) return;
            string? url = await TuiPrompts.AskTextAsync(CoreTools.Translate("Add source"), CoreTools.Translate("Source URL:"), "https://",
                u => Uri.TryCreate(u.Trim(), UriKind.Absolute, out _) ? null : CoreTools.Translate("Please enter a valid URL"));
            if (string.IsNullOrWhiteSpace(url)) return;
            source = new ManagerSource(_manager, name.Trim(), new Uri(url.Trim()));
        }

        var op = new AddSourceOperation(source);
        op.OperationFinished += (_, _) => _ = LoadSourcesAsync();
        TuiOperationRegistry.Start(op);
        TuiNotifications.Info(CoreTools.Translate("Add source"), source.Name);
    }

    private async Task RemoveSourceAsync(IManagerSource source)
    {
        if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Remove"),
                CoreTools.Translate("Are you sure you want to delete the source {0}?", source.Name)))
            return;
        var op = new RemoveSourceOperation(source);
        op.OperationFinished += (_, _) => _ = LoadSourcesAsync();
        TuiOperationRegistry.Start(op);
    }

    private static Task ResetWinGetAsync()
    {
        if (TuiEngine.IsFakeData || !OperatingSystem.IsWindows())
        {
            TuiNotifications.Info(CoreTools.Translate("Reset WinGet"), CoreTools.Translate("Not available in fake data mode or on this platform."));
            return Task.CompletedTask;
        }

        return TuiWinGetRepair.RepairAsync();
    }
}
