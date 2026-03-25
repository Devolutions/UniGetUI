using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class AdministratorViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;

    // ── Warning banner strings ────────────────────────────────────────────
    public string WarningTitle { get; } = CoreTools.Translate("Warning") + "!";

    public string WarningBody1 { get; } =
        CoreTools.Translate("The following settings may pose a security risk, hence they are disabled by default.")
        + " "
        + CoreTools.Translate("Enable the settings below if and only if you fully understand what they do, and the implications they may have.");

    public string WarningBody2 { get; } =
        CoreTools.Translate("The settings will list, in their descriptions, the potential security issues they may have.");

    // ── Observable state ─────────────────────────────────────────────────
    /// <summary>True when elevation is NOT prohibited — controls enabled-state of the cache-admin-rights cards.</summary>
    [ObservableProperty] private bool _isElevationEnabled;

    /// <summary>Mirrors AllowCLIArguments toggle — controls IsEnabled of AllowImportingCLIArguments.</summary>
    [ObservableProperty] private bool _isCLIArgumentsEnabled;

    /// <summary>Mirrors AllowPrePostOpCommand toggle — controls IsEnabled of AllowImportingPrePostInstallCommands.</summary>
    [ObservableProperty] private bool _isPrePostCommandsEnabled;

    public AdministratorViewModel()
    {
        _isElevationEnabled = !Settings.Get(Settings.K.ProhibitElevation);
        _isCLIArgumentsEnabled = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
        _isPrePostCommandsEnabled = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void RestartCache() => _ = CoreTools.ResetUACForCurrentProcess();

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RefreshElevationState()
    {
        IsElevationEnabled = !Settings.Get(Settings.K.ProhibitElevation);
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RefreshCLIState()
    {
        IsCLIArgumentsEnabled = SecureSettings.Get(SecureSettings.K.AllowCLIArguments);
    }

    [RelayCommand]
    private void RefreshPrePostState()
    {
        IsPrePostCommandsEnabled = SecureSettings.Get(SecureSettings.K.AllowPrePostOpCommand);
    }
}
