using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class BackupViewModel : ViewModelBase, IDisposable
{
    public event EventHandler? RestartRequired;

    public IReadOnlyList<string> InfoLines { get; } =
    [
        " \u25cf " + CoreTools.Translate("The backup will include the complete list of the installed packages and their installation options. Ignored updates and skipped versions will also be saved."),
        " \u25cf " + CoreTools.Translate("The backup will NOT include any binary file nor any program's saved data."),
        " \u25cf " + CoreTools.Translate("The size of the backup is estimated to be less than 1MB."),
        " \u25cf " + CoreTools.Translate("The backup will be performed after login."),
    ];

    /* ── Local backup ── */
    [ObservableProperty] private bool _isLocalBackupEnabled;
    [ObservableProperty] private string _backupDirectoryLabel = "";

    /* ── Cloud backup ── */
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isLoginButtonEnabled = true;
    [ObservableProperty] private bool _isCloudControlsEnabled;
    [ObservableProperty] private bool _isCloudBackupNowEnabled;
    [ObservableProperty] private string _gitHubUserTitle = "";
    [ObservableProperty] private string _gitHubUserSubtitle = "";
    [ObservableProperty] private IImage? _gitHubAvatarBitmap;

    private readonly GitHubAuthService _authService = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _isLoading;
    private int _isDisposed;
    private long _statusGeneration;

    public BackupViewModel()
    {
        _lifetimeToken = _lifetimeCancellation.Token;
        _isLocalBackupEnabled = CoreSettings.Get(CoreSettings.K.EnablePackageBackup_LOCAL);
        RefreshDirectoryLabel();

        GitHubAuthService.AuthStatusChanged += OnAuthStatusChanged;
        _ = UpdateGitHubLoginStatus();
    }

    private bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    private bool CanApplyStatus(long generation) =>
        !IsDisposed
        && !_lifetimeToken.IsCancellationRequested
        && generation == Volatile.Read(ref _statusGeneration);

    private void OnAuthStatusChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
            _ = UpdateGitHubLoginStatus();
    }

    /* ─────────────── Local backup ─────────────── */

    [RelayCommand]
    private void EnableLocalBackupChanged()
    {
        if (IsDisposed) return;
        IsLocalBackupEnabled = CoreSettings.Get(CoreSettings.K.EnablePackageBackup_LOCAL);
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDirectoryLabel()
    {
        if (IsDisposed) return;
        string dir = CoreSettings.GetValue(CoreSettings.K.ChangeBackupOutputDirectory);
        BackupDirectoryLabel = string.IsNullOrEmpty(dir) ? CoreData.UniGetUI_DefaultBackupDirectory : dir;
    }

    [RelayCommand]
    private async Task PickBackupDirectory(Visual? visual)
    {
        if (IsDisposed || visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
        });
        if (IsDisposed || folders is not [{ } folder]) return;
        var path = folder.TryGetLocalPath();
        if (path is null) return;
        CoreSettings.SetValue(CoreSettings.K.ChangeBackupOutputDirectory, path);
        RefreshDirectoryLabel();
    }

    [RelayCommand]
    private static Task DoLocalBackup(Visual? _) => DoLocalBackupStatic();

    public static async Task DoLocalBackupStatic()
    {
        try
        {
            var packages = InstalledPackagesLoader.Instance?.Packages.ToList()
                ?? [];
            string backupContents = await PackageBundlesPage.CreateBundle(packages);

            string dirName = CoreSettings.GetValue(CoreSettings.K.ChangeBackupOutputDirectory);
            if (string.IsNullOrEmpty(dirName))
                dirName = CoreData.UniGetUI_DefaultBackupDirectory;

            if (!Directory.Exists(dirName))
                Directory.CreateDirectory(dirName);

            string fileName = CoreSettings.GetValue(CoreSettings.K.ChangeBackupFileName);
            if (string.IsNullOrEmpty(fileName))
                fileName = CoreTools.Translate(
                    "{pcName} installed packages",
                    new Dictionary<string, object?> { { "pcName", Environment.MachineName } }
                );

            if (CoreSettings.Get(CoreSettings.K.EnableBackupTimestamping))
                fileName += " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");

            fileName += ".ubundle";

            string filePath = Path.Combine(dirName, fileName);
            await File.WriteAllTextAsync(filePath, backupContents);
            Logger.ImportantInfo("Local backup saved to " + filePath);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while performing a LOCAL backup:");
            Logger.Error(ex);
        }
    }

    /* ─────────────── Cloud backup ─────────────── */

    private async Task UpdateGitHubLoginStatus()
    {
        if (IsDisposed) return;

        long generation = Interlocked.Increment(ref _statusGeneration);
        if (GitHubAuthService.IsAuthenticated())
        {
            try { await GenerateLogoutState(generation); }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                {
                    Logger.Error("An error occurred while attempting to generate settings login UI:");
                    Logger.Error(ex);
                }
                GenerateLoginState(generation);
            }
        }
        else
        {
            GenerateLoginState(generation);
        }

        if (CanApplyStatus(generation))
            UpdateCloudControlsEnabled();
    }

    private void GenerateLoginState(long generation)
    {
        if (!CanApplyStatus(generation)) return;

        IsLoggedIn = false;
        GitHubUserTitle = CoreTools.Translate("Current status: Not logged in");
        GitHubUserSubtitle = CoreTools.Translate("Log in to enable cloud backup");
        SetGitHubAvatarBitmap(null);
    }

    private async Task GenerateLogoutState(long generation)
    {
        using var client = GitHubAuthService.CreateGitHubClient()
            ?? throw new InvalidOperationException("Authenticated but cannot create GitHub client.");
        var user = await client.GetCurrentUserAsync();
        if (!CanApplyStatus(generation)) return;

        IsLoggedIn = true;
        string displayName = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name;
        GitHubUserTitle = CoreTools.Translate("You are logged in as {0} (@{1})", displayName, user.Login);
        GitHubUserSubtitle = CoreTools.Translate("Nice! Backups will be uploaded to a private gist on your account");

        try
        {
            using var http = new HttpClient(CoreTools.GenericHttpClientParameters);
            if (!string.IsNullOrWhiteSpace(user.AvatarUrl))
            {
                var bytes = await http.GetByteArrayAsync(user.AvatarUrl, _lifetimeToken);
                if (!CanApplyStatus(generation)) return;

                using var ms = new MemoryStream(bytes);
                var bitmap = new Bitmap(ms);
                if (CanApplyStatus(generation))
                    SetGitHubAvatarBitmap(bitmap);
                else
                    bitmap.Dispose();
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Logger.Error("Failed to load GitHub avatar:");
                Logger.Error(ex);
            }
        }
    }

    private void SetGitHubAvatarBitmap(IImage? bitmap) => GitHubAvatarBitmap = bitmap;

    private void UpdateCloudControlsEnabled()
    {
        if (IsDisposed) return;

        IsLoginButtonEnabled = !_isLoading;
        IsCloudControlsEnabled = IsLoggedIn && !_isLoading;
        IsCloudBackupNowEnabled = IsLoggedIn && !_isLoading
            && CoreSettings.Get(CoreSettings.K.EnablePackageBackup_CLOUD);
    }

    [RelayCommand]
    private void EnableCloudBackupChanged()
    {
        if (IsDisposed) return;
        RestartRequired?.Invoke(this, EventArgs.Empty);
        UpdateCloudControlsEnabled();
    }

    [RelayCommand]
    private async Task Login()
    {
        if (IsDisposed) return;
        _isLoading = true;
        UpdateCloudControlsEnabled();

        bool success = await _authService.SignInAsync();
        if (!success && !IsDisposed)
            Logger.Error("An error occurred while logging in to GitHub.");

        if (IsDisposed) return;
        _isLoading = false;
        UpdateCloudControlsEnabled();
    }

    [RelayCommand]
    private void Logout()
    {
        if (IsDisposed) return;
        _isLoading = true;
        UpdateCloudControlsEnabled();

        _authService.SignOut();

        _isLoading = false;
        UpdateCloudControlsEnabled();
    }

    [RelayCommand]
    private async Task DoCloudBackup()
    {
        if (IsDisposed) return;
        _isLoading = true;
        UpdateCloudControlsEnabled();
        try { await DoCloudBackupStatic(); }
        finally
        {
            _isLoading = false;
            UpdateCloudControlsEnabled();
        }
    }

    public static async Task DoCloudBackupStatic()
    {
        try
        {
            var packages = InstalledPackagesLoader.Instance?.Packages.ToList() ?? [];
            string bundle = await PackageBundlesPage.CreateBundle(packages);
            await GitHubCloudBackupService.UploadPackageBundleAsync(bundle);
            Logger.ImportantInfo("Cloud backup completed successfully.");
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while performing a CLOUD backup:");
            Logger.Error(ex);
        }
    }

    [RelayCommand]
    private static async Task RestoreFromCloud()
    {
        try
        {
            var backups = await GitHubCloudBackupService.GetAvailableBackupsAsync();
            if (backups.Count == 0)
            {
                Logger.Warn("No cloud backups found for this account.");
                return;
            }

            string? selectedKey = await AskForBackupSelectionAsync(backups);
            if (selectedKey is null) return;

            string contents = await GitHubCloudBackupService.GetBackupContentsAsync(selectedKey);

            if (MainWindow.Instance?.DataContext is MainWindowViewModel vm)
                await vm.LoadCloudBundleAsync(contents);
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while restoring from cloud backup:");
            Logger.Error(ex);
        }
    }

    private static async Task<string?> AskForBackupSelectionAsync(
        IReadOnlyList<GitHubCloudBackupService.CloudBackupEntry> backups)
    {
        if (MainWindow.Instance is not { } owner) return null;

        var combo = new ComboBox
        {
            ItemsSource = backups.Select(b => b.Display).ToList(),
            SelectedIndex = 0,
            Width = 360,
        };

        var dialog = new Window
        {
            Title = CoreTools.Translate("Select backup"),
            Width = 440,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    combo,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = CoreTools.Translate("Cancel") },
                            new Button { Content = CoreTools.Translate("Select backup"), Classes = { "accent" } },
                        }
                    }
                }
            }
        };

        string? result = null;
        var buttons = ((StackPanel)((StackPanel)dialog.Content).Children[1]).Children;
        ((Button)buttons[0]).Click += (_, _) => dialog.Close();
        ((Button)buttons[1]).Click += (_, _) =>
        {
            result = backups[combo.SelectedIndex].Key;
            dialog.Close();
        };

        await dialog.ShowDialog(owner);
        return result;
    }

    [RelayCommand]
    private static void MoreDetails()
    {
        CoreTools.Launch("https://devolutions.net/unigetui");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        GitHubAuthService.AuthStatusChanged -= OnAuthStatusChanged;
        // In-flight HTTP work can still observe this token. Cancelling is sufficient;
        // disposing the source here could race with that work.
        _lifetimeCancellation.Cancel();
    }
}
