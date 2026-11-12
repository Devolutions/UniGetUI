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

    private readonly XcodesService _xcodes;

    public XcodeSettingsViewModel() : this(XcodeViewModel.SharedService, MainWindow.Instance?.XcodeAccount) { }

    internal XcodeSettingsViewModel(XcodesService xcodes, XcodeAccountViewModel? account = null, bool? isSupported = null)
    {
        _xcodes = xcodes;
        Account = account ?? new XcodeAccountViewModel(_xcodes);
        IsSupported = isSupported ?? XcodePlatform.IsSupported;
        _isEnabled = IsSupported && AppExtensions.Xcode.IsEnabled;
    }

    public XcodeAccountViewModel Account { get; }
    public bool IsSupported { get; }

    internal Func<XcodeViewModel?> GetXcodePage { get; set; } = () => MainWindow.Instance?.Xcode;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _installDirectoryDraft = XcodePreferences.InstallDirectory ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallDirectoryStatus))]
    private string _installDirectoryStatus = "";

    public bool HasInstallDirectoryStatus => InstallDirectoryStatus.Length > 0;

    [ObservableProperty]
    private string _archiveDirectoryDraft = XcodePreferences.ArchiveDirectory ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchiveDirectoryStatus))]
    private string _archiveDirectoryStatus = "";

    public bool HasArchiveDirectoryStatus => ArchiveDirectoryStatus.Length > 0;

    [RelayCommand]
    private void EnabledChanged()
    {
        // The card has saved the setting; this starts or stops what runs in the app.
        IsEnabled = AppExtensions.Xcode.IsEnabled;
        AppExtensions.Xcode.ApplyEnabled(IsEnabled);
    }

    [RelayCommand]
    private static void PreferencesChanged() => MainWindow.Instance?.Xcode?.ReloadPreferences();

    [RelayCommand]
    private async Task ApplyInstallDirectoryAsync()
    {
        try
        {
            string? directory = XcodesService.ValidateInstallDirectory(InstallDirectoryDraft);
            if (Account.IsBusy)
            {
                InstallDirectoryStatus = CoreTools.Translate("Wait until the current Xcode operation finishes.");
                return;
            }

            XcodeViewModel? page = GetXcodePage();
            if (page is null)
            {
                if (!XcodePreferences.TrySetInstallDirectory(directory))
                {
                    InstallDirectoryStatus = CoreTools.Translate("Could not save the Xcode installation directory. Check the application log for details.");
                    return;
                }
            }
            else if (!await page.ChangeInstallDirectoryAsync(directory))
            {
                InstallDirectoryStatus = CoreTools.Translate("Could not change the Xcode installation directory. Check the Xcode page for details.");
                return;
            }

            InstallDirectoryDraft = directory ?? "";
            InstallDirectoryStatus = page is not null && !page.HasLoaded
                ? CoreTools.Translate("The directory was saved, but Xcode versions could not be refreshed. Check the Xcode page for details.")
                : CoreTools.Translate("Xcode installation directory saved.");
        }
        catch (XcodesException ex)
        {
            InstallDirectoryStatus = ex.Message switch
            {
                "Enter an absolute path for the Xcode installation directory."
                    => CoreTools.Translate("Enter an absolute path for the Xcode installation directory."),
                "The Xcode installation directory does not exist or is inaccessible."
                    => CoreTools.Translate("The Xcode installation directory does not exist or is inaccessible."),
                _ => ex.Message,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn(ex);
            InstallDirectoryStatus = CoreTools.Translate("Could not save the Xcode installation directory: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ApplyArchiveDirectoryAsync()
    {
        try
        {
            string? directory = XcodesService.ValidateArchiveDirectory(ArchiveDirectoryDraft);
            if (Account.IsBusy)
            {
                ArchiveDirectoryStatus = CoreTools.Translate("Wait until the current Xcode operation finishes.");
                return;
            }

            XcodeViewModel? page = GetXcodePage();
            if (page is null)
            {
                if (!XcodePreferences.TrySetArchiveDirectory(directory))
                {
                    ArchiveDirectoryStatus = CoreTools.Translate("Could not save the Xcode archive directory. Check the application log for details.");
                    return;
                }
            }
            else if (!await page.ChangeArchiveDirectoryAsync(directory))
            {
                ArchiveDirectoryStatus = CoreTools.Translate("Could not change the Xcode archive directory. Check the Xcode page for details.");
                return;
            }

            ArchiveDirectoryDraft = directory ?? "";
            ArchiveDirectoryStatus = CoreTools.Translate("Xcode archive directory saved.");
        }
        catch (XcodesException ex)
        {
            ArchiveDirectoryStatus = ex.Message switch
            {
                "Enter an absolute path for the Xcode archive directory."
                    => CoreTools.Translate("Enter an absolute path for the Xcode archive directory."),
                "The Xcode archive directory does not exist or is inaccessible."
                    => CoreTools.Translate("The Xcode archive directory does not exist or is inaccessible."),
                _ => ex.Message,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn(ex);
            ArchiveDirectoryStatus = CoreTools.Translate("Could not save the Xcode archive directory: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private static void Open() => MainWindow.Instance?.Navigate(PageType.Xcode);
}
