using System.Data;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.SoftwarePages;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.Pages.DialogPages;
using UniGetUI.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace UniGetUI.Pages.SettingsPages.GeneralPages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Backup : Page, ISettingsPage
    {
        private readonly GitHubAuthService _authService;
        private readonly GitHubBackupService _backupService;
        private bool _isLoggedIn;
        private bool _isLoading;

        public Backup()
        {
            this.InitializeComponent();

            _authService = new GitHubAuthService();
            _backupService = new GitHubBackupService(_authService);

            EnablePackageBackupUI(Settings.Get(Settings.K.EnablePackageBackup_LOCAL));
            ResetBackupDirectory.Content = CoreTools.Translate("Reset");
            OpenBackupDirectory.Content = CoreTools.Translate("Open");

            GitHubAuthService.AuthStatusChanged += (_, _) => _ = UpdateGitHubLoginStatus();
            EnablePackageBackupCheckBox_CLOUD.StateChanged +=
                EnablePackageBackupCheckBox_CLOUD_StateChanged;
            _ = UpdateGitHubLoginStatus();
        }

        public bool CanGoBack => true;

        public string ShortTitle => CoreTools.Translate("Backup and Restore");

        public event EventHandler? RestartRequired;
        public event EventHandler<Type>? NavigationRequested
        {
            add { }
            remove { }
        }

        public void ShowRestartBanner(object? sender, EventArgs e) =>
            RestartRequired?.Invoke(this, e);

        private void ChangeBackupDirectory_Click(object sender, EventArgs e)
        {
            ExternalLibraries.Pickers.FolderPicker openPicker = new(
                MainApp.Instance.MainWindow.GetWindowHandle()
            );
            string folder = openPicker.Show();
            if (folder != string.Empty)
            {
                Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, folder);
                BackupDirectoryLabel.Text = folder;
                ResetBackupDirectory.IsEnabled = true;
            }
        }

        public void EnablePackageBackupUI(bool enabled)
        {
            EnableBackupTimestampingCheckBox.IsEnabled = enabled;
            ChangeBackupFileNameTextBox.IsEnabled = enabled;
            ChangeBackupDirectory.IsEnabled = enabled;
            BackupNowButton_LOCAL.IsEnabled = enabled;

            if (enabled)
            {
                if (!Settings.Get(Settings.K.ChangeBackupOutputDirectory))
                {
                    BackupDirectoryLabel.Text = CoreData.UniGetUI_DefaultBackupDirectory;
                    ResetBackupDirectory.IsEnabled = false;
                }
                else
                {
                    BackupDirectoryLabel.Text = Settings.GetValue(
                        Settings.K.ChangeBackupOutputDirectory
                    );
                    ResetBackupDirectory.IsEnabled = true;
                }
            }
        }

        private void ResetBackupPath_Click(object sender, RoutedEventArgs e)
        {
            BackupDirectoryLabel.Text = CoreData.UniGetUI_DefaultBackupDirectory;
            Settings.Set(Settings.K.ChangeBackupOutputDirectory, false);
            ResetBackupDirectory.IsEnabled = false;
        }

        private void OpenBackupPath_Click(object sender, RoutedEventArgs e)
        {
            string directory = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
            if (directory == "")
                directory = CoreData.UniGetUI_DefaultBackupDirectory;

            directory = directory.Replace("/", "\\");
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            CoreTools.Launch(directory);
        }

        private void DoBackup_LOCAL_Click(object sender, EventArgs e) =>
            _ = _doBackup_LOCAL_Click();

        private static async Task _doBackup_LOCAL_Click()
        {
            int loadingId = DialogHelper.ShowLoadingDialog(
                CoreTools.Translate("Performing backup, please wait...")
            );
            await InstalledPackagesPage.BackupPackages_LOCAL();
            DialogHelper.HideLoadingDialog(loadingId);
        }

        /*
         *
         *       BEGIN CLOUD BACKUP METHODS
         *
         */
        private async Task UpdateGitHubLoginStatus()
        {
            GitHubAuthService authService = new();
            if (authService.IsAuthenticated())
            {
                try
                {
                    await GenerateLogoutUI(authService);
                }
                catch (Exception ex)
                {
                    Logger.Error(
                        "An error occurred while attempting to generate settings login UI: "
                    );
                    Logger.Error(ex);
                    GenerateLoginUI();
                }
            }
            else
            {
                GenerateLoginUI();
            }
            UpdateCloudControlsEnabled();
        }

        private void GenerateLoginUI()
        {
            _isLoggedIn = false;
            LogInButton.Visibility = Visibility.Visible;
            LogOutButton.Visibility = Visibility.Collapsed;
            GitHubUserTitle.Text = CoreTools.Translate("Current status: Not logged in");
            GitHubUserSubtitle.Text = CoreTools.Translate("Log in to enable cloud backup");
            GitHubImage.ProfilePicture = null;
        }

        private async Task GenerateLogoutUI(GitHubAuthService authService)
        {
            var client = authService.CreateGitHubClient();
            if (client is null)
                throw new AuthenticationException(
                    "How can it be authenticated and fail to create a client?"
                );
            var user = await client.User.Current();

            _isLoggedIn = true;
            LogInButton.Visibility = Visibility.Collapsed;
            LogOutButton.Visibility = Visibility.Visible;
            GitHubUserTitle.Text = CoreTools.Translate(
                "You are logged in as {0} (@{1})",
                user.Name,
                user.Login
            );
            GitHubUserSubtitle.Text = CoreTools.Translate(
                "Nice! Backups will be uploaded to a private gist on your account"
            );
            GitHubImage.Initials = "";
            GitHubImage.ProfilePicture = new BitmapImage(new Uri(user.AvatarUrl));
        }

        private void UpdateCloudControlsEnabled()
        {
            LogInButton.IsEnabled = !_isLoading;
            LogOutButton.IsEnabled = !_isLoading;
            if (_isLoggedIn && !_isLoading)
            {
                EnablePackageBackupCheckBox_CLOUD.IsEnabled = true;
                RestorePackagesFromGitHubButton.IsEnabled = true;
                BackupNowButton_Cloud.IsEnabled = Settings.Get(
                    Settings.K.EnablePackageBackup_CLOUD
                );
            }
            else
            {
                EnablePackageBackupCheckBox_CLOUD.IsEnabled = false;
                BackupNowButton_Cloud.IsEnabled = false;
                RestorePackagesFromGitHubButton.IsEnabled = false;
            }
        }

        private void LoginWithGitHubButton_Click(object sender, RoutedEventArgs e) =>
            _ = _loginWithGitHubButton_Click();

        private async Task _loginWithGitHubButton_Click()
        {
            _isLoading = true;
            UpdateCloudControlsEnabled();

            bool success = await _authService.SignInAsync();
            if (!success)
            {
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Failed"),
                    CoreTools.Translate("An error occurred while logging in: ")
                );
            }
            _isLoading = false;
            UpdateCloudControlsEnabled();
        }

        private void LogoutGitHubButton_Click(object sender, RoutedEventArgs e)
        {
            _isLoading = true;
            UpdateCloudControlsEnabled();

            _authService.SignOut();

            _isLoading = false;
            UpdateCloudControlsEnabled();
        }

        private void RestoreFromGitHubButton_Click(object sender, EventArgs e) =>
            _ = _restoreFromGitHubButton_Click();

        private async Task _restoreFromGitHubButton_Click()
        {
            RestorePackagesFromGitHubButton.IsEnabled = false;
            try
            {
                int loadingId = DialogHelper.ShowLoadingDialog(
                    CoreTools.Translate("Fetching available backups...")
                );
                var availableBackups = await _backupService.GetAvailableBackups();
                DialogHelper.HideLoadingDialog(loadingId);

                var selectedBackup = await DialogHelper.AskForBackupSelection(availableBackups);
                if (selectedBackup is null)
                {
                    RestorePackagesFromGitHubButton.IsEnabled = true;
                    return;
                }
                selectedBackup = selectedBackup.Split(' ')[0];

                loadingId = DialogHelper.ShowLoadingDialog(
                    CoreTools.Translate("Downloading backup...")
                );
                var backupContents = await _backupService.GetBackupContents(selectedBackup);
                // DialogHelper.HideLoadingDialog(loadingId);
                await Task.Delay(500); // Prevent race conditions with dialogs

                if (backupContents is null)
                    throw new DataException(
                        $"The backupContents for backup {selectedBackup} returned null"
                    );

                Logger.Info("Successfully loaded package bundle from GitHub Gist.");
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Done!"),
                    CoreTools.Translate("The cloud backup has been loaded successfully.")
                );

                MainApp.Instance.MainWindow.NavigationPage.LoadBundleFromString(
                    backupContents,
                    BundleFormatType.UBUNDLE,
                    $"GitHub Gist {selectedBackup}",
                    loadingId
                );
            }
            catch (Exception ex)
            {
                Logger.Error("An error occurred while loading a backup:");
                Logger.Error(ex);

                DialogHelper.HideAllLoadingDialogs();
                var errorDialog = DialogHelper.DialogFactory.Create();
                errorDialog.Title = CoreTools.Translate("An error occurred");
                errorDialog.Content =
                    CoreTools.Translate("An error occurred while loading a backup: ") + ex.Message;
                errorDialog.PrimaryButtonText = CoreTools.Translate("OK");
                errorDialog.DefaultButton = ContentDialogButton.Primary;
                await DialogHelper.ShowDialogAsync(errorDialog);
            }
        }

        private void BackupToGitHubButton_Click(object sender, EventArgs e) =>
            _ = _backupToGitHubButton_Click();

        private async Task _backupToGitHubButton_Click()
        {
            int loadingId = DialogHelper.ShowLoadingDialog(
                CoreTools.Translate("Backing up packages to GitHub Gist...")
            );

            var packagesContent = await InstalledPackagesPage.GenerateBackupContents();

            try
            {
                await _backupService.UploadPackageBundle(packagesContent);
                DialogHelper.HideLoadingDialog(loadingId);
                Logger.Info("Successfully backed up packages to GitHub Gist.");
                DialogHelper.ShowDismissableBalloon(
                    CoreTools.Translate("Backup Successful"),
                    CoreTools.Translate("The cloud backup completed successfully.")
                );
            }
            catch (Exception ex)
            {
                DialogHelper.HideLoadingDialog(loadingId);

                Logger.Error("An error occurred while uploading the backup:");
                Logger.Error(ex);

                var dialog = DialogHelper.DialogFactory.Create();
                dialog.Title = CoreTools.Translate("Backup Failed");
                dialog.Content =
                    CoreTools.Translate("Could not back up packages to GitHub Gist: ") + ex.Message;
                dialog.PrimaryButtonText = CoreTools.Translate("OK");
                dialog.DefaultButton = ContentDialogButton.Primary;
                await DialogHelper.ShowDialogAsync(dialog);
            }
        }

        private void EnablePackageBackupCheckBox_CLOUD_StateChanged(object? sender, EventArgs e)
        {
            ShowRestartBanner(sender, e);
            UpdateCloudControlsEnabled();
        }

        private void MoreInfoBtn_OnClick(object sender, RoutedEventArgs e)
        {
            CoreTools.Launch("https://devolutions.net/unigetui");
        }
    }
}
