using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// Every desktop settings page (General, User interface, Notifications, Updates, Operations,
/// Scheduled maintenance, Backup, Administrator, Internet, Experimental), reading and writing the same
/// <see cref="Settings"/> / <see cref="SecureSettings"/> keys, so a change here is a change in the desktop
/// app too. "Disable…" keys are shown with positive labels (inverted), as the desktop cards do.
/// Settings that only affect the desktop window are kept editable and labelled "(desktop app)".
/// </summary>
internal sealed class SettingsPage : UserControl, ITuiPage
{
    internal enum Section
    {
        General,
        Interface,
        Notifications,
        Updates,
        Operations,
        Scheduler,
        Backup,
        Administrator,
        Internet,
        Experimental,
    }

    private static readonly (Section Id, string Label)[] Sections =
    [
        (Section.General, "General preferences"),
        (Section.Interface, "User interface preferences"),
        (Section.Notifications, "Notification preferences"),
        (Section.Updates, "Package update preferences"),
        (Section.Operations, "Package operation preferences"),
        (Section.Scheduler, "Scheduled maintenance"),
        (Section.Backup, "Backup and Restore"),
        (Section.Administrator, "Administrator rights and other dangerous settings"),
        (Section.Internet, "Internet connection settings"),
        (Section.Experimental, "Experimental settings and developer options"),
    ];

    private TuiForm _form = new();
    private Section _section = Section.General;
    private readonly string _desktopOnly = " (" + CoreTools.Translate("desktop app") + ")";

    public SettingsPage()
    {
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 1, 0) };
        var header = new StackPanel();
        header.Children.Add(TuiChrome.PageTitle(CoreTools.Translate("Settings")));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_form);
        Content = root;
        _form.Rebuild(Build);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.PageDown or Key.PageUp)
            {
                int i = Array.FindIndex(Sections, s => s.Id == _section);
                ShowSection(Sections[(i + (e.Key == Key.PageDown ? 1 : Sections.Length - 1)) % Sections.Length].Id);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<TuiKeyHint> KeyHints => TuiChrome.Hints(("↑/↓", "Move"), ("Space", "Toggle"), ("←/→", "Change"), ("Enter", "Edit/open"), ("PgUp/PgDn", "Section"));

    public IReadOnlyList<TuiAction> Actions => Sections.Select(s => new TuiAction(CoreTools.Translate(s.Label), null, () =>
    {
        ShowSection(s.Id);
        return Task.CompletedTask;
    })).ToList();

    public bool FocusPrimary() => _form.FocusPrimary();

    /// <summary>Ctrl+F on the Settings page searches every setting (desktop settings search).</summary>
    public bool FocusSearch()
    {
        _ = SearchSettingsAsync();
        return true;
    }

    private IReadOnlyList<(Section Section, string Label)>? _index;

    /// <summary>Every field label of every section, built by rendering each section into a scratch form.</summary>
    internal IReadOnlyList<(Section Section, string Label)> SettingsIndex()
    {
        if (_index is not null) return _index;
        var index = new List<(Section, string)>();
        TuiForm real = _form;
        Section current = _section;
        try
        {
            foreach (var (id, _) in Sections)
            {
                _form = new TuiForm();
                _section = id;
                Build();
                foreach (var control in _form.Rows.Children)
                {
                    string? label = control switch
                    {
                        TuiFormField field => field.Label.TrimEnd(':'),
                        DockPanel row => row.Children.OfType<TextBlock>().FirstOrDefault()?.Text?.TrimEnd(':', ' '),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(label) && !label.StartsWith(CoreTools.Translate("Section"), StringComparison.Ordinal))
                        index.Add((id, label));
                }
            }
        }
        finally
        {
            _form = real;
            _section = current;
        }

        return _index = index;
    }

    private async Task SearchSettingsAsync()
    {
        string? query = await TuiPrompts.AskTextAsync(CoreTools.Translate("Search settings"), CoreTools.Translate("Search for a setting:"));
        if (string.IsNullOrWhiteSpace(query)) return;
        var hits = SettingsIndex().Where(e => e.Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
        if (hits.Count == 0)
        {
            await TuiPrompts.InfoAsync(CoreTools.Translate("Search settings"), CoreTools.Translate("No settings match {0}", query));
            return;
        }

        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Search settings"),
            hits.Select(h => new TuiChoice(h.Label, Hint: CoreTools.Translate(Sections.First(s => s.Id == h.Section).Label))).ToList());
        if (picked is not int i) return;
        ShowSection(hits[i].Section);
        string label = hits[i].Label;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _form.FocusField(label), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    public void OnShown() => _form.Refresh();

    public void Reload() => _form.Refresh();

    public void ShowSection(Section section)
    {
        _section = section;
        _form.Rebuild(Build);
    }

    public Section CurrentSection => _section;

    private void Build()
    {
        _form.AddChoice(CoreTools.Translate("Section"),
            Sections.Select(s => new TuiOption(CoreTools.Translate(s.Label), s.Id.ToString())).ToList(),
            () => _section.ToString(),
            v =>
            {
                _section = Enum.Parse<Section>(v);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _form.Rebuild(Build));
            });

        switch (_section)
        {
            case Section.General: BuildGeneral(); break;
            case Section.Interface: BuildInterface(); break;
            case Section.Notifications: BuildNotifications(); break;
            case Section.Updates: BuildUpdates(); break;
            case Section.Operations: BuildOperations(); break;
            case Section.Scheduler: BuildScheduler(); break;
            case Section.Backup: BuildBackup(); break;
            case Section.Administrator: BuildAdministrator(); break;
            case Section.Internet: BuildInternet(); break;
            case Section.Experimental: BuildExperimental(); break;
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private void Check(string label, Settings.K key, bool inverted = false)
        => _form.AddCheck(CoreTools.Translate(label), () => Settings.Get(key) ^ inverted, v => Settings.Set(key, v ^ inverted));

    private void Choice(string label, Settings.K key, IReadOnlyList<TuiOption> options, Action<string>? after = null)
        => _form.AddChoice(CoreTools.Translate(label), options, () => Settings.GetValue(key), v =>
        {
            Settings.SetValue(key, v);
            after?.Invoke(v);
        });

    private void SecureCheck(string label, SecureSettings.K key)
    {
        _form.AddCheck(CoreTools.Translate(label), () => SecureSettings.Get(key), v => _ = SetSecureAsync(key, v));
    }

    private async Task SetSecureAsync(SecureSettings.K key, bool value)
    {
        bool ok = await TuiSecureSettings.TrySetAsync(key, value);
        if (!ok) TuiNotifications.Error(CoreTools.Translate("Settings"), CoreTools.Translate("The setting could not be changed"));
        Avalonia.Threading.Dispatcher.UIThread.Post(_form.Refresh);
    }

    // ─── General ─────────────────────────────────────────────────────────

    private void BuildGeneral()
    {
        _form.AddHeader(CoreTools.Translate("General preferences"));
        var languages = new List<TuiOption> { new(CoreTools.Translate("System language"), "default") };
        foreach (var (code, name) in LanguageData.LanguageReference)
        {
            if (code == "default") continue;
            string pct = LanguageData.TranslationPercentages.TryGetValue(code, out var p) ? $" ({p})" : "";
            languages.Add(new TuiOption(name + pct, code));
        }

        _form.AddChoice(CoreTools.Translate("UniGetUI display language:"), languages,
            () => Settings.GetValue(Settings.K.PreferredLanguage) is { Length: > 0 } l ? l : "default",
            v =>
            {
                Settings.SetValue(Settings.K.PreferredLanguage, v);
                TuiNotifications.Info(CoreTools.Translate("Language"), CoreTools.Translate("Restart UniGetUI to fully apply changes"));
            });
        Check("Update UniGetUI automatically" + _desktopOnly, Settings.K.DisableAutoUpdateWingetUI, inverted: true);
        Check("Install prerelease versions of UniGetUI" + _desktopOnly, Settings.K.EnableUniGetUIBeta);
        Check("Show the release notes after UniGetUI is updated" + _desktopOnly, Settings.K.DisableReleaseNotesOnUpdate, inverted: true);
        Check("Share anonymous usage data" + _desktopOnly, Settings.K.DisableTelemetry, inverted: true);
        _form.AddNote(CoreTools.Translate("The terminal UI never sends telemetry."));
        _form.AddCheck(CoreTools.Translate("Hide my username from the logs"), () => Settings.Get(Settings.K.RedactUsernameInLog), v =>
        {
            Settings.Set(Settings.K.RedactUsernameInLog, v);
            Logger.RedactUsername = v;
        });

        _form.AddHeader(CoreTools.Translate("Import settings") + " / " + CoreTools.Translate("Export settings"));
        _form.AddButton(CoreTools.Translate("Import settings from a local file"), ImportSettingsAsync);
        _form.AddButton(CoreTools.Translate("Export settings to a local file"), ExportSettingsAsync);
        _form.AddButton(CoreTools.Translate("Reset UniGetUI"), ResetSettingsAsync);
    }

    private async Task ImportSettingsAsync()
    {
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Import settings"), CoreTools.Translate("Path of the settings file (JSON):"),
            Path.Join(CoreData.UniGetUIDataDirectory, "settings.json"),
            p => File.Exists(p.Trim().Trim('"')) ? null : CoreTools.Translate("The file does not exist."));
        if (string.IsNullOrWhiteSpace(path)) return;
        Settings.ImportFromFile_JSON(path.Trim().Trim('"'));
        TuiNotifications.Success(CoreTools.Translate("Import settings"), CoreTools.Translate("Restart UniGetUI to fully apply changes"));
    }

    private async Task ExportSettingsAsync()
    {
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Export settings"), CoreTools.Translate("Save to file:"),
            Path.Join(TuiPackageActions.DefaultDownloadDirectory(), "UniGetUI settings.json"));
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = path.Trim().Trim('"');
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(full))!);
        Settings.ExportToFile_JSON(full);
        TuiNotifications.Success(CoreTools.Translate("Export settings"), full);
    }

    private async Task ResetSettingsAsync()
    {
        if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Reset UniGetUI"),
                CoreTools.Translate("Are you sure you want to reset all the preferences to their default values?")
                + "\n" + CoreTools.Translate("This action cannot be undone."), CoreTools.Translate("Reset"), CoreTools.Translate("Cancel")))
            return;
        Settings.ResetSettings();
        if (TuiEngine.IsFakeData) Settings.Set(Settings.K.UseAgentBroker, false);
        TuiNotifications.Success(CoreTools.Translate("Reset UniGetUI"), CoreTools.Translate("Restart UniGetUI to fully apply changes"));
    }

    // ─── Interface ───────────────────────────────────────────────────────

    private void BuildInterface()
    {
        _form.AddHeader(CoreTools.Translate("User interface preferences"));
        // Applies immediately, so stepping through the themes with ← → previews each one.
        _form.AddChoice(CoreTools.Translate("Theme"), TuiThemes.All.Select(t => new TuiOption(t.Name, t.Id)).ToList(),
            () => TuiPalette.Current.Id,
            id => TuiPalette.Apply(TuiThemes.Find(id) ?? TuiThemes.Default, save: true));
        Choice("Default page", Settings.K.StartupPage,
            [
                new(CoreTools.Translate("Default"), ""), new(CoreTools.Translate("Discover Packages"), "discover"),
                new(CoreTools.Translate("Software Updates"), "updates"), new(CoreTools.Translate("Installed Packages"), "installed"),
                new(CoreTools.Translate("Package Bundles"), "bundles"), new(CoreTools.Translate("Settings"), "settings"),
            ]);
        Check("Select upgradable packages by default", Settings.K.DisableSelectingUpdatesByDefault, inverted: true);
        _form.AddCheck(CoreTools.Translate("Show the installer host column"), () => Settings.Get(Settings.K.ShowInstallerHostColumn), v =>
        {
            Settings.Set(Settings.K.ShowInstallerHostColumn, v);
            TuiShell.InvalidatePages(TuiPageIds.Discover, TuiPageIds.Updates, TuiPageIds.Bundles);
        });
        _form.AddCheck(CoreTools.Translate("Show the download size column"), () => Settings.Get(Settings.K.ShowDownloadSizeColumn), v =>
        {
            Settings.Set(Settings.K.ShowDownloadSizeColumn, v);
            TuiShell.InvalidatePages(TuiPageIds.Discover, TuiPageIds.Updates, TuiPageIds.Bundles);
        });
        _form.AddHeader(CoreTools.Translate("Desktop app"));
        Choice("Application theme:" + _desktopOnly, Settings.K.PreferredTheme,
            [new(CoreTools.Translate("Light"), "light"), new(CoreTools.Translate("Dark"), "dark"), new(CoreTools.Translate("Follow system color scheme"), "auto")]);
        Check("Use the system UI font" + _desktopOnly, Settings.K.UseSystemUIFont);
        Choice("Navigation menu" + _desktopOnly, Settings.K.NavMenuMode,
            [new(CoreTools.Translate("Automatic"), "auto"), new(CoreTools.Translate("Docked"), "docked"), new(CoreTools.Translate("Overlay"), "overlay")]);
        Check("Close UniGetUI to the system tray" + _desktopOnly, Settings.K.DisableSystemTray, inverted: true);
        Check("Show package icons on package lists" + _desktopOnly, Settings.K.DisableIconsOnPackageLists, inverted: true);
        Check("Show illustrations on empty package lists" + _desktopOnly, Settings.K.DisablePackageIllustrations, inverted: true);
        Check("Use software rendering automatically on hosts without a GPU" + _desktopOnly, Settings.K.DisableAutoSoftwareRenderingOnGpuLessHosts, inverted: true);
        _form.AddButton(CoreTools.Translate("Clear cache"), () =>
        {
            try
            {
                if (Directory.Exists(CoreData.UniGetUICacheDirectory_Icons)) Directory.Delete(CoreData.UniGetUICacheDirectory_Icons, true);
                TuiNotifications.Success(CoreTools.Translate("Clear cache"), CoreTools.Translate("The icon cache was cleared"));
            }
            catch (Exception ex)
            {
                TuiNotifications.Error(CoreTools.Translate("Clear cache"), ex.Message);
            }
        });
    }

    // ─── Notifications ───────────────────────────────────────────────────

    private void BuildNotifications()
    {
        _form.AddHeader(CoreTools.Translate("Notification preferences"));
        Check("Enable UniGetUI notifications", Settings.K.DisableNotifications, inverted: true);
        Check("Show a summary notification when a batch of operations finishes", Settings.K.ShowOperationSummaryNotifications);
        Check("Show a notification when there are available updates", Settings.K.DisableUpdatesNotifications, inverted: true);
        Check("Show a silent notification when an operation is running", Settings.K.DisableProgressNotifications, inverted: true);
        Check("Show a notification when an operation fails", Settings.K.DisableErrorNotifications, inverted: true);
        Check("Show a notification when an operation finishes successfully", Settings.K.DisableSuccessNotifications, inverted: true);
    }

    // ─── Updates ─────────────────────────────────────────────────────────

    private void BuildUpdates()
    {
        _form.AddHeader(CoreTools.Translate("Package update preferences"));
        _form.AddNote(CoreTools.Translate("How often UniGetUI checks for and installs updates is set in Scheduled maintenance."));
        _form.AddButton(CoreTools.Translate("Scheduled maintenance") + "…", () => ShowSection(Section.Scheduler));
        Check("Do not automatically install updates when the network connection is metered", Settings.K.DisableAUPOnMeteredConnections);
        Check("Do not automatically install updates when the device runs on battery", Settings.K.DisableAUPOnBattery);
        Check("Do not automatically install updates when the battery saver is on", Settings.K.DisableAUPOnBatterySaver);
        _form.AddNote(CoreTools.Translate("Power and network conditions are read by the desktop app; the terminal UI does not evaluate them."));
        _form.AddHeader(CoreTools.Translate("Update security"));
        Choice("Minimum age for updates", Settings.K.MinimumUpdateAge,
            [
                new(CoreTools.Translate("No minimum age"), ""), new(CoreTools.Translate("1 day"), "1"),
                new(CoreTools.Translate("{0} days", 3), "3"), new(CoreTools.Translate("{0} days", 7), "7"),
                new(CoreTools.Translate("{0} days", 14), "14"), new(CoreTools.Translate("{0} days", 30), "30"),
                new(CoreTools.Translate("Custom..."), "custom"),
            ]);
        _form.AddText(CoreTools.Translate("Custom minimum age (days)"), () => Settings.GetValue(Settings.K.MinimumUpdateAgeCustom),
            v => Settings.SetValue(Settings.K.MinimumUpdateAgeCustom, string.Concat(v.Where(char.IsDigit))), CoreTools.Translate("e.g. 10"),
            () => Settings.GetValue(Settings.K.MinimumUpdateAge) == "custom");
        Check("Warn me when the host of an installer changes", Settings.K.DisableInstallerHostChangeWarning, inverted: true);
        _form.AddHeader(CoreTools.Translate("Package managers that provide release dates"));
        foreach (var m in TuiEngine.Managers)
            _form.AddNote($"{m.DisplayName}: {m.Capabilities.KnowsPackageReleaseDate}");
    }

    // ─── Operations ──────────────────────────────────────────────────────

    private void BuildOperations()
    {
        _form.AddHeader(CoreTools.Translate("Package operation preferences"));
        Choice("Choose how many operations should be performed in parallel", Settings.K.ParallelOperationCount,
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 15, 20, 30, 50, 75, 100 }.Select(n => new TuiOption(n.ToString(), n.ToString())).ToList(),
            v =>
            {
                if (int.TryParse(v, out int n)) AbstractOperation.MAX_OPERATIONS = n;
            });
        _form.AddCheck(CoreTools.Translate("Clear successful operations from the operation list after a 5 second delay"),
            () => !Settings.Get(Settings.K.MaintainSuccessfulInstalls), v => Settings.Set(Settings.K.MaintainSuccessfulInstalls, !v));
        Check("Try to kill the processes that refuse to close when requested to", Settings.K.KillProcessesThatRefuseToDie);
        _form.AddText(CoreTools.Translate("Default installer download location"), () => InstallerDownloadLocation.GetCustomDirectory() ?? "",
            v => InstallerDownloadLocation.SetCustomDirectory(string.IsNullOrWhiteSpace(v) ? null : v.Trim()),
            InstallerDownloadLocation.DefaultDirectory);
        Choice("Installer file name", Settings.K.InstallerFileNameScheme,
            [
                new(CoreTools.Translate("Default"), ""),
                new(CoreTools.Translate("Publisher name"), InstallerFileNaming.PublisherNameValue),
                new(CoreTools.Translate("Package name and version"), InstallerFileNaming.NameAndVersionValue),
                new(CoreTools.Translate("Package ID and version"), InstallerFileNaming.IdAndVersionValue),
                new(CoreTools.Translate("Publisher name and version"), InstallerFileNaming.PublisherNameAndVersionValue),
            ]);
        Check("Use %VARIABLE% syntax for environment variables in custom arguments", Settings.K.ExpandEnvVarsWithPercentSyntax);
        Check("Ask to delete desktop shortcuts created during an install or upgrade", Settings.K.AskToDeleteNewDesktopShortcuts);
        Check("Ask about new Start Menu shortcuts created during an install or upgrade", Settings.K.AskAboutNewStartMenuShortcuts);
        _form.AddButton(CoreTools.Translate("Manage shortcuts") + "…", ShortcutsDialog.ShowAsync);
    }

    // ─── Scheduler ───────────────────────────────────────────────────────

    private void BuildScheduler()
    {
        _form.AddHeader(CoreTools.Translate("Scheduled maintenance"));
        foreach (MaintenanceTaskKind kind in MaintenanceTasks.All)
        {
            MaintenanceTaskKind k = kind;
            _form.AddHeader(TaskLabel(k));
            if (!TuiMaintenanceScheduler.IsSupported(k))
                _form.AddNote(CoreTools.Translate("GitHub sign-in is not available in this build."));
            _form.AddCheck(CoreTools.Translate("Enabled"), () => MaintenanceScheduleStore.Get(k).Enabled, v => Update(k, s => s.Enabled = v));
            _form.AddChoice(CoreTools.Translate("Frequency"),
                MaintenanceTasks.GetSupportedFrequencies(k).Select(f => new TuiOption(FrequencyLabel(f), f.ToString())).ToList(),
                () => MaintenanceScheduleStore.Get(k).Frequency.ToString(), v => Update(k, s => s.Frequency = Enum.Parse<ScheduleFrequency>(v)),
                () => MaintenanceScheduleStore.Get(k).Enabled);
            _form.AddText(CoreTools.Translate("Interval (minutes)"), () => (MaintenanceScheduleStore.Get(k).IntervalSeconds / 60).ToString(),
                v => Update(k, s => s.IntervalSeconds = Math.Max(1, int.TryParse(v, out int m) ? m : 60) * 60), null,
                () => MaintenanceScheduleStore.Get(k).Enabled && MaintenanceScheduleStore.Get(k).Frequency is ScheduleFrequency.Interval);
            _form.AddText(CoreTools.Translate("Start time (HH:mm)"),
                () => TimeSpan.FromMinutes(MaintenanceScheduleStore.Get(k).StartMinutes).ToString(@"hh\:mm"),
                v => Update(k, s => s.StartMinutes = TimeSpan.TryParse(v, out var t) ? (int)t.TotalMinutes : s.StartMinutes), null,
                () => MaintenanceScheduleStore.Get(k).Enabled && ScheduleEvaluator.IsTimeBased(MaintenanceScheduleStore.Get(k).Frequency));
            _form.AddText(CoreTools.Translate("Days (e.g. Mon,Wed,Fri)"),
                () => string.Join(',', Enum.GetValues<DayOfWeek>().Where(d => MaintenanceScheduleStore.Get(k).HasDay(d)).Select(d => d.ToString()[..3])),
                v => Update(k, s =>
                {
                    foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
                        s.SetDay(d, v.Contains(d.ToString()[..3], StringComparison.OrdinalIgnoreCase));
                }), null,
                () => MaintenanceScheduleStore.Get(k).Enabled && MaintenanceScheduleStore.Get(k).Frequency is ScheduleFrequency.Weekly);
            _form.AddText(CoreTools.Translate("Grace period (minutes, empty = unlimited, 0 = skip missed runs)"),
                () => MaintenanceScheduleStore.Get(k).GraceMinutes == MaintenanceTaskSchedule.UnlimitedGrace ? "" : MaintenanceScheduleStore.Get(k).GraceMinutes.ToString(),
                v => Update(k, s => s.GraceMinutes = int.TryParse(v, out int g) ? g : MaintenanceTaskSchedule.UnlimitedGrace), null,
                () => MaintenanceScheduleStore.Get(k).Enabled && ScheduleEvaluator.IsTimeBased(MaintenanceScheduleStore.Get(k).Frequency));
            if (k is MaintenanceTaskKind.InstallUpdates)
            {
                _form.AddChoice(CoreTools.Translate("Packages to update"),
                    [new(CoreTools.Translate("All packages"), nameof(ScheduleInstallTargets.AllPackages)),
                     new(CoreTools.Translate("Only packages marked for automatic updates"), nameof(ScheduleInstallTargets.MarkedPackagesOnly))],
                    () => MaintenanceScheduleStore.Get(k).InstallTargets.ToString(),
                    v => Update(k, s => s.InstallTargets = Enum.Parse<ScheduleInstallTargets>(v)),
                    () => MaintenanceScheduleStore.Get(k).Enabled);
                _form.AddButton(CoreTools.Translate("Manage automatic updates") + "…", AutoUpdatesDialog.ShowAsync);
            }

            _form.AddLiveNote(() => ScheduleSummary(k));
            _form.AddButton(CoreTools.Translate("Run now"), () => TuiMaintenanceScheduler.RunAsync(k), () => TuiMaintenanceScheduler.IsSupported(k));
        }
    }

    private static void Update(MaintenanceTaskKind kind, Action<MaintenanceTaskSchedule> change)
    {
        var schedule = MaintenanceScheduleStore.Get(kind).Clone();
        change(schedule);
        schedule.Normalize();
        MaintenanceScheduleStore.Set(kind, schedule);
    }

    private static string ScheduleSummary(MaintenanceTaskKind kind)
    {
        var schedule = MaintenanceScheduleStore.Get(kind);
        DateTime? last = MaintenanceScheduleStore.GetLastRun(kind);
        DateTime? failed = MaintenanceScheduleStore.GetLastFailure(kind);
        DateTime? next = schedule.Enabled ? ScheduleEvaluator.GetNextOccurrence(schedule, last, DateTime.Now) : null;
        return CoreTools.Translate("Last run") + ": " + (last?.ToLocalTime().ToString("g") ?? CoreTools.Translate("Never"))
               + "  ·  " + CoreTools.Translate("Next run") + ": " + (next?.ToString("g") ?? "—")
               + (failed is { } f ? "  ·  " + CoreTools.Translate("Last failure") + ": " + f.ToLocalTime().ToString("g") : "");
    }

    internal static string TaskLabel(MaintenanceTaskKind kind) => kind switch
    {
        MaintenanceTaskKind.CheckForUpdates => CoreTools.Translate("Check for package updates"),
        MaintenanceTaskKind.InstallUpdates => CoreTools.Translate("Install available updates"),
        MaintenanceTaskKind.LocalBackup => CoreTools.Translate("Back up installed packages locally"),
        _ => CoreTools.Translate("Back up installed packages to the cloud"),
    };

    private static string FrequencyLabel(ScheduleFrequency f) => f switch
    {
        ScheduleFrequency.AtAppStart => CoreTools.Translate("When UniGetUI starts"),
        ScheduleFrequency.AfterEveryUpdateCheck => CoreTools.Translate("After every update check"),
        ScheduleFrequency.Interval => CoreTools.Translate("At a fixed interval"),
        ScheduleFrequency.Daily => CoreTools.Translate("Daily"),
        _ => CoreTools.Translate("Weekly"),
    };

    // ─── Backup ──────────────────────────────────────────────────────────

    private void BuildBackup()
    {
        _form.AddHeader(CoreTools.Translate("Backup and Restore"));
        _form.AddNote(CoreTools.Translate("The backup will include the complete list of the installed packages and their installation options. Ignored updates and skipped versions will also be saved."));
        _form.AddHeader(CoreTools.Translate("Local backup"));
        Check("Periodically perform a local backup of the installed packages", Settings.K.EnablePackageBackup_LOCAL);
        _form.AddButton(CoreTools.Translate("Perform a local backup now"), async () =>
        {
            string? path = await TuiBackup.DoLocalBackupAsync();
            if (path is null) TuiNotifications.Error(CoreTools.Translate("Backup"), CoreTools.Translate("The backup could not be created"));
            else TuiNotifications.Success(CoreTools.Translate("Backup"), path);
        });
        _form.AddText(CoreTools.Translate("Backup output directory"), () => Settings.GetValue(Settings.K.ChangeBackupOutputDirectory),
            v => Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, v.Trim()), CoreData.UniGetUI_DefaultBackupDirectory);
        _form.AddText(CoreTools.Translate("Backup file name"), () => Settings.GetValue(Settings.K.ChangeBackupFileName),
            v => Settings.SetValue(Settings.K.ChangeBackupFileName, v.Trim()), LocalBackupManager.ResolveFileNameBase());
        Check("Add a timestamp to the backup file names", Settings.K.EnableBackupTimestamping);
        Choice("Maximum number of local backups to keep", Settings.K.MaxLocalBackupCount,
            [
                new(CoreTools.Translate("Unlimited"), ""), new("5", "5"), new("10", "10"), new("25", "25"), new("50", "50"),
                new(CoreTools.Translate("Custom..."), "custom"),
            ]);
        _form.AddText(CoreTools.Translate("Custom number of backups"), () => Settings.GetValue(Settings.K.MaxLocalBackupCountCustom),
            v => Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, string.Concat(v.Where(char.IsDigit))), null,
            () => Settings.GetValue(Settings.K.MaxLocalBackupCount) == "custom");
        _form.AddLiveNote(() => CoreTools.Translate("Backups are saved to {0}", LocalBackupManager.ResolveOutputDirectory()));
        _form.AddButton(CoreTools.Translate("Restore a backup") + "…", async () =>
        {
            TuiShell.Navigate(TuiPageIds.Bundles);
            await Task.Yield();
            TuiShell.RequestBundleOpen();
        });
        _form.AddHeader(CoreTools.Translate("Cloud backup"));
        _form.AddNote(CoreTools.Translate("Back up the list of installed packages to a private GitHub Gist."));
        _form.AddLiveNote(() => !TuiCloudBackup.IsConfigured
            ? CoreTools.Translate("GitHub sign-in is not available in this build.")
            : TuiCloudBackup.IsSignedIn
                ? CoreTools.Translate("You are logged in as {0} (@{1})", TuiCloudBackup.UserLogin, TuiCloudBackup.UserLogin)
                : CoreTools.Translate("Log in to enable cloud backup"), TuiPalette.Brand);
        _form.AddButton(CoreTools.Translate("Log in with GitHub"), SignInAsync, () => TuiCloudBackup.IsConfigured && !TuiCloudBackup.IsSignedIn);
        _form.AddButton(CoreTools.Translate("Log out from GitHub"), TuiCloudBackup.SignOut, () => TuiCloudBackup.IsSignedIn);
        Check("Periodically perform a cloud backup of the installed packages", Settings.K.EnablePackageBackup_CLOUD);
        _form.AddButton(CoreTools.Translate("Perform a cloud backup now"), async () =>
        {
            bool ok = await TuiCloudBackup.BackupNowAsync();
            if (ok) TuiNotifications.Success(CoreTools.Translate("Cloud backup"), CoreTools.Translate("Your packages have been backed up to the cloud"));
            else TuiNotifications.Error(CoreTools.Translate("Cloud backup"), CoreTools.Translate("Backup Failed"));
        }, () => TuiCloudBackup.IsSignedIn);
        _form.AddButton(CoreTools.Translate("Restore a backup from the cloud") + "…", RestoreCloudBackupAsync, () => TuiCloudBackup.IsSignedIn);
    }

    private async Task SignInAsync()
    {
        using var cancellation = new CancellationTokenSource();
        MessageDialog? codeDialog = null;
        Task<bool> signIn = TuiCloudBackup.SignInAsync(async (url, code) =>
        {
            TuiPackageActions.OpenExternally(url);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                codeDialog = new MessageDialog(CoreTools.Translate("Log in with GitHub"),
                    CoreTools.Translate("Open {0} in a browser and enter this code:", url) + "\n\n    " + code + "\n\n"
                    + CoreTools.Translate("Waiting for GitHub…"), [CoreTools.Translate("Cancel")]);
                _ = TuiModal.ShowAsync(codeDialog).ContinueWith(t => cancellation.Cancel(), TaskScheduler.Default);
            });
        }, cancellation.Token);
        bool ok;
        try
        {
            ok = await signIn;
        }
        catch (OperationCanceledException)
        {
            ok = false;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            ok = false;
        }

        codeDialog?.Close(null);
        if (ok) TuiNotifications.Success(CoreTools.Translate("Log in with GitHub"), CoreTools.Translate("You are logged in as {0} (@{1})", TuiCloudBackup.UserLogin, TuiCloudBackup.UserLogin));
        else TuiNotifications.Warning(CoreTools.Translate("Log in with GitHub"), CoreTools.Translate("The login was not completed"));
        _form.Refresh();
    }

    private async Task RestoreCloudBackupAsync()
    {
        IReadOnlyList<CloudBackup> backups;
        try
        {
            backups = await TuiCloudBackup.ListAsync();
        }
        catch (Exception ex)
        {
            TuiNotifications.Error(CoreTools.Translate("Cloud backup"), ex.Message);
            return;
        }

        if (backups.Count == 0)
        {
            await TuiPrompts.InfoAsync(CoreTools.Translate("Cloud backup"), CoreTools.Translate("There are no backups in the cloud yet."));
            return;
        }

        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Select backup"), backups.Select(b => new TuiChoice(b.Display)).ToList());
        if (picked is not int i) return;
        string contents = await TuiCloudBackup.DownloadAsync(backups[i].Key);
        TuiShell.Navigate(TuiPageIds.Bundles);
        TuiBundleService.Clear();
        var (count, _) = await TuiBundleService.AddFromStringAsync(contents, UniGetUI.PackageEngine.Enums.BundleFormatType.UBUNDLE);
        TuiNotifications.Success(CoreTools.Translate("Cloud backup"), CoreTools.Translate("{0} package(s) loaded from {1}", count, backups[i].Key));
    }

    // ─── Administrator ───────────────────────────────────────────────────

    private void BuildAdministrator()
    {
        _form.AddHeader(CoreTools.Translate("Administrator rights and other dangerous settings"));
        _form.AddNote(CoreTools.Translate("Warning") + ": " + CoreTools.Translate("The following settings may pose a security risk, hence they are disabled by default."));
        Check("Ask for administrator privileges once for each batch of operations", Settings.K.DoCacheAdminRightsForBatches);
        Check("Ask only once for administrator privileges", Settings.K.DoCacheAdminRights);
        Check("Prohibit any kind of elevation via UniGetUI Elevator or GSudo", Settings.K.ProhibitElevation);
        _form.AddCheck(CoreTools.Translate("Delegate package operations to the Devolutions Agent"),
            () => Settings.Get(Settings.K.UseAgentBroker), v => Settings.Set(Settings.K.UseAgentBroker, v),
            () => !TuiEngine.IsFakeData);
        if (TuiEngine.IsFakeData) _form.AddNote(CoreTools.Translate("Not available in fake data mode."));
        _form.AddHeader(CoreTools.Translate("Security"));
        SecureCheck("Allow custom command-line arguments", SecureSettings.K.AllowCLIArguments);
        SecureCheck("Allow pre-install and post-install commands", SecureSettings.K.AllowPrePostOpCommand);
        SecureCheck("Allow changing the paths for package manager executables", SecureSettings.K.AllowCustomManagerPaths);
        SecureCheck("Allow importing custom command-line arguments when importing packages from a bundle", SecureSettings.K.AllowImportingCLIArguments);
        SecureCheck("Allow importing pre-install and post-install commands when importing packages from a bundle", SecureSettings.K.AllowImportPrePostOpCommands);
        _form.AddNote(OperatingSystem.IsWindows() && !TuiEngine.IsFakeData
            ? CoreTools.Translate("Changing these settings requires administrator rights.")
            : CoreTools.Translate("These settings are stored per user."));
        _form.AddNote(CoreTools.Translate("The Devolutions Agent policy inspector is available in the desktop app."));
    }

    // ─── Internet ────────────────────────────────────────────────────────

    private void BuildInternet()
    {
        _form.AddHeader(CoreTools.Translate("Internet connection settings"));
        Check("Connect the internet using a custom proxy", Settings.K.EnableProxy);
        _form.AddText(CoreTools.Translate("Proxy URL"), () => Settings.GetValue(Settings.K.ProxyURL),
            v => Settings.SetValue(Settings.K.ProxyURL, v.Trim()), "http://proxy.example:8080", () => Settings.Get(Settings.K.EnableProxy));
        Check("Authenticate to the proxy with a user and a password", Settings.K.EnableProxyAuth);
        _form.AddButton(CoreTools.Translate("Set proxy credentials") + "…", async () =>
        {
            string? user = await TuiPrompts.AskTextAsync(CoreTools.Translate("Proxy authentication"), CoreTools.Translate("Username:"),
                Settings.GetProxyCredentials()?.UserName ?? "");
            if (user is null) return;
            string? password = await TuiModal.ShowAsync(new PasswordDialog(CoreTools.Translate("Proxy authentication"), CoreTools.Translate("Password:"))) as string;
            if (password is null) return;
            Settings.SetProxyCredentials(user, password);
            TuiNotifications.Success(CoreTools.Translate("Proxy authentication"), CoreTools.Translate("Credentials saved"));
        }, () => Settings.Get(Settings.K.EnableProxy) && Settings.Get(Settings.K.EnableProxyAuth));
        Check("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.", Settings.K.DisableWaitForInternetConnection, inverted: true);
        _form.AddNote(CoreTools.Translate("Proxy changes are applied the next time UniGetUI starts."));
        _form.AddHeader(CoreTools.Translate("Proxy compatibility table"));
        foreach (var m in TuiEngine.Managers)
            _form.AddNote($"{m.DisplayName}: {m.Capabilities.SupportsProxy}{(m.Capabilities.SupportsProxyAuth ? " (+auth)" : "")}");
    }

    // ─── Experimental ────────────────────────────────────────────────────

    private void BuildExperimental()
    {
        _form.AddHeader(CoreTools.Translate("Experimental settings and developer options"));
        Check("Show UniGetUI's version and build number on the titlebar.", Settings.K.ShowVersionNumberOnTitlebar);
        Check("Enable background API (Widgets for UniGetUI and Sharing, port 7058)" + _desktopOnly, Settings.K.DisableApi, inverted: true);
        _form.AddCheck(CoreTools.Translate("Disable the 1-minute timeout for package-related operations"),
            () => Settings.Get(Settings.K.DisableTimeoutOnPackageListingTasks), v => Settings.Set(Settings.K.DisableTimeoutOnPackageListingTasks, v));
        SecureCheck("Use installed GSudo instead of UniGetUI Elevator", SecureSettings.K.ForceUserGSudo);
        _form.AddText(CoreTools.Translate("Use a custom icon and screenshot database URL"), () => Settings.GetValue(Settings.K.IconDataBaseURL),
            v => Settings.SetValue(Settings.K.IconDataBaseURL, v.Trim()));
        Check("Enable background CPU usage optimizations (see Pull Request #3278)" + _desktopOnly, Settings.K.DisableDMWThreadOptimizations, inverted: true);
        Check("Perform integrity checks at startup" + _desktopOnly, Settings.K.DisableIntegrityChecks, inverted: true);
        Check("When batch installing packages from a bundle, install also packages that are already installed", Settings.K.InstallInstalledPackagesBundlesPage);
    }
}

/// <summary>A masked single-line prompt used for passwords.</summary>
internal sealed class PasswordDialog : TuiDialog
{
    private readonly TextBox _box = new() { PasswordChar = '*' };

    public PasswordDialog(string title, string prompt) : base(title, 60)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(_box);
        Body.Content = panel;
        AddButton(CoreTools.Translate("OK"), () => Close(_box.Text ?? ""));
        AddButton(CoreTools.Translate("Cancel"), () => Close(null));
    }

    public override void FocusInitial() => _box.Focus();

    public override void OnPreviewKey(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && _box.IsFocused)
        {
            Close(_box.Text ?? "");
            e.Handled = true;
        }
    }
}
