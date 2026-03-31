using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Language;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class InstallOptionsPanelViewModel : ViewModelBase
{
    private readonly IPackageManager _manager;
    private readonly string _defaultLocationLabel;

    public event EventHandler? NavigateToAdministratorRequested;

    // ── Loading state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _hasChanges;

    // ── Checkbox enabled + checked ────────────────────────────────────────────
    [ObservableProperty] private bool _adminEnabled;
    [ObservableProperty] private bool _adminChecked;

    [ObservableProperty] private bool _interactiveEnabled;
    [ObservableProperty] private bool _interactiveChecked;

    [ObservableProperty] private bool _skipHashEnabled;
    [ObservableProperty] private bool _skipHashChecked;

    [ObservableProperty] private bool _preReleaseEnabled;
    [ObservableProperty] private bool _preReleaseChecked;

    [ObservableProperty] private bool _uninstallPreviousEnabled;
    [ObservableProperty] private bool _uninstallPreviousChecked;

    // ── Architecture ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _architectureEnabled;
    [ObservableProperty] private ObservableCollection<string> _architectureItems = [];
    [ObservableProperty] private string? _selectedArchitecture;

    // ── Scope ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _scopeEnabled;
    [ObservableProperty] private ObservableCollection<string> _scopeItems = [];
    [ObservableProperty] private string? _selectedScope;

    // ── Location ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _locationSelectEnabled;
    [ObservableProperty] private bool _locationResetEnabled;
    [ObservableProperty] private string _locationText = "";

    // ── CLI args ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _cliSectionEnabled;
    [ObservableProperty] private bool _cliDisabledWarningVisible;
    [ObservableProperty] private string _customInstall = "";
    [ObservableProperty] private string _customUpdate = "";
    [ObservableProperty] private string _customUninstall = "";

    // ── Translated labels (static) ────────────────────────────────────────────
    public string AdminLabel { get; } = CoreTools.Translate("Run as admin");
    public string InteractiveLabel { get; } = CoreTools.Translate("Interactive installation");
    public string SkipHashLabel { get; } = CoreTools.Translate("Skip hash check");
    public string PreReleaseLabel { get; } = CoreTools.Translate("Allow pre-release versions");
    public string UninstallPrevLabel { get; } = CoreTools.Translate("Uninstall previous versions when updated");
    public string ArchLabel { get; } = CoreTools.Translate("Architecture to install:");
    public string ScopeLabel { get; } = CoreTools.Translate("Installation scope:");
    public string LocationLabel { get; } = CoreTools.Translate("Install location:");
    public string SelectDirLabel { get; } = CoreTools.Translate("Select");
    public string ResetDirLabel { get; } = CoreTools.Translate("Reset");
    public string InstallArgsLabel { get; } = CoreTools.Translate("Custom install arguments:");
    public string UpdateArgsLabel { get; } = CoreTools.Translate("Custom update arguments:");
    public string UninstallArgsLabel { get; } = CoreTools.Translate("Custom uninstall arguments:");
    public string ResetLabel { get; } = CoreTools.Translate("Reset");
    public string ApplyLabel { get; } = CoreTools.Translate("Apply");
    public string CliDisabledLabel { get; } = CoreTools.Translate("For security reasons, custom command-line arguments are disabled by default. Go to UniGetUI security settings to change this.");
    public string GoToSecurityLabel { get; } = CoreTools.Translate("Go to UniGetUI security settings");

    public string HeaderText => CoreTools.Translate(
        "The following options will be applied by default each time a {0} package is installed, upgraded or uninstalled.",
        _manager.DisplayName);

    public double CliOpacity => CliSectionEnabled ? 1.0 : 0.5;
    public double ArchOpacity => ArchitectureEnabled ? 1.0 : 0.5;
    public double ScopeOpacity => ScopeEnabled ? 1.0 : 0.5;
    public double LocationOpacity => LocationSelectEnabled ? 1.0 : 0.5;

    partial void OnCliSectionEnabledChanged(bool value) => OnPropertyChanged(nameof(CliOpacity));
    partial void OnArchitectureEnabledChanged(bool value) => OnPropertyChanged(nameof(ArchOpacity));
    partial void OnScopeEnabledChanged(bool value) => OnPropertyChanged(nameof(ScopeOpacity));
    partial void OnLocationSelectEnabledChanged(bool value) => OnPropertyChanged(nameof(LocationOpacity));

    // Mark HasChanges when user edits options (guards against firing during load)
    partial void OnAdminCheckedChanged(bool _) => HasChanges = !IsLoading;
    partial void OnInteractiveCheckedChanged(bool _) => HasChanges = !IsLoading;
    partial void OnSkipHashCheckedChanged(bool _) => HasChanges = !IsLoading;
    partial void OnPreReleaseCheckedChanged(bool _) => HasChanges = !IsLoading;
    partial void OnUninstallPreviousCheckedChanged(bool _) => HasChanges = !IsLoading;
    partial void OnSelectedArchitectureChanged(string? _) => HasChanges = !IsLoading;
    partial void OnSelectedScopeChanged(string? _) => HasChanges = !IsLoading;

    public InstallOptionsPanelViewModel(IPackageManager manager)
    {
        _manager = manager;
        _defaultLocationLabel = CoreTools.Translate("Package's default");

        // Architecture items — always show Default + any supported archs
        _architectureItems.Add(CoreTools.Translate("Default"));
        foreach (var arch in manager.Capabilities.SupportedCustomArchitectures)
            _architectureItems.Add(arch);

        // Scope items — always show Default + Local + Global
        _scopeItems.Add(CoreTools.Translate("Default"));
        _scopeItems.Add(CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Local]));
        _scopeItems.Add(CoreTools.Translate(CommonTranslations.ScopeNames[PackageScope.Global]));

        _ = DoLoadOptions();
    }

    // ── Load / Save / Reset ───────────────────────────────────────────────────

    [RelayCommand]
    private Task LoadOptions() => DoLoadOptions();

    [RelayCommand]
    private async Task SaveOptions()
    {
        IsLoading = true;
        DisableAllInput();

        var options = new InstallOptions
        {
            RunAsAdministrator = AdminChecked,
            SkipHashCheck = SkipHashChecked,
            InteractiveInstallation = InteractiveChecked,
            PreRelease = PreReleaseChecked,
            UninstallPreviousVersionsOnUpdate = UninstallPreviousChecked,
        };

        if (_manager.Capabilities.SupportsCustomArchitectures &&
            SelectedArchitecture is { } arch &&
            _manager.Capabilities.SupportedCustomArchitectures.Contains(arch))
            options.Architecture = arch;

        if (_manager.Capabilities.SupportsCustomScopes && SelectedScope is { } scope)
            if (CommonTranslations.InvertedScopeNames.TryGetValue(scope, out string? scopeVal))
                options.InstallationScope = scopeVal;

        if (_manager.Capabilities.SupportsCustomLocations &&
            LocationText != _defaultLocationLabel)
            options.CustomInstallLocation = LocationText;

        options.CustomParameters_Install = CustomInstall.Split(' ').Where(x => x.Any()).ToList();
        options.CustomParameters_Update = CustomUpdate.Split(' ').Where(x => x.Any()).ToList();
        options.CustomParameters_Uninstall = CustomUninstall.Split(' ').Where(x => x.Any()).ToList();

        await InstallOptionsFactory.SaveForManagerAsync(options, _manager);
        await DoLoadOptions();
    }

    [RelayCommand]
    private async Task ResetOptions()
    {
        IsLoading = true;
        DisableAllInput();
        await InstallOptionsFactory.SaveForManagerAsync(new InstallOptions(), _manager);
        await DoLoadOptions();
    }

    private void DisableAllInput()
    {
        AdminEnabled = false;
        InteractiveEnabled = false;
        SkipHashEnabled = false;
        PreReleaseEnabled = false;
        UninstallPreviousEnabled = false;
        ArchitectureEnabled = false;
        ScopeEnabled = false;
        LocationSelectEnabled = false;
        LocationResetEnabled = false;
        CliSectionEnabled = false;
    }

    private async Task DoLoadOptions()
    {
        IsLoading = true;
        HasChanges = false;
        DisableAllInput();

        var options = await InstallOptionsFactory.LoadForManagerAsync(_manager);
        await Task.Delay(300);

        // Checkboxes — load value, then set enabled per capability
        AdminChecked = options.RunAsAdministrator;
        AdminEnabled = OperatingSystem.IsWindows();

        InteractiveChecked = options.InteractiveInstallation;
        InteractiveEnabled = _manager.Capabilities.CanRunInteractively;

        SkipHashChecked = options.SkipHashCheck;
        SkipHashEnabled = _manager.Capabilities.CanSkipIntegrityChecks;

        PreReleaseChecked = options.PreRelease;
        PreReleaseEnabled = _manager.Capabilities.SupportsPreRelease;

        UninstallPreviousChecked = options.UninstallPreviousVersionsOnUpdate;
        UninstallPreviousEnabled = _manager.Capabilities.CanUninstallPreviousVersionsAfterUpdate;

        // Architecture
        ArchitectureEnabled = _manager.Capabilities.SupportsCustomArchitectures;
        string? matchedArch = ArchitectureItems.Contains(options.Architecture) ? options.Architecture : null;
        SelectedArchitecture = matchedArch ?? ArchitectureItems.FirstOrDefault();

        // Scope
        ScopeEnabled = _manager.Capabilities.SupportsCustomScopes;
        string? matchedScope = null;
        if (!string.IsNullOrEmpty(options.InstallationScope) &&
            CommonTranslations.ScopeNames.TryGetValue(options.InstallationScope, out string? display))
        {
            string translated = CoreTools.Translate(display);
            if (ScopeItems.Contains(translated)) matchedScope = translated;
        }
        SelectedScope = matchedScope ?? ScopeItems.FirstOrDefault();

        // Location
        LocationSelectEnabled = _manager.Capabilities.SupportsCustomLocations;
        if (!string.IsNullOrEmpty(options.CustomInstallLocation))
        {
            LocationText = options.CustomInstallLocation;
            LocationResetEnabled = true;
        }
        else
        {
            LocationText = _manager.Capabilities.SupportsCustomLocations
                ? _defaultLocationLabel
                : CoreTools.Translate("Install location can't be changed for {0} packages", _manager.DisplayName);
            LocationResetEnabled = false;
        }

        // CLI
        bool isCLI = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
        CliSectionEnabled = isCLI;
        CliDisabledWarningVisible = !isCLI;
        CustomInstall = string.Join(' ', options.CustomParameters_Install);
        CustomUpdate = string.Join(' ', options.CustomParameters_Update);
        CustomUninstall = string.Join(' ', options.CustomParameters_Uninstall);

        IsLoading = false;
    }

    // ── Location picker ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectLocation(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders is not [{ } folder]) return;
        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        LocationText = path.TrimEnd('/').TrimEnd('\\') + "/%PACKAGE%";
        LocationResetEnabled = true;
        HasChanges = true;
    }

    [RelayCommand]
    private void ResetLocation()
    {
        LocationText = _defaultLocationLabel;
        LocationResetEnabled = false;
        HasChanges = true;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void NavigateToAdministrator() =>
        NavigateToAdministratorRequested?.Invoke(this, EventArgs.Empty);

    // ── Mark changed ─────────────────────────────────────────────────────────

    public void MarkChanged() => HasChanges = true;
}
