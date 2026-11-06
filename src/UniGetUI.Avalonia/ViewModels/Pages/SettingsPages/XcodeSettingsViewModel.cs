using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

/// <summary>The Xcode page under Settings &gt; Extensions.</summary>
public partial class XcodeSettingsViewModel : ViewModelBase
{
    /// <summary>The automatic check choices, as labels and the values stored for them.</summary>
    public static readonly (string Label, string Value)[] CheckIntervalItems =
    [
        ("Only when asked", ""),
        ("Every 4 hours", "240"),
        ("Every day", "1440"),
        ("Every week", "10080"),
    ];

    private readonly XcodesService _xcodes = new();

    public XcodeSettingsViewModel() => _ = FindXcodesAsync();

    [ObservableProperty]
    private bool _isEnabled = AppExtensions.Xcode.IsEnabled;

    [ObservableProperty]
    private string _xcodesStatus = CoreTools.Translate("Looking for xcodes...");

    [ObservableProperty]
    private bool _isXcodesFound;

    [ObservableProperty]
    private string _signOutStatus = CoreTools.Translate("Forgets the Apple ID and password that xcodes saved in your Keychain.");

    private async Task FindXcodesAsync()
    {
        string? path = _xcodes.FindExecutable();
        string? version = path is null ? null : await _xcodes.GetVersionAsync();
        IsXcodesFound = version is not null;
        XcodesStatus = path is null
            ? CoreTools.Translate("xcodes was not found. Install it from Discover Packages, then come back to this page.")
            : version is null
                ? CoreTools.Translate("xcodes was found at {0} but did not answer.", path)
                : CoreTools.Translate("Found xcodes {0} at {1}", version, path);
    }

    [RelayCommand]
    private void EnabledChanged()
    {
        // The card has saved the setting; this starts or stops what runs in the app.
        IsEnabled = AppExtensions.Xcode.IsEnabled;
        AppExtensions.Xcode.ApplyEnabled(IsEnabled);
    }

    [RelayCommand]
    private async Task SignOut()
    {
        try
        {
            await _xcodes.SignOutAsync();
            SignOutStatus = CoreTools.Translate("xcodes has forgotten your Apple ID. It asks for it again the next time it needs it.");
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"xcodes could not sign out: {ex.Message}");
            SignOutStatus = CoreTools.Translate("Could not sign out: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private static void PreferencesChanged() => MainWindow.Instance?.Xcode?.ReloadPreferences();

    [RelayCommand]
    private static void Open() => MainWindow.Instance?.Navigate(PageType.Xcode);
}
