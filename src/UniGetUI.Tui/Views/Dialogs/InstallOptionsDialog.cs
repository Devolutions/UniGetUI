using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Controls;

namespace UniGetUI.Tui.Views.Dialogs;

/// <summary>
/// Installation options editor, ported from the desktop <c>InstallOptionsWindow</c> /
/// <c>InstallOptionsViewModel</c> (per package) and <c>InstallOptionsPanel</c> (manager defaults).
/// The desktop tabs become sections of one scrolling form; the live command preview is kept.
/// Closes with true when the user chose to proceed with the operation.
/// </summary>
internal sealed class InstallOptionsDialog : FormDialog
{
    private readonly IPackage? _package;
    private readonly IPackageManager _manager;
    private readonly InstallOptions _target;
    private readonly InstallOptions _o;
    private readonly bool _managerMode;
    private readonly bool _bundleMode;
    private OperationType _profile;
    private bool _followGlobal;
    private bool _ignoreUpdates;
    private bool _autoUpdate;
    private bool _forceKill = Settings.Get(Settings.K.KillProcessesThatRefuseToDie);
    private string _killList;
    private IReadOnlyList<string> _versions = [];
    private string _preview = "";

    /// <summary>True once the asynchronously loaded state (versions, ignored updates) is shown.</summary>
    internal bool StateLoaded { get; private set; }

    private InstallOptionsDialog(IPackage? package, IPackageManager manager, InstallOptions options, OperationType profile,
        bool managerMode, bool bundleMode)
        : base(managerMode
            ? CoreTools.Translate("Default installation options for {0} packages", manager.DisplayName)
            : CoreTools.Translate("{0} installation options", package!.Name), 100, 34)
    {
        _package = package;
        _manager = manager;
        _target = options;
        _o = options.Copy();
        _managerMode = managerMode;
        _bundleMode = bundleMode;
        _profile = profile is OperationType.None ? OperationType.Install : profile;
        _followGlobal = !managerMode && !bundleMode && !options.OverridesNextLevelOpts;
        _killList = string.Join(", ", options.KillBeforeOperation);
        if (package is not null && !managerMode)
        {
            _autoUpdate = AutoUpdatesDatabase.IsAutoUpdated(package);
            _ = LoadAsyncState();
        }

        Build();
        if (!managerMode)
        {
            AddButton(CoreTools.Translate("Save and close"), () => _ = SaveAsync(proceed: false));
            AddButton(ProfileLabel(), () => _ = SaveAsync(proceed: true));
        }
        else
        {
            AddButton(CoreTools.Translate("Save"), () => _ = SaveAsync(proceed: false));
            AddButton(CoreTools.Translate("Reset"), () =>
            {
                CopyInto(new InstallOptions(), _o);
                Form.Refresh();
            });
        }

        AddButton(CoreTools.Translate("Cancel"), () => Close(false));
        _ = RefreshPreviewAsync();
    }

    public static async Task<bool> ShowForPackageAsync(IPackage package, OperationType role)
    {
        bool bundle = package is ImportedPackage;
        InstallOptions options = package is ImportedPackage imported
            ? imported.installation_options
            : await InstallOptionsFactory.LoadForPackageAsync(package);
        return await TuiModal.ShowAsync(new InstallOptionsDialog(package, package.Manager, options, role, false, bundle)) is true;
    }

    public static async Task ShowForManagerAsync(IPackageManager manager)
    {
        InstallOptions options = await InstallOptionsFactory.LoadForManagerAsync(manager);
        await TuiModal.ShowAsync(new InstallOptionsDialog(null, manager, options, OperationType.Install, true, false));
    }

    private string ProfileLabel() => _profile switch
    {
        OperationType.Update => CoreTools.Translate("Update"),
        OperationType.Uninstall => CoreTools.Translate("Uninstall"),
        _ => CoreTools.Translate("Install"),
    };

    private async Task LoadAsyncState()
    {
        try
        {
            _ignoreUpdates = await _package!.HasUpdatesIgnoredAsync();
            if (_manager.Capabilities.SupportsCustomVersions)
                _versions = await Task.Run(() => _manager.DetailsHelper.GetVersions(_package));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Form.Rebuild(Build);
            StateLoaded = true;
        });
    }

    private bool Unlocked() => !_followGlobal;

    private void Build()
    {
        var caps = _manager.Capabilities;
        bool cliAllowed = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
        bool prePostAllowed = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);

        if (!_managerMode)
        {
            Form.AddChoice(CoreTools.Translate("Operation profile:"),
                [
                    new(CoreTools.Translate("Install"), nameof(OperationType.Install)),
                    new(CoreTools.Translate("Update"), nameof(OperationType.Update)),
                    new(CoreTools.Translate("Uninstall"), nameof(OperationType.Uninstall)),
                ],
                () => _profile.ToString(), v =>
                {
                    _profile = Enum.Parse<OperationType>(v);
                    _ = RefreshPreviewAsync();
                });
            if (!_bundleMode)
            {
                Form.AddCheck(CoreTools.Translate("Follow the default options when installing, upgrading or uninstalling this package"),
                    () => _followGlobal, v =>
                    {
                        _followGlobal = v;
                        _ = RefreshPreviewAsync();
                    });
                Form.AddLiveNote(() => _followGlobal
                    ? CoreTools.Translate("{0} Install options are currently locked because {0} follows the default install options.", _package!.Name)
                    : CoreTools.Translate("The following settings will be applied each time this package is installed, updated or removed."));
                Form.AddButton(CoreTools.Translate("Change default options"), () => ShowForManagerAsync(_manager));
            }
        }

        Form.AddHeader(CoreTools.Translate("General"));
        Form.AddCheck(CoreTools.Translate("Run as admin"), () => _o.RunAsAdministrator, v => Set(() => _o.RunAsAdministrator = v),
            () => Unlocked() && caps.CanRunAsAdmin);
        Form.AddCheck(CoreTools.Translate("Interactive installation"), () => _o.InteractiveInstallation,
            v => Set(() => _o.InteractiveInstallation = v), () => Unlocked() && caps.CanRunInteractively);
        Form.AddCheck(CoreTools.Translate("Skip hash check"), () => _o.SkipHashCheck, v => Set(() => _o.SkipHashCheck = v),
            () => Unlocked() && caps.CanSkipIntegrityChecks && _profile != OperationType.Uninstall);
        Form.AddCheck(CoreTools.Translate("Uninstall previous versions when updated"), () => _o.UninstallPreviousVersionsOnUpdate,
            v => Set(() => _o.UninstallPreviousVersionsOnUpdate = v), () => Unlocked() && caps.CanUninstallPreviousVersionsAfterUpdate);
        Form.AddCheck(CoreTools.Translate("Remove data on uninstall"), () => _o.RemoveDataOnUninstall,
            v => Set(() => _o.RemoveDataOnUninstall = v), () => Unlocked() && caps.CanRemoveDataOnUninstall);

        if (_managerMode)
        {
            Form.AddCheck(CoreTools.Translate("Allow pre-release versions"), () => _o.PreRelease, v => Set(() => _o.PreRelease = v),
                () => caps.SupportsPreRelease);
        }
        else
        {
            Form.AddCheck(CoreTools.Translate("Skip minor updates for this package"), () => _o.SkipMinorUpdates,
                v => Set(() => _o.SkipMinorUpdates = v), Unlocked);
            Form.AddChoice(CoreTools.Translate("Ignore changes after the first") + " (" + CoreTools.Translate("version numbers") + ")",
                [new("1", "2"), new("2", "3"), new("3", "4")],
                () => _o.SkipMinorUpdatesLevel.ToString(), v => Set(() => _o.SkipMinorUpdatesLevel = int.Parse(v)),
                () => Unlocked() && _o.SkipMinorUpdates);
            if (!_bundleMode)
            {
                Form.AddCheck(CoreTools.Translate("Automatically update this package"), () => _autoUpdate, v => _autoUpdate = v);
                Form.AddLiveNote(AutoUpdateHint);
                Form.AddCheck(CoreTools.Translate("Ignore future updates for this package"), () => _ignoreUpdates, v => _ignoreUpdates = v);
            }

            var versionOptions = new List<TuiOption> { new(CoreTools.Translate("Latest"), "") };
            if (caps.SupportsPreRelease) versionOptions.Add(new(CoreTools.Translate("PreRelease"), "\u0001pre"));
            versionOptions.AddRange(_versions.Select(v => new TuiOption(v, v)));
            Form.AddChoice(CoreTools.Translate("Version to install:"), versionOptions,
                () => _o.PreRelease ? "\u0001pre" : _o.Version,
                v => Set(() =>
                {
                    _o.PreRelease = v == "\u0001pre";
                    _o.Version = v == "\u0001pre" ? "" : v;
                }),
                () => Unlocked() && _profile == OperationType.Install && (caps.SupportsCustomVersions || caps.SupportsPreRelease));
        }

        Form.AddHeader(CoreTools.Translate("Architecture") + " / " + CoreTools.Translate("Location and Scope"));
        var arch = new List<TuiOption> { new(CoreTools.Translate("Default"), "") };
        arch.AddRange(caps.SupportedCustomArchitectures.Select(a => new TuiOption(a, a)));
        Form.AddChoice(CoreTools.Translate("Architecture to install:"), arch, () => _o.Architecture,
            v => Set(() => _o.Architecture = v), () => Unlocked() && caps.SupportsCustomArchitectures && _profile != OperationType.Uninstall);
        Form.AddChoice(CoreTools.Translate("Installation scope:"),
            [
                new(CoreTools.Translate("Default"), ""),
                new(CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Local]), PackageScope.Local),
                new(CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Global]), PackageScope.Global),
            ],
            () => _o.InstallationScope, v => Set(() => _o.InstallationScope = v),
            () => Unlocked() && caps.SupportsCustomScopes && _profile switch
            {
                OperationType.Update => caps.SupportsCustomScopesOnUpdate,
                OperationType.Uninstall => caps.SupportsCustomScopesOnUninstall,
                _ => true,
            });
        Form.AddText(CoreTools.Translate("Install location:"), () => _o.CustomInstallLocation,
            v => Set(() => _o.CustomInstallLocation = v.Trim()), CoreTools.Translate("Default"),
            () => Unlocked() && caps.SupportsCustomLocations);
        Form.AddNote(CoreTools.Translate("%PACKAGE% is replaced with the package ID, and %NAME% with the package name."));

        Form.AddHeader(CoreTools.Translate("Command-line") + " " + CoreTools.Translate("Arguments"));
        if (!cliAllowed)
            Form.AddNote(CoreTools.Translate("For security reasons, custom command-line arguments are disabled by default. Go to UniGetUI security settings to change this."));
        Func<bool> cliEnabled = () => Unlocked() && cliAllowed;
        Form.AddText(CoreTools.Translate("Custom install arguments:"), () => string.Join(' ', _o.CustomParameters_Install),
            v => Set(() => _o.CustomParameters_Install = Split(v)), null, cliEnabled);
        Form.AddText(CoreTools.Translate("Custom update arguments:"), () => string.Join(' ', _o.CustomParameters_Update),
            v => Set(() => _o.CustomParameters_Update = Split(v)), null, cliEnabled);
        Form.AddText(CoreTools.Translate("Custom uninstall arguments:"), () => string.Join(' ', _o.CustomParameters_Uninstall),
            v => Set(() => _o.CustomParameters_Uninstall = Split(v)), null, cliEnabled);
        Form.AddButton(CoreTools.Translate("Copy install arguments to update and uninstall"), () =>
        {
            _o.CustomParameters_Update = _o.CustomParameters_Install.ToList();
            _o.CustomParameters_Uninstall = _o.CustomParameters_Install.ToList();
            _ = RefreshPreviewAsync();
        }, cliEnabled);
        Form.AddNote(Settings.Get(Settings.K.ExpandEnvVarsWithPercentSyntax)
            ? CoreTools.Translate("Environment variables use %VARIABLE% syntax.")
            : CoreTools.Translate("Environment variables use <VARIABLE> syntax."));

        if (!_managerMode)
        {
            Form.AddHeader(CoreTools.Translate("Close apps") + " " + CoreTools.Translate("before installing"));
            Form.AddNote(CoreTools.Translate("Select the processes that should be closed before this package is installed, updated or uninstalled."));
            Form.AddText(CoreTools.Translate("Processes"), () => _killList, v => _killList = v,
                CoreTools.Translate("Write here the process names here, separated by commas (,)"), Unlocked);
            Form.AddCheck(CoreTools.Translate("Try to kill the processes that refuse to close when requested to"),
                () => _forceKill, v => _forceKill = v);

            Form.AddHeader(CoreTools.Translate("Pre-install") + " / " + CoreTools.Translate("Post-install"));
            Form.AddNote(prePostAllowed
                ? CoreTools.Translate("You can define the commands that will be run before or after this package is installed, updated or uninstalled. They will be run on a command prompt, so CMD scripts will work here.")
                : CoreTools.Translate("For security reasons, pre-operation and post-operation scripts are disabled by default. Go to UniGetUI security settings to change this."));
            Func<bool> prePost = () => Unlocked() && prePostAllowed;
            Form.AddText(CoreTools.Translate("Pre-install command:"), () => _o.PreInstallCommand, v => _o.PreInstallCommand = v, null, prePost);
            Form.AddText(CoreTools.Translate("Post-install command:"), () => _o.PostInstallCommand, v => _o.PostInstallCommand = v, null, prePost);
            Form.AddCheck(CoreTools.Translate("Abort install if pre-install command fails"), () => _o.AbortOnPreInstallFail, v => _o.AbortOnPreInstallFail = v, prePost);
            Form.AddText(CoreTools.Translate("Pre-update command:"), () => _o.PreUpdateCommand, v => _o.PreUpdateCommand = v, null, prePost);
            Form.AddText(CoreTools.Translate("Post-update command:"), () => _o.PostUpdateCommand, v => _o.PostUpdateCommand = v, null, prePost);
            Form.AddCheck(CoreTools.Translate("Abort update if pre-update command fails"), () => _o.AbortOnPreUpdateFail, v => _o.AbortOnPreUpdateFail = v, prePost);
            Form.AddText(CoreTools.Translate("Pre-uninstall command:"), () => _o.PreUninstallCommand, v => _o.PreUninstallCommand = v, null, prePost);
            Form.AddText(CoreTools.Translate("Post-uninstall command:"), () => _o.PostUninstallCommand, v => _o.PostUninstallCommand = v, null, prePost);
            Form.AddCheck(CoreTools.Translate("Abort uninstall if pre-uninstall command fails"), () => _o.AbortOnPreUninstallFail, v => _o.AbortOnPreUninstallFail = v, prePost);

            Form.AddHeader(CoreTools.Translate("Command-line to run:"));
            Form.AddLiveNote(() => _preview.Length == 0 ? "…" : _preview);
        }
    }

    private static string AutoUpdateHint()
    {
        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        if (!schedule.Enabled)
            return CoreTools.Translate("Turn on \"Install available updates\" in the scheduled maintenance settings for this to take effect.");
        if (schedule.InstallTargets is ScheduleInstallTargets.AllPackages)
            return CoreTools.Translate("Every upgradable package is already installed automatically, so this changes nothing until the scheduled task is limited to marked packages.");
        return CoreTools.Translate("This package will be updated when the scheduled maintenance task runs.");
    }

    private void Set(Action change)
    {
        change();
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (_package is null || _managerMode) return;
        try
        {
            var snapshot = _o.Copy();
            snapshot.OverridesNextLevelOpts = !_followGlobal;
            var applied = await InstallOptionsFactory.LoadApplicableAsync(_package, overridePackageOptions: snapshot);
            var args = await Task.Run(() => _manager.OperationHelper.GetStandaloneParameters(_package, applied, _profile));
            _preview = _manager.Properties.ExecutableFriendlyName + " " + string.Join(' ', args);
        }
        catch (Exception ex)
        {
            _preview = CoreTools.Translate("No command line is available for this package.") + $" ({ex.Message})";
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(Form.Refresh);
    }

    private async Task SaveAsync(bool proceed)
    {
        _o.OverridesNextLevelOpts = _managerMode || _bundleMode || !_followGlobal;
        _o.KillBeforeOperation = _killList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        CopyInto(_o, _target);
        Settings.Set(Settings.K.KillProcessesThatRefuseToDie, _forceKill);
        try
        {
            if (_managerMode)
            {
                await InstallOptionsFactory.SaveForManagerAsync(_target, _manager);
            }
            else if (!_bundleMode && _package is not null)
            {
                await InstallOptionsFactory.SaveForPackageAsync(_target, _package);
                string id = AutoUpdatesDatabase.GetIdForPackage(_package);
                if (_autoUpdate) AutoUpdatesDatabase.Add(id);
                else if (AutoUpdatesDatabase.IsAutoUpdated(id)) AutoUpdatesDatabase.Remove(id);
                if (_ignoreUpdates) await _package.AddToIgnoredUpdatesAsync("*");
                else if (await _package.GetIgnoredUpdatesVersionAsync() == "*") await _package.RemoveFromIgnoredUpdatesAsync();
            }

            TuiNotifications.Success(CoreTools.Translate("Installation options"), CoreTools.Translate("Options saved"));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            TuiNotifications.Error(CoreTools.Translate("Installation options"), ex.Message);
        }

        Close(proceed);
    }

    private static void CopyInto(InstallOptions from, InstallOptions to)
    {
        to.RunAsAdministrator = from.RunAsAdministrator;
        to.InteractiveInstallation = from.InteractiveInstallation;
        to.SkipHashCheck = from.SkipHashCheck;
        to.UninstallPreviousVersionsOnUpdate = from.UninstallPreviousVersionsOnUpdate;
        to.RemoveDataOnUninstall = from.RemoveDataOnUninstall;
        to.SkipMinorUpdates = from.SkipMinorUpdates;
        to.SkipMinorUpdatesLevel = from.SkipMinorUpdatesLevel;
        to.OverridesNextLevelOpts = from.OverridesNextLevelOpts;
        to.PreRelease = from.PreRelease;
        to.Version = from.Version;
        to.Architecture = from.Architecture;
        to.InstallationScope = from.InstallationScope;
        to.CustomInstallLocation = from.CustomInstallLocation;
        to.CustomParameters_Install = from.CustomParameters_Install.ToList();
        to.CustomParameters_Update = from.CustomParameters_Update.ToList();
        to.CustomParameters_Uninstall = from.CustomParameters_Uninstall.ToList();
        to.PreInstallCommand = from.PreInstallCommand;
        to.PostInstallCommand = from.PostInstallCommand;
        to.AbortOnPreInstallFail = from.AbortOnPreInstallFail;
        to.PreUpdateCommand = from.PreUpdateCommand;
        to.PostUpdateCommand = from.PostUpdateCommand;
        to.AbortOnPreUpdateFail = from.AbortOnPreUpdateFail;
        to.PreUninstallCommand = from.PreUninstallCommand;
        to.PostUninstallCommand = from.PostUninstallCommand;
        to.AbortOnPreUninstallFail = from.AbortOnPreUninstallFail;
        to.KillBeforeOperation = from.KillBeforeOperation.ToList();
    }

    private static List<string> Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
