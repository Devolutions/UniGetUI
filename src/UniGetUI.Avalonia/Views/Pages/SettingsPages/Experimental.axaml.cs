using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Experimental : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Experimental settings and developer options");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public void ShowRestartBanner(object? sender, EventArgs e) =>
        RestartRequired?.Invoke(this, e);

    public Experimental()
    {
        DataContext = new ExperimentalViewModel();
        InitializeComponent();

        ShowVersionNumberOnTitlebar.SettingName = Settings.K.ShowVersionNumberOnTitlebar;
        ShowVersionNumberOnTitlebar.Text = CoreTools.Translate("Show UniGetUI's version and build number on the titlebar.");
        ShowVersionNumberOnTitlebar.StateChanged += ShowRestartBanner;

        DisableWidgetsApi.SettingName = Settings.K.DisableApi;
        DisableWidgetsApi.Text = CoreTools.Translate("Enable background api (WingetUI Widgets and Sharing, port 7058)");
        DisableWidgetsApi.StateChanged += ShowRestartBanner;

        DisableWaitForInternetConnection.SettingName = Settings.K.DisableWaitForInternetConnection;
        DisableWaitForInternetConnection.Text = CoreTools.Translate("Wait for the device to be connected to the internet before attempting to do tasks that require internet connectivity.");
        DisableWaitForInternetConnection.StateChanged += ShowRestartBanner;

        DisableTimeoutOnPackageListingTasks.SettingName = Settings.K.DisableTimeoutOnPackageListingTasks;
        DisableTimeoutOnPackageListingTasks.ForceInversion = true;
        DisableTimeoutOnPackageListingTasks.Text = CoreTools.Translate("Disable the 1-minute timeout for package-related operations");

        UseUserGSudoToggle.SettingName = SecureSettings.K.ForceUserGSudo;
        UseUserGSudoToggle.Text = CoreTools.Translate("Use installed GSudo instead of UniGetUI Elevator");
        UseUserGSudoToggle.StateChanged += ShowRestartBanner;

        DisableDownloadingNewTranslations.SettingName = Settings.K.DisableLangAutoUpdater;
        DisableDownloadingNewTranslations.Text = CoreTools.Translate("Download updated language files from GitHub automatically");
        DisableDownloadingNewTranslations.StateChanged += ShowRestartBanner;

        IconDatabaseURLCard.SettingName = Settings.K.IconDataBaseURL;
        IconDatabaseURLCard.HelpUrl = new Uri("https://www.marticliment.com/unigetui/help/icons-and-screenshots#custom-source");
        IconDatabaseURLCard.Placeholder = CoreTools.Translate("Leave empty for default");
        IconDatabaseURLCard.Text = CoreTools.Translate("Use a custom icon and screenshot database URL");
        IconDatabaseURLCard.ValueChanged += ShowRestartBanner;

        DisableDMWThreadOptimizations.SettingName = Settings.K.DisableDMWThreadOptimizations;
        DisableDMWThreadOptimizations.Text = CoreTools.Translate("Enable background CPU Usage optimizations (see Pull Request #3278)");

        DisableIntegrityChecks.SettingName = Settings.K.DisableIntegrityChecks;
        DisableIntegrityChecks.Text = CoreTools.Translate("Perform integrity checks at startup");

        InstallInstalledPackagesBundlesPage.SettingName = Settings.K.InstallInstalledPackagesBundlesPage;
        InstallInstalledPackagesBundlesPage.Text = CoreTools.Translate("When batch installing packages from a bundle, install also packages that are already installed");
    }
}
