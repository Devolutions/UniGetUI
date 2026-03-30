using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Avalonia.ViewModels;

public partial class InstallOptionsViewModel : ObservableObject
{
    // ── Public result ──────────────────────────────────────────────────────────
    public bool ShouldProceedWithOperation { get; private set; }
    public event EventHandler? CloseRequested;

    private readonly IPackage _package;
    private readonly InstallOptions _options;
    private readonly bool _uiLoaded;

    // ── Translated static labels ───────────────────────────────────────────────
    public string DialogTitle { get; }
    public string ProfileLabel { get; } = CoreTools.Translate("Operation profile:");
    public string FollowGlobalLabel { get; } = CoreTools.Translate("Follow the default options when installing, upgrading or uninstalling this package");
    public string GeneralInfoLabel { get; } = CoreTools.Translate("The following settings will be applied each time this package is installed, updated or removed.");
    public string VersionLabel { get; } = CoreTools.Translate("Version to install:");
    public string ArchLabel { get; } = CoreTools.Translate("Architecture to install:");
    public string ScopeLabel { get; } = CoreTools.Translate("Installation scope:");
    public string LocationLabel { get; } = CoreTools.Translate("Install location:");
    public string SelectDirLabel { get; } = CoreTools.Translate("Select");
    public string ResetDirLabel { get; } = CoreTools.Translate("Reset");
    public string ParamsInstallLabel { get; } = CoreTools.Translate("Custom install arguments:");
    public string ParamsUpdateLabel { get; } = CoreTools.Translate("Custom update arguments:");
    public string ParamsUninstallLabel { get; } = CoreTools.Translate("Custom uninstall arguments:");
    public string PreInstallLabel { get; } = CoreTools.Translate("Pre-install command:");
    public string PostInstallLabel { get; } = CoreTools.Translate("Post-install command:");
    public string AbortInstallLabel { get; } = CoreTools.Translate("Abort install if pre-install command fails");
    public string PreUpdateLabel { get; } = CoreTools.Translate("Pre-update command:");
    public string PostUpdateLabel { get; } = CoreTools.Translate("Post-update command:");
    public string AbortUpdateLabel { get; } = CoreTools.Translate("Abort update if pre-update command fails");
    public string PreUninstallLabel { get; } = CoreTools.Translate("Pre-uninstall command:");
    public string PostUninstallLabel { get; } = CoreTools.Translate("Post-uninstall command:");
    public string AbortUninstallLabel { get; } = CoreTools.Translate("Abort uninstall if pre-uninstall command fails");
    public string CommandPreviewLabel { get; } = CoreTools.Translate("Command-line to run:");
    public string SaveLabel { get; } = CoreTools.Translate("Save and close");
    public string TabGeneralLabel { get; } = CoreTools.Translate("General");
    public string TabLocationLabel { get; } = CoreTools.Translate("Architecture & Location");
    public string TabCLILabel { get; } = CoreTools.Translate("Command-line");
    public string TabPrePostLabel { get; } = CoreTools.Translate("Pre/Post install");

    // Checkbox content labels
    public string AdminCheckBox_Content { get; } = CoreTools.Translate("Run as admin");
    public string InteractiveCheckBox_Content { get; } = CoreTools.Translate("Interactive installation");
    public string SkipHashCheckBox_Content { get; } = CoreTools.Translate("Skip hash check");
    public string UninstallPrevCheckBox_Content { get; } = CoreTools.Translate("Uninstall previous versions when updated");
    public string SkipMinorCheckBox_Content { get; } = CoreTools.Translate("Skip minor updates for this package");
    public string AutoUpdateCheckBox_Content { get; } = CoreTools.Translate("Automatically update this package");

    // ── Capability flags (for IsEnabled bindings) ─────────────────────────────
    public bool CanRunAsAdmin { get; }
    public bool CanRunInteractively { get; }
    public bool CanSkipHash { get; }
    public bool CanUninstallPrev { get; }
    public bool HasCustomScopes { get; }
    public bool HasCustomLocations { get; }

    // ── Package icon ───────────────────────────────────────────────────────────
    [ObservableProperty] private Bitmap? _packageIcon;

    // ── Follow-global toggle ───────────────────────────────────────────────────
    [ObservableProperty] private bool _followGlobal;
    [ObservableProperty] private bool _isCustomMode;
    [ObservableProperty] private double _optionsOpacity = 1.0;

    partial void OnFollowGlobalChanged(bool value)
    {
        IsCustomMode = !value;
        OptionsOpacity = value ? 0.35 : 1.0;
        if (_uiLoaded) _ = RefreshCommandPreviewAsync();
    }

    // ── Profile combo ──────────────────────────────────────────────────────────
    public ObservableCollection<string> ProfileOptions { get; } = [];

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string _proceedButtonLabel = "";

    partial void OnSelectedProfileChanged(string? value)
    {
        ProceedButtonLabel = value ?? "";
        ApplyProfileEnableState();
        if (_uiLoaded) _ = RefreshCommandPreviewAsync();
    }

    // ── General tab ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _adminChecked;
    [ObservableProperty] private bool _interactiveChecked;
    [ObservableProperty] private bool _skipHashChecked;
    [ObservableProperty] private bool _skipHashEnabled;
    [ObservableProperty] private bool _uninstallPrevChecked;

    [ObservableProperty] private bool _versionEnabled;
    public ObservableCollection<string> VersionOptions { get; } = [];
    [ObservableProperty] private string? _selectedVersion;

    [ObservableProperty] private bool _skipMinorChecked;
    [ObservableProperty] private bool _autoUpdateChecked;

    partial void OnAdminCheckedChanged(bool _) => Refresh();
    partial void OnInteractiveCheckedChanged(bool _) => Refresh();
    partial void OnSkipHashCheckedChanged(bool _) => Refresh();
    partial void OnSelectedVersionChanged(string? _) => Refresh();

    // ── Architecture / Scope / Location tab ───────────────────────────────────
    [ObservableProperty] private bool _archEnabled;
    public ObservableCollection<string> ArchOptions { get; } = [];
    [ObservableProperty] private string? _selectedArch;

    [ObservableProperty] private bool _scopeEnabled;
    public ObservableCollection<string> ScopeOptions { get; } = [];
    [ObservableProperty] private string? _selectedScope;

    [ObservableProperty] private string _locationText = "";
    [ObservableProperty] private bool _locationEnabled;

    partial void OnSelectedArchChanged(string? _) => Refresh();
    partial void OnSelectedScopeChanged(string? _) => Refresh();

    // ── CLI params tab ────────────────────────────────────────────────────────
    [ObservableProperty] private string _paramsInstall = "";
    [ObservableProperty] private string _paramsUpdate = "";
    [ObservableProperty] private string _paramsUninstall = "";

    partial void OnParamsInstallChanged(string _) => Refresh();
    partial void OnParamsUpdateChanged(string _) => Refresh();
    partial void OnParamsUninstallChanged(string _) => Refresh();

    // ── Pre/Post commands tab ─────────────────────────────────────────────────
    [ObservableProperty] private string _preInstallText = "";
    [ObservableProperty] private string _postInstallText = "";
    [ObservableProperty] private bool _abortInstall;

    [ObservableProperty] private string _preUpdateText = "";
    [ObservableProperty] private string _postUpdateText = "";
    [ObservableProperty] private bool _abortUpdate;

    [ObservableProperty] private string _preUninstallText = "";
    [ObservableProperty] private string _postUninstallText = "";
    [ObservableProperty] private bool _abortUninstall;

    // ── Command preview ───────────────────────────────────────────────────────
    [ObservableProperty] private string _commandPreview = "";

    // ── Constructor ───────────────────────────────────────────────────────────
    public InstallOptionsViewModel(IPackage package, OperationType operation, InstallOptions options)
    {
        _package = package;
        _options = options;
        var caps = package.Manager.Capabilities;

        DialogTitle = CoreTools.Translate("{0} installation options", package.Name);

        // Capability flags
        CanRunAsAdmin = OperatingSystem.IsWindows() && caps.CanRunAsAdmin;
        CanRunInteractively = caps.CanRunInteractively;
        CanSkipHash = caps.CanSkipIntegrityChecks;
        CanUninstallPrev = caps.CanUninstallPreviousVersionsAfterUpdate;
        HasCustomScopes = caps.SupportsCustomScopes;
        HasCustomLocations = caps.SupportsCustomLocations;

        // Profile
        string installLabel = CoreTools.Translate("Install");
        string updateLabel = CoreTools.Translate("Update");
        string uninstallLabel = CoreTools.Translate("Uninstall");
        ProfileOptions.Add(installLabel);
        ProfileOptions.Add(updateLabel);
        ProfileOptions.Add(uninstallLabel);
        SelectedProfile = operation switch
        {
            OperationType.Update => updateLabel,
            OperationType.Uninstall => uninstallLabel,
            _ => installLabel,
        };
        ProceedButtonLabel = SelectedProfile;

        // Follow-global
        FollowGlobal = !options.OverridesNextLevelOpts;
        IsCustomMode = options.OverridesNextLevelOpts;
        OptionsOpacity = options.OverridesNextLevelOpts ? 1.0 : 0.35;

        // General checkboxes
        AdminChecked = options.RunAsAdministrator;
        InteractiveChecked = options.InteractiveInstallation;
        SkipHashChecked = options.SkipHashCheck;
        SkipHashEnabled = caps.CanSkipIntegrityChecks;
        UninstallPrevChecked = options.UninstallPreviousVersionsOnUpdate;
        SkipMinorChecked = options.SkipMinorUpdates;
        AutoUpdateChecked = options.AutoUpdatePackage;

        // Version
        VersionOptions.Add(CoreTools.Translate("Latest"));
        if (caps.SupportsPreRelease)
            VersionOptions.Add(CoreTools.Translate("PreRelease"));
        SelectedVersion = options.PreRelease
            ? CoreTools.Translate("PreRelease")
            : CoreTools.Translate("Latest");
        VersionEnabled = caps.SupportsCustomVersions || caps.SupportsPreRelease;

        // Architecture
        string defaultLabel = CoreTools.Translate("Default");
        ArchOptions.Add(defaultLabel);
        SelectedArch = defaultLabel;
        if (caps.SupportsCustomArchitectures)
        {
            foreach (var arch in caps.SupportedCustomArchitectures)
            {
                ArchOptions.Add(arch);
                if (options.Architecture == arch) SelectedArch = arch;
            }
        }
        ArchEnabled = caps.SupportsCustomArchitectures;

        // Scope
        ScopeOptions.Add(CoreTools.Translate("Default"));
        SelectedScope = CoreTools.Translate("Default");
        if (caps.SupportsCustomScopes)
        {
            string localName = CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Local]);
            string globalName = CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Global]);
            ScopeOptions.Add(localName);
            ScopeOptions.Add(globalName);
            if (options.InstallationScope == "Local") SelectedScope = localName;
            if (options.InstallationScope == "Global") SelectedScope = globalName;
        }
        ScopeEnabled = caps.SupportsCustomScopes;

        // Location
        LocationText = options.CustomInstallLocation;
        LocationEnabled = caps.SupportsCustomLocations;

        // CLI params
        ParamsInstall = string.Join(' ', options.CustomParameters_Install);
        ParamsUpdate = string.Join(' ', options.CustomParameters_Update);
        ParamsUninstall = string.Join(' ', options.CustomParameters_Uninstall);

        // Pre/Post commands
        PreInstallText = options.PreInstallCommand;
        PostInstallText = options.PostInstallCommand;
        AbortInstall = options.AbortOnPreInstallFail;
        PreUpdateText = options.PreUpdateCommand;
        PostUpdateText = options.PostUpdateCommand;
        AbortUpdate = options.AbortOnPreUpdateFail;
        PreUninstallText = options.PreUninstallCommand;
        PostUninstallText = options.PostUninstallCommand;
        AbortUninstall = options.AbortOnPreUninstallFail;

        // Show fallback immediately, then replace with real icon if available
        using var fallback = AssetLoader.Open(_fallbackIconUri);
        PackageIcon = new Bitmap(fallback);
        _ = LoadIconAsync();

        if (caps.SupportsCustomVersions)
            _ = LoadVersionsAsync(options.Version);

        _uiLoaded = true;
        _ = RefreshCommandPreviewAsync();
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    [RelayCommand]
    private void Save()
    {
        ApplyToOptions();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Proceed()
    {
        ApplyToOptions();
        ShouldProceedWithOperation = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ResetLocation() => LocationText = "";

    // ── Enable/disable based on selected operation profile ────────────────────
    private void ApplyProfileEnableState()
    {
        if (!_uiLoaded) return;
        var op = CurrentOp();
        var caps = _package.Manager.Capabilities;

        SkipHashEnabled = op is not OperationType.Uninstall && caps.CanSkipIntegrityChecks;
        ArchEnabled = op is not OperationType.Uninstall && caps.SupportsCustomArchitectures;
        VersionEnabled = op is OperationType.Install && (caps.SupportsCustomVersions || caps.SupportsPreRelease);
    }

    // ── Live command preview ──────────────────────────────────────────────────
    private async Task RefreshCommandPreviewAsync()
    {
        if (!_uiLoaded) return;
        var snap = SnapshotOptions();
        var op = CurrentOp();
        var applied = await InstallOptionsFactory.LoadApplicableAsync(_package, overridePackageOptions: snap);
        var args = await Task.Run(() => _package.Manager.OperationHelper.GetParameters(_package, applied, op));
        CommandPreview = _package.Manager.Properties.ExecutableFriendlyName + " " + string.Join(' ', args);
    }

    private void Refresh() { if (_uiLoaded) _ = RefreshCommandPreviewAsync(); }

    // ── Package icon ──────────────────────────────────────────────────────────
    private static readonly HttpClient _iconHttp = new(CoreTools.GenericHttpClientParameters);

    private static readonly Uri _fallbackIconUri =
        new("avares://UniGetUI.Avalonia/Assets/package_color.png");

    private async Task LoadIconAsync()
    {
        try
        {
            var uri = await Task.Run(_package.GetIconUrlIfAny);
            if (uri is null) return;

            Bitmap bmp;
            if (uri.IsFile)
                bmp = new Bitmap(uri.LocalPath);
            else if (uri.Scheme is "http" or "https")
            {
                var bytes = await _iconHttp.GetByteArrayAsync(uri);
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            else return;

            PackageIcon = bmp;
        }
        catch (Exception ex) { Logger.Warn($"[InstallOptionsViewModel] Failed to load icon for {_package.Id}: {ex.Message}"); }
    }

    // ── Async version loader ──────────────────────────────────────────────────
    private async Task LoadVersionsAsync(string selectedVersion)
    {
        VersionEnabled = false;
        var versions = await Task.Run(() => _package.Manager.DetailsHelper.GetVersions(_package));
        foreach (var ver in versions)
        {
            VersionOptions.Add(ver);
            if (selectedVersion == ver)
                SelectedVersion = ver;
        }
        var op = CurrentOp();
        VersionEnabled = op is OperationType.Install &&
            (_package.Manager.Capabilities.SupportsCustomVersions ||
             _package.Manager.Capabilities.SupportsPreRelease);
    }

    // ── Snapshot & apply ──────────────────────────────────────────────────────
    private OperationType CurrentOp() => SelectedProfile switch
    {
        var s when s == CoreTools.Translate("Update") => OperationType.Update,
        var s when s == CoreTools.Translate("Uninstall") => OperationType.Uninstall,
        _ => OperationType.Install,
    };

    private InstallOptions SnapshotOptions()
    {
        var o = new InstallOptions();
        o.RunAsAdministrator = AdminChecked;
        o.InteractiveInstallation = InteractiveChecked;
        o.SkipHashCheck = SkipHashChecked;
        o.UninstallPreviousVersionsOnUpdate = UninstallPrevChecked;
        o.AutoUpdatePackage = AutoUpdateChecked;
        o.SkipMinorUpdates = SkipMinorChecked;
        o.OverridesNextLevelOpts = !FollowGlobal;

        var ver = SelectedVersion ?? "";
        o.PreRelease = ver == CoreTools.Translate("PreRelease");
        o.Version = (ver != CoreTools.Translate("Latest") && ver != CoreTools.Translate("PreRelease") && ver.Length > 0) ? ver : "";

        string defaultLabel = CoreTools.Translate("Default");
        o.Architecture = (SelectedArch != defaultLabel && SelectedArch is not null) ? SelectedArch : "";
        o.InstallationScope = ScopeToString(SelectedScope);

        o.CustomInstallLocation = LocationText;
        o.CustomParameters_Install = Split(ParamsInstall);
        o.CustomParameters_Update = Split(ParamsUpdate);
        o.CustomParameters_Uninstall = Split(ParamsUninstall);
        o.PreInstallCommand = PreInstallText;
        o.PostInstallCommand = PostInstallText;
        o.AbortOnPreInstallFail = AbortInstall;
        o.PreUpdateCommand = PreUpdateText;
        o.PostUpdateCommand = PostUpdateText;
        o.AbortOnPreUpdateFail = AbortUpdate;
        o.PreUninstallCommand = PreUninstallText;
        o.PostUninstallCommand = PostUninstallText;
        o.AbortOnPreUninstallFail = AbortUninstall;
        return o;
    }

    private void ApplyToOptions()
    {
        var s = SnapshotOptions();
        _options.RunAsAdministrator = s.RunAsAdministrator;
        _options.InteractiveInstallation = s.InteractiveInstallation;
        _options.SkipHashCheck = s.SkipHashCheck;
        _options.UninstallPreviousVersionsOnUpdate = s.UninstallPreviousVersionsOnUpdate;
        _options.AutoUpdatePackage = s.AutoUpdatePackage;
        _options.SkipMinorUpdates = s.SkipMinorUpdates;
        _options.OverridesNextLevelOpts = s.OverridesNextLevelOpts;
        _options.PreRelease = s.PreRelease;
        _options.Version = s.Version;
        _options.Architecture = s.Architecture;
        _options.InstallationScope = s.InstallationScope;
        _options.CustomInstallLocation = s.CustomInstallLocation;
        _options.CustomParameters_Install = s.CustomParameters_Install;
        _options.CustomParameters_Update = s.CustomParameters_Update;
        _options.CustomParameters_Uninstall = s.CustomParameters_Uninstall;
        _options.PreInstallCommand = s.PreInstallCommand;
        _options.PostInstallCommand = s.PostInstallCommand;
        _options.AbortOnPreInstallFail = s.AbortOnPreInstallFail;
        _options.PreUpdateCommand = s.PreUpdateCommand;
        _options.PostUpdateCommand = s.PostUpdateCommand;
        _options.AbortOnPreUpdateFail = s.AbortOnPreUpdateFail;
        _options.PreUninstallCommand = s.PreUninstallCommand;
        _options.PostUninstallCommand = s.PostUninstallCommand;
        _options.AbortOnPreUninstallFail = s.AbortOnPreUninstallFail;
    }

    private static string ScopeToString(string? selected)
    {
        if (selected == CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Local])) return "Local";
        if (selected == CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Global])) return "Global";
        return "";
    }

    private static List<string> Split(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
}
