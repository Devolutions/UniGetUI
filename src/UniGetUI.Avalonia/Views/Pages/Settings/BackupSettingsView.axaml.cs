using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class BackupSettingsView : UserControl, ISettingsSectionView
{
    private bool _isLoading;
    private bool _isCloudLoading;
    private bool _isCloudLoggedIn;
    private readonly DispatcherTimer _filenameSaveTimer;

    // Checkboxes
    private CheckBox EnableLocalBackupCheckBoxControl => GetControl<CheckBox>("EnableLocalBackupCheckBox");
    private CheckBox EnableCloudBackupCheckBoxControl => GetControl<CheckBox>("EnableCloudBackupCheckBox");
    private CheckBox BackupTimestampingCheckBoxControl => GetControl<CheckBox>("BackupTimestampingCheckBox");

    // Buttons
    private Button LoginCloudButtonControl => GetControl<Button>("LoginCloudButton");
    private Button LogoutCloudButtonControl => GetControl<Button>("LogoutCloudButton");
    private Button ChangeBackupDirButtonControl => GetControl<Button>("ChangeBackupDirButton");
    private Button ResetBackupDirButtonControl => GetControl<Button>("ResetBackupDirButton");
    private Button OpenBackupDirButtonControl => GetControl<Button>("OpenBackupDirButton");
    private Button BackupToCloudButtonControl => GetControl<Button>("BackupToCloudButton");
    private Button RestoreFromCloudButtonControl => GetControl<Button>("RestoreFromCloudButton");
    private Button BackupNowButtonControl => GetControl<Button>("BackupNowButton");

    // TextBox
    private TextBox BackupFilenameTextBoxControl => GetControl<TextBox>("BackupFilenameTextBox");

    // Panels / cards
    private Border RestartNoticeCardControl => GetControl<Border>("RestartNoticeCard");
    private Button RestartAppBtnCtrl => GetControl<Button>("RestartAppButton");
    private Border DirectoryCardControl => GetControl<Border>("DirectoryCard");
    private Border AdvancedCardControl => GetControl<Border>("AdvancedCard");

    // Labels
    private TextBlock LeadTitleText => GetControl<TextBlock>("LeadTitleBlock");
    private TextBlock BulletPackageListText => GetControl<TextBlock>("BulletPackageListBlock");
    private TextBlock BulletNoBinaryText => GetControl<TextBlock>("BulletNoBinaryBlock");
    private TextBlock BulletSmallSizeText => GetControl<TextBlock>("BulletSmallSizeBlock");
    private TextBlock BulletAutoBackupText => GetControl<TextBlock>("BulletAutoBackupBlock");
    private TextBlock RestartTitleText => GetControl<TextBlock>("RestartTitleBlock");
    private TextBlock RestartDescriptionText => GetControl<TextBlock>("RestartDescriptionBlock");
    private TextBlock CloudSectionTitleText => GetControl<TextBlock>("CloudSectionTitleBlock");
    private TextBlock CloudNotAvailableDescriptionText => GetControl<TextBlock>("CloudNotAvailableDescriptionBlock");
    private TextBlock CloudStatusText => GetControl<TextBlock>("CloudStatusBlock");
    private TextBlock LocalSectionTitleText => GetControl<TextBlock>("LocalSectionTitleBlock");
    private TextBlock LocalSectionDescriptionText => GetControl<TextBlock>("LocalSectionDescriptionBlock");
    private TextBlock DirectorySectionTitleText => GetControl<TextBlock>("DirectorySectionTitleBlock");
    private TextBlock DirectoryDescriptionText => GetControl<TextBlock>("DirectoryDescriptionBlock");
    private TextBlock BackupDirCurrentLabelText => GetControl<TextBlock>("BackupDirCurrentLabel");
    private TextBlock AdvancedSectionTitleText => GetControl<TextBlock>("AdvancedSectionTitleBlock");
    private TextBlock FilenameDescriptionText => GetControl<TextBlock>("FilenameDescriptionBlock");
    private TextBlock FilenameHintText => GetControl<TextBlock>("FilenameHintBlock");

    public BackupSettingsView()
    {
        _filenameSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _filenameSaveTimer.Tick += FilenameSaveTimer_OnTick;

        InitializeComponent();

        EnableLocalBackupCheckBoxControl.Click += EnableLocalBackupCheckBox_OnClick;
        EnableCloudBackupCheckBoxControl.Click += EnableCloudBackupCheckBox_OnClick;
        BackupTimestampingCheckBoxControl.Click += BackupTimestampingCheckBox_OnClick;
        ChangeBackupDirButtonControl.Click += ChangeBackupDir_OnClick;
        ResetBackupDirButtonControl.Click += ResetBackupDir_OnClick;
        OpenBackupDirButtonControl.Click += OpenBackupDir_OnClick;
        LoginCloudButtonControl.Click += LoginCloudButton_OnClick;
        LogoutCloudButtonControl.Click += LogoutCloudButton_OnClick;
        BackupToCloudButtonControl.Click += BackupToCloudButton_OnClick;
        RestoreFromCloudButtonControl.Click += RestoreFromCloudButton_OnClick;

        SectionTitle = CoreTools.Translate("Backup and Restore");
        SectionSubtitle = CoreTools.Translate("Automatically save a list of your installed packages on your computer.");
        SectionStatus = CoreTools.Translate("Backup and Restore");

        ApplyLocalizedText();

        if (Design.IsDesignMode)
        {
            LoadPreviewValues();
            return;
        }

        LoadStoredValues();

        // Subscribe after loading to avoid spurious saves at init
        BackupFilenameTextBoxControl.TextChanged += BackupFilenameTextBox_OnTextChanged;
    }

    public string SectionTitle { get; }
    public string SectionSubtitle { get; }
    public string SectionStatus { get; }

    private void ApplyLocalizedText()
    {
        LeadTitleText.Text = CoreTools.Translate("Package backup");
        BulletPackageListText.Text = " \u25cf " + CoreTools.Translate("The backup will include the complete list of the installed packages and their installation options. Ignored updates and skipped versions will also be saved.");
        BulletNoBinaryText.Text = " \u25cf " + CoreTools.Translate("The backup will NOT include any binary file nor any program's saved data.");
        BulletSmallSizeText.Text = " \u25cf " + CoreTools.Translate("The size of the backup is estimated to be less than 1MB.");
        BulletAutoBackupText.Text = " \u25cf " + CoreTools.Translate("The backup will be performed after login.");

        RestartTitleText.Text = CoreTools.Translate("Restart required");
        RestartDescriptionText.Text = CoreTools.Translate("Restart WingetUI to fully apply changes");
        RestartAppBtnCtrl.Content = CoreTools.Translate("Restart UniGetUI");

        CloudSectionTitleText.Text = CoreTools.Translate("Cloud package backup");
        CloudNotAvailableDescriptionText.Text = CoreTools.Translate("Cloud backup uses a private GitHub Gist to store a list of installed packages");
        CloudStatusText.Text = CoreTools.Translate("Current status: Not logged in");
        LoginCloudButtonControl.Content = CoreTools.Translate("Log in");
        LogoutCloudButtonControl.Content = CoreTools.Translate("Log out");
        EnableCloudBackupCheckBoxControl.Content = CoreTools.Translate("Periodically perform a cloud backup of the installed packages");
        BackupToCloudButtonControl.Content = CoreTools.Translate("Backup");
        RestoreFromCloudButtonControl.Content = CoreTools.Translate("Select backup");

        LocalSectionTitleText.Text = CoreTools.Translate("Local package backup");
        LocalSectionDescriptionText.Text = CoreTools.Translate("Automatically save a list of all your installed packages to easily restore them.");
        EnableLocalBackupCheckBoxControl.Content = CoreTools.Translate("Periodically perform a local backup of the installed packages");
        BackupNowButtonControl.Content = CoreTools.Translate("Perform a backup now");

        DirectorySectionTitleText.Text = CoreTools.Translate("Backup location");
        DirectoryDescriptionText.Text = CoreTools.Translate("Change backup output directory");
        ChangeBackupDirButtonControl.Content = CoreTools.Translate("Select");
        ResetBackupDirButtonControl.Content = CoreTools.Translate("Reset");
        OpenBackupDirButtonControl.Content = CoreTools.Translate("Open");

        AdvancedSectionTitleText.Text = CoreTools.Translate("Local backup advanced options");
        FilenameDescriptionText.Text = CoreTools.Translate("Set a custom backup file name");
        BackupFilenameTextBoxControl.Watermark = CoreTools.Translate("Leave empty for default");
        FilenameHintText.Text = CoreTools.Translate("Leave empty for default");
        BackupTimestampingCheckBoxControl.Content = CoreTools.Translate("Add a timestamp to the backup file names");
    }

    private void LoadStoredValues()
    {
        _isLoading = true;

        bool localEnabled = Settings.Get(Settings.K.EnablePackageBackup_LOCAL);
        bool cloudEnabled = Settings.Get(Settings.K.EnablePackageBackup_CLOUD);
        EnableLocalBackupCheckBoxControl.IsChecked = localEnabled;
        EnableCloudBackupCheckBoxControl.IsChecked = cloudEnabled;
        BackupTimestampingCheckBoxControl.IsChecked = Settings.Get(Settings.K.EnableBackupTimestamping);
        BackupFilenameTextBoxControl.Text = Settings.GetValue(Settings.K.ChangeBackupFileName);

        UpdateDirectoryLabel();
        ApplyLocalBackupEnabledState(localEnabled);
        BackupNowButtonControl.IsEnabled = localEnabled;
        UpdateCloudControlsEnabled();

        RestartNoticeCardControl.IsVisible = false;
        _isLoading = false;

        _ = RefreshCloudStatusAsync();
    }

    private void LoadPreviewValues()
    {
        EnableLocalBackupCheckBoxControl.IsChecked = true;
        EnableCloudBackupCheckBoxControl.IsChecked = true;
        BackupTimestampingCheckBoxControl.IsChecked = true;
        BackupFilenameTextBoxControl.Text = CoreTools.Translate("Package backup");
        BackupDirCurrentLabelText.Text = CoreData.UniGetUI_DefaultBackupDirectory;
        CloudStatusText.Text = CoreTools.Translate("You are logged in as {0} (@{1})", "Preview User", "preview-user");
        _isCloudLoggedIn = true;
        ApplyLocalBackupEnabledState(enabled: true);
        UpdateCloudControlsEnabled();
        RestartNoticeCardControl.IsVisible = true;
    }

    private void UpdateDirectoryLabel()
    {
        bool hasCustomDir = Settings.Get(Settings.K.ChangeBackupOutputDirectory);
        if (hasCustomDir)
        {
            string customPath = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
            BackupDirCurrentLabelText.Text = string.IsNullOrWhiteSpace(customPath)
                ? CoreData.UniGetUI_DefaultBackupDirectory
                : customPath;
            ResetBackupDirButtonControl.IsEnabled = true;
        }
        else
        {
            BackupDirCurrentLabelText.Text = CoreData.UniGetUI_DefaultBackupDirectory;
            ResetBackupDirButtonControl.IsEnabled = false;
        }
    }

    private void ApplyLocalBackupEnabledState(bool enabled)
    {
        DirectoryCardControl.IsEnabled = enabled;
        AdvancedCardControl.IsEnabled = enabled;
        BackupNowButtonControl.IsEnabled = enabled;
    }

    private void UpdateCloudControlsEnabled()
    {
        LoginCloudButtonControl.IsEnabled = !_isCloudLoading && !_isCloudLoggedIn;
        LogoutCloudButtonControl.IsEnabled = !_isCloudLoading && _isCloudLoggedIn;
        EnableCloudBackupCheckBoxControl.IsEnabled = !_isCloudLoading && _isCloudLoggedIn;

        bool cloudEnabled = _isCloudLoggedIn && EnableCloudBackupCheckBoxControl.IsChecked == true;
        BackupToCloudButtonControl.IsEnabled = !_isCloudLoading && cloudEnabled;
        RestoreFromCloudButtonControl.IsEnabled = !_isCloudLoading && _isCloudLoggedIn;
    }

    private async Task RefreshCloudStatusAsync()
    {
        if (_isCloudLoading)
            return;

        var client = GitHubCloudBackupService.CreateGitHubClient();
        if (client is null)
        {
            _isCloudLoggedIn = false;
            CloudStatusText.Text = CoreTools.Translate("Current status: Not logged in");
            UpdateCloudControlsEnabled();
            return;
        }

        try
        {
            var (userLogin, userName) = await GitHubCloudBackupService.GetCurrentUserAsync();
            _isCloudLoggedIn = true;

            Settings.SetValue(Settings.K.GitHubUserLogin, userLogin);

            CloudStatusText.Text = CoreTools.Translate(
                "You are logged in as {0} (@{1})",
                userName,
                userLogin
            );
        }
        catch (Exception ex)
        {
            Logger.Warn("Cloud backup token validation failed.");
            Logger.Warn(ex);
            _isCloudLoggedIn = false;
            SecureGHTokenManager.DeleteToken();
            Settings.SetValue(Settings.K.GitHubUserLogin, string.Empty);
            CloudStatusText.Text = CoreTools.Translate("Current status: Not logged in");
        }

        UpdateCloudControlsEnabled();
    }

    // ── Click handlers ────────────────────────────────────────────────────

    private void EnableLocalBackupCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        bool enabled = EnableLocalBackupCheckBoxControl.IsChecked == true;
        Settings.Set(Settings.K.EnablePackageBackup_LOCAL, enabled);
        ApplyLocalBackupEnabledState(enabled);
        ShowRestartNotice();
    }

    private void EnableCloudBackupCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.EnablePackageBackup_CLOUD, EnableCloudBackupCheckBoxControl.IsChecked == true);
        ShowRestartNotice();
        UpdateCloudControlsEnabled();
    }

    private void BackupTimestampingCheckBox_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        Settings.Set(Settings.K.EnableBackupTimestamping, BackupTimestampingCheckBoxControl.IsChecked == true);
    }

    private async void ChangeBackupDir_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null) return;

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = CoreTools.Translate("Select a folder"),
            AllowMultiple = false,
        });

        if (result.Count > 0)
        {
            string? path = result[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, path);
                Settings.Set(Settings.K.ChangeBackupOutputDirectory, true);
                UpdateDirectoryLabel();
            }
        }
    }

    private void ResetBackupDir_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.Set(Settings.K.ChangeBackupOutputDirectory, false);
        UpdateDirectoryLabel();
    }

    private void OpenBackupDir_OnClick(object? sender, RoutedEventArgs e)
    {
        string directory = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
        if (string.IsNullOrWhiteSpace(directory))
            directory = CoreData.UniGetUI_DefaultBackupDirectory;

        directory = directory.Replace("/", "\\");
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    // ── Filename debounced save ───────────────────────────────────────────

    private void BackupFilenameTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isLoading) return;
        _filenameSaveTimer.Stop();
        _filenameSaveTimer.Start();
    }

    private void FilenameSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _filenameSaveTimer.Stop();
        Settings.SetValue(Settings.K.ChangeBackupFileName, BackupFilenameTextBoxControl.Text ?? string.Empty);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ShowRestartNotice()
    {
        RestartNoticeCardControl.IsVisible = true;
    }

    private async void BackupNowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        BackupNowButtonControl.IsEnabled = false;
        try
        {
            var packages = InstalledPackagesLoader.Instance.Packages;
            string contents = await BundlesPageView.CreateBundleStringAsync(packages);

            string dirName = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
            if (string.IsNullOrWhiteSpace(dirName))
                dirName = CoreData.UniGetUI_DefaultBackupDirectory;

            if (!Directory.Exists(dirName))
                Directory.CreateDirectory(dirName);

            string fileName = Settings.GetValue(Settings.K.ChangeBackupFileName);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = CoreTools.Translate("{pcName} installed packages",
                    new Dictionary<string, object?> { { "pcName", Environment.MachineName } });

            if (Settings.Get(Settings.K.EnableBackupTimestamping))
                fileName += " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");

            fileName += ".ubundle";
            string filePath = Path.Combine(dirName, fileName);
            await File.WriteAllTextAsync(filePath, contents);
            Logger.ImportantInfo("Backup saved to " + filePath);
            CoreTools.Launch(dirName);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred during manual backup");
            Logger.Error(ex);
        }
        finally
        {
            BackupNowButtonControl.IsEnabled = Settings.Get(Settings.K.EnablePackageBackup_LOCAL);
        }
    }

    private async void LoginCloudButton_OnClick(object? sender, RoutedEventArgs e)
    {
        string? token = await PromptForTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            return;

        _isCloudLoading = true;
        UpdateCloudControlsEnabled();

        try
        {
            SecureGHTokenManager.StoreToken(token);

            var (userLogin, _) = await GitHubCloudBackupService.GetCurrentUserAsync();
            if (string.IsNullOrWhiteSpace(userLogin))
                throw new InvalidOperationException("The GitHub token did not return a valid user.");

            Settings.SetValue(Settings.K.GitHubUserLogin, userLogin);
            await ShowInfoDialogAsync(
                CoreTools.Translate("Done!"),
                CoreTools.Translate("You are logged in as {0} (@{1})", userLogin, userLogin)
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Cloud backup sign-in failed");
            Logger.Error(ex);
            SecureGHTokenManager.DeleteToken();
            Settings.SetValue(Settings.K.GitHubUserLogin, string.Empty);
            await ShowInfoDialogAsync(
                CoreTools.Translate("Backup Failed"),
                CoreTools.Translate("Could not back up packages to GitHub Gist: ") + ex.Message
            );
        }
        finally
        {
            _isCloudLoading = false;
            await RefreshCloudStatusAsync();
        }
    }

    private async void LogoutCloudButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SecureGHTokenManager.DeleteToken();
        Settings.SetValue(Settings.K.GitHubUserLogin, string.Empty);
        _isCloudLoggedIn = false;
        await RefreshCloudStatusAsync();
    }

    private async void BackupToCloudButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isCloudLoading = true;
        UpdateCloudControlsEnabled();

        try
        {
            string contents = await BundlesPageView.CreateBundleStringAsync(InstalledPackagesLoader.Instance.Packages);
            await GitHubCloudBackupService.UploadPackageBundleAsync(contents);
            await ShowInfoDialogAsync(
                CoreTools.Translate("Backup Successful"),
                CoreTools.Translate("The cloud backup completed successfully.")
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Could not back up packages to GitHub Gist");
            Logger.Error(ex);
            await ShowInfoDialogAsync(
                CoreTools.Translate("Backup Failed"),
                CoreTools.Translate("Could not back up packages to GitHub Gist: ") + ex.Message
            );
        }
        finally
        {
            _isCloudLoading = false;
            UpdateCloudControlsEnabled();
        }
    }

    private async void RestoreFromCloudButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _isCloudLoading = true;
        UpdateCloudControlsEnabled();

        try
        {
            var backups = await GitHubCloudBackupService.GetAvailableBackupsAsync();
            if (backups.Count == 0)
            {
                return;
            }

            string? selectedKey = await PromptForBackupSelectionAsync(backups);
            if (string.IsNullOrWhiteSpace(selectedKey))
                return;

            string backupContents = await GitHubCloudBackupService.GetBackupContentsAsync(selectedKey);

            string filePath = Path.Combine(
                Path.GetTempPath(),
                "UniGetUI-cloud-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".ubundle"
            );
            await File.WriteAllTextAsync(filePath, backupContents);

            var shell = this.FindAncestorOfType<MainShellView>();
            if (shell is null)
            {
                await ShowInfoDialogAsync(
                    CoreTools.Translate("Backup Failed"),
                    CoreTools.Translate("An error occurred while loading a backup: ") + "Could not access shell navigation"
                );
                return;
            }

            await shell.OpenBundleFromFileAsync(filePath);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while restoring a cloud backup");
            Logger.Error(ex);
            await ShowInfoDialogAsync(
                CoreTools.Translate("Backup Failed"),
                CoreTools.Translate("An error occurred while loading a backup: ") + ex.Message
            );
        }
        finally
        {
            _isCloudLoading = false;
            UpdateCloudControlsEnabled();
        }
    }

    private async Task<string?> PromptForTokenAsync()
    {
        if (VisualRoot is not Window owner)
            return null;

        string? token = null;
        var tokenBox = new TextBox
        {
            Watermark = CoreTools.Translate("Leave empty for default"),
            Width = 460,
        };

        var dialog = new Window
        {
            Width = 560,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Cloud package backup"),
        };

        var saveBtn = new Button { Content = CoreTools.Translate("Log in"), MinWidth = 100 };
        var cancelBtn = new Button { Content = CoreTools.Translate("Cancel"), MinWidth = 100 };

        saveBtn.Click += (_, _) =>
        {
            token = tokenBox.Text?.Trim();
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = CoreTools.Translate("Log in with GitHub to enable cloud package backup."),
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                tokenBox,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancelBtn, saveBtn },
                },
            },
        };

        await dialog.ShowDialog(owner);

        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private async Task<string?> PromptForBackupSelectionAsync(IReadOnlyList<GitHubCloudBackupService.CloudBackupEntry> backups)
    {
        if (VisualRoot is not Window owner)
            return null;

        string? selectedKey = null;
        var selector = new ComboBox
        {
            Width = 420,
            ItemsSource = backups.Select(b => b.Display).ToArray(),
            SelectedIndex = backups.Count > 0 ? 0 : -1,
        };

        var dialog = new Window
        {
            Width = 520,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Select backup"),
        };

        var openBtn = new Button { Content = CoreTools.Translate("Open"), MinWidth = 100 };
        var cancelBtn = new Button { Content = CoreTools.Translate("Cancel"), MinWidth = 100 };

        openBtn.Click += (_, _) =>
        {
            if (selector.SelectedIndex >= 0 && selector.SelectedIndex < backups.Count)
                selectedKey = backups[selector.SelectedIndex].Key;
            dialog.Close();
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = CoreTools.Translate("Select the backup you want to open. Later, you will be able to review which packages you want to install."),
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                selector,
                new StackPanel
                {
                    Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancelBtn, openBtn },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return selectedKey;
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        if (VisualRoot is not Window owner)
            return;

        var dialog = new Window
        {
            Width = 520,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };

        var okBtn = new Button
        {
            Content = CoreTools.Translate("OK"),
            MinWidth = 100,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
        };
        okBtn.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                },
                okBtn,
            },
        };

        await dialog.ShowDialog(owner);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }

    private void RestartAppButton_OnClick(object? sender, RoutedEventArgs e)
        => MainWindow.KillAndRestart();
}
