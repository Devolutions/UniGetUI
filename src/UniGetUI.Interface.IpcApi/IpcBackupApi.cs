using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface;

public sealed class IpcGitHubAuthInfo
{
    public bool ClientConfigured { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? Login { get; set; }
    public bool DeviceFlowPending { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? PollIntervalSeconds { get; set; }
}

public sealed class IpcBackupStatus
{
    public bool LocalBackupEnabled { get; set; }
    public bool CloudBackupEnabled { get; set; }
    public string BackupDirectory { get; set; } = "";
    public string? CustomBackupDirectory { get; set; }
    public string BackupFileName { get; set; } = "";
    public bool TimestampingEnabled { get; set; }
    public string CurrentMachineBackupKey { get; set; } = "";
    public IpcGitHubAuthInfo Auth { get; set; } = new();
}

public class IpcBackupCommandResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string? Message { get; set; }
}

public sealed class IpcLocalBackupResult : IpcBackupCommandResult
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public int PackageCount { get; set; }
}

public sealed class IpcCloudBackupEntry
{
    public string Key { get; set; } = "";
    public string Display { get; set; } = "";
    public bool IsCurrentMachine { get; set; }
}

public sealed class IpcCloudBackupUploadResult : IpcBackupCommandResult
{
    public string Key { get; set; } = "";
    public int PackageCount { get; set; }
}

public sealed class IpcCloudBackupRequest
{
    public string Key { get; set; } = "";
    public bool Append { get; set; }
}

public sealed class IpcCloudBackupContentResult : IpcBackupCommandResult
{
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class IpcCloudBackupRestoreResult : IpcBackupCommandResult
{
    public string Key { get; set; } = "";
    public double SchemaVersion { get; set; }
    public IpcBundleInfo Bundle { get; set; } = new();
    public IReadOnlyList<IpcBundleSecurityEntry> SecurityReport { get; set; } = [];
}

public sealed class IpcGitHubDeviceFlowRequest
{
    public bool LaunchBrowser { get; set; }
}

public sealed class IpcGitHubAuthResult : IpcBackupCommandResult
{
    public IpcGitHubAuthInfo Auth { get; set; } = new();
}

public static class IpcBackupApi
{
    private const string MissingClientId = "CLIENT_ID_UNSET";
    private const string GistDescriptionEndingKey = "@[UNIGETUI_BACKUP_V1]";
    private const string PackageBackupStartingKey = "@[PACKAGES]";
    private const string GistDescription =
        "UniGetUI package backups - DO NOT RENAME OR MODIFY " + GistDescriptionEndingKey;
    private const string ReadMeContents =
        "This special Gist is used by UniGetUI to store your package backups.\n"
        + "Please DO NOT EDIT the contents or the description of this gist, or unexpected behaviours may occur.\n"
        + "Learn more about UniGetUI at https://github.com/Devolutions/UniGetUI\n";

    private static readonly object GitHubAuthLock = new();
    private static PendingGitHubDeviceFlow? _pendingGitHubDeviceFlow;

    private sealed class PendingGitHubDeviceFlow
    {
        public required OauthDeviceFlowResponse DeviceFlow { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
    }

    public static async Task<IpcBackupStatus> GetStatusAsync()
    {
        string? customBackupDirectory = Settings.Get(Settings.K.ChangeBackupOutputDirectory)
            ? Settings.GetValue(Settings.K.ChangeBackupOutputDirectory)
            : null;
        string backupFileName = Settings.GetValue(Settings.K.ChangeBackupFileName);
        if (string.IsNullOrWhiteSpace(backupFileName))
        {
            backupFileName = CoreTools.Translate(
                "{pcName} installed packages",
                new Dictionary<string, object?> { ["pcName"] = Environment.MachineName }
            );
        }

        return new IpcBackupStatus
        {
            LocalBackupEnabled = Settings.Get(Settings.K.EnablePackageBackup_LOCAL),
            CloudBackupEnabled = Settings.Get(Settings.K.EnablePackageBackup_CLOUD),
            BackupDirectory = ResolveBackupDirectory(),
            CustomBackupDirectory = string.IsNullOrWhiteSpace(customBackupDirectory)
                ? null
                : customBackupDirectory,
            BackupFileName = backupFileName,
            TimestampingEnabled = Settings.Get(Settings.K.EnableBackupTimestamping),
            CurrentMachineBackupKey = BuildGistFileKey().Split(' ')[^1],
            Auth = await GetGitHubAuthInfoAsync(),
        };
    }

    public static async Task<IpcLocalBackupResult> CreateLocalBackupAsync()
    {
        var packages = GetInstalledPackagesForBackup();
        string fileName = BuildBackupFileName();
        string outputDirectory = ResolveBackupDirectory();
        Directory.CreateDirectory(outputDirectory);

        string filePath = Path.Combine(outputDirectory, fileName);
        string content = await IpcBundleApi.CreateBundleAsync(packages);
        await File.WriteAllTextAsync(filePath, content);

        Logger.ImportantInfo("Local backup saved to " + filePath);
        return new IpcLocalBackupResult
        {
            Status = "success",
            Command = "create-local-backup",
            Path = filePath,
            FileName = fileName,
            PackageCount = packages.Count,
        };
    }

    public static async Task<IpcGitHubAuthResult> StartGitHubDeviceFlowAsync(
        IpcGitHubDeviceFlowRequest? request = null
    )
    {
        request ??= new IpcGitHubDeviceFlowRequest();
        EnsureGitHubClientConfigured();

        var client = CreateAnonymousGitHubClient();
        var deviceFlow = await client.Oauth.InitiateDeviceFlow(
            new OauthDeviceFlowRequest(Secrets.GetGitHubClientId())
            {
                Scopes = { "read:user", "gist" },
            },
            CancellationToken.None
        );

        lock (GitHubAuthLock)
        {
            _pendingGitHubDeviceFlow = new PendingGitHubDeviceFlow
            {
                DeviceFlow = deviceFlow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(deviceFlow.ExpiresIn),
            };
        }

        if (request.LaunchBrowser)
        {
            CoreTools.Launch(deviceFlow.VerificationUri);
        }

        return new IpcGitHubAuthResult
        {
            Status = "success",
            Command = "start-github-sign-in",
            Message = request.LaunchBrowser
                ? "GitHub device flow started and the verification page was opened."
                : "GitHub device flow started.",
            Auth = await GetGitHubAuthInfoAsync(),
        };
    }

    public static async Task<IpcGitHubAuthResult> CompleteGitHubDeviceFlowAsync()
    {
        EnsureGitHubClientConfigured();

        PendingGitHubDeviceFlow pending = GetPendingGitHubDeviceFlow();
        if (DateTimeOffset.UtcNow >= pending.ExpiresAtUtc)
        {
            ClearPendingGitHubDeviceFlow();
            throw new InvalidOperationException(
                "The pending GitHub device flow has expired. Start sign-in again."
            );
        }

        try
        {
            var client = CreateAnonymousGitHubClient();
            var token = await client.Oauth.CreateAccessTokenForDeviceFlow(
                Secrets.GetGitHubClientId(),
                pending.DeviceFlow,
                CancellationToken.None
            );

            if (string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new InvalidOperationException("GitHub did not return an access token.");
            }

            SecureGHTokenManager.StoreToken(token.AccessToken);
            var userClient = CreateAuthenticatedGitHubClient(token.AccessToken);
            var user = await userClient.User.Current();
            Settings.SetValue(Settings.K.GitHubUserLogin, user.Login ?? string.Empty);
            ClearPendingGitHubDeviceFlow();

            return new IpcGitHubAuthResult
            {
                Status = "success",
                Command = "complete-github-sign-in",
                Message = string.IsNullOrWhiteSpace(user.Login)
                    ? "GitHub sign-in completed."
                    : $"GitHub sign-in completed for {user.Login}.",
                Auth = await GetGitHubAuthInfoAsync(),
            };
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while completing GitHub device flow sign-in:");
            Logger.Error(ex);
            throw new InvalidOperationException(
                "GitHub sign-in did not complete successfully. Finish the device authorization and try again."
            );
        }
    }

    public static async Task<IpcGitHubAuthResult> SignOutGitHubAsync()
    {
        Settings.SetValue(Settings.K.GitHubUserLogin, "");
        SecureGHTokenManager.DeleteToken();
        ClearPendingGitHubDeviceFlow();

        return new IpcGitHubAuthResult
        {
            Status = "success",
            Command = "sign-out-github",
            Message = "GitHub sign-out complete.",
            Auth = await GetGitHubAuthInfoAsync(),
        };
    }

    public static async Task<IReadOnlyList<IpcCloudBackupEntry>> ListCloudBackupsAsync()
    {
        var (client, user) = await GetAuthenticatedGitHubContextAsync();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: false);
        if (backupGist is null)
        {
            return [];
        }

        string currentMachineKey = BuildGistFileKey().Split(' ')[^1];
        return backupGist.Files
            .Where(file => file.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal))
            .Select(file => new IpcCloudBackupEntry
            {
                Key = file.Key.Split(' ')[^1],
                Display = file.Key.Split(' ')[^1] + " (" + CoreTools.FormatAsSize(file.Value.Size) + ")",
                IsCurrentMachine = file.Key.Split(' ')[^1].Equals(
                    currentMachineKey,
                    StringComparison.OrdinalIgnoreCase
                ),
            })
            .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<IpcCloudBackupUploadResult> CreateCloudBackupAsync()
    {
        var packages = GetInstalledPackagesForBackup();
        string bundleContents = await IpcBundleApi.CreateBundleAsync(packages);
        var (client, user) = await GetAuthenticatedGitHubContextAsync();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: true)
            ?? throw new InvalidOperationException("The GitHub backup gist could not be created.");

        string fileKey = BuildGistFileKey();
        var update = new GistUpdate { Description = GistDescription };
        if (backupGist.Files.ContainsKey(fileKey))
        {
            update.Files[fileKey] = new GistFileUpdate { Content = bundleContents };
        }
        else
        {
            update.Files.Add(fileKey, new GistFileUpdate { Content = bundleContents });
        }

        await client.Gist.Edit(backupGist.Id, update);
        return new IpcCloudBackupUploadResult
        {
            Status = "success",
            Command = "create-cloud-backup",
            Key = fileKey.Split(' ')[^1],
            PackageCount = packages.Count,
        };
    }

    public static async Task<IpcCloudBackupContentResult> DownloadCloudBackupAsync(
        IpcCloudBackupRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = ValidateBackupKey(request.Key);
        string content = await GetCloudBackupContentsAsync(key);
        return new IpcCloudBackupContentResult
        {
            Status = "success",
            Command = "download-cloud-backup",
            Key = key,
            Content = content,
        };
    }

    public static async Task<IpcCloudBackupRestoreResult> RestoreCloudBackupAsync(
        IpcCloudBackupRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = ValidateBackupKey(request.Key);
        string content = await GetCloudBackupContentsAsync(key);
        var importResult = await IpcBundleApi.ImportBundleAsync(
            new IpcBundleImportRequest
            {
                Content = content,
                Format = "ubundle",
                Append = request.Append,
            }
        );

        return new IpcCloudBackupRestoreResult
        {
            Status = importResult.Status,
            Command = "restore-cloud-backup",
            Message = importResult.Message,
            Key = key,
            SchemaVersion = importResult.SchemaVersion,
            Bundle = importResult.Bundle,
            SecurityReport = importResult.SecurityReport,
        };
    }

    private static IReadOnlyList<IPackage> GetInstalledPackagesForBackup()
    {
        return InstalledPackagesLoader.Instance?.Packages.ToList()
            ?? throw new InvalidOperationException("The installed packages loader is not available.");
    }

    private static string ResolveBackupDirectory()
    {
        string directory = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
        return string.IsNullOrWhiteSpace(directory)
            ? CoreData.UniGetUI_DefaultBackupDirectory
            : directory;
    }

    private static string BuildBackupFileName()
    {
        string fileName = Settings.GetValue(Settings.K.ChangeBackupFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = CoreTools.Translate(
                "{pcName} installed packages",
                new Dictionary<string, object?> { ["pcName"] = Environment.MachineName }
            );
        }

        if (Settings.Get(Settings.K.EnableBackupTimestamping))
        {
            fileName += " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        }

        return fileName + ".ubundle";
    }

    private static async Task<IpcGitHubAuthInfo> GetGitHubAuthInfoAsync()
    {
        PendingGitHubDeviceFlow? pending;
        lock (GitHubAuthLock)
        {
            pending = _pendingGitHubDeviceFlow;
        }

        bool isAuthenticated = !string.IsNullOrWhiteSpace(SecureGHTokenManager.GetToken());
        string login = Settings.GetValue(Settings.K.GitHubUserLogin);
        var auth = new IpcGitHubAuthInfo
        {
            ClientConfigured = HasConfiguredGitHubClient(),
            IsAuthenticated = isAuthenticated,
            Login = string.IsNullOrWhiteSpace(login) ? null : login,
            DeviceFlowPending = pending is not null && DateTimeOffset.UtcNow < pending.ExpiresAtUtc,
            UserCode = pending?.DeviceFlow.UserCode,
            VerificationUri = pending?.DeviceFlow.VerificationUri,
            ExpiresAt = pending?.ExpiresAtUtc,
            PollIntervalSeconds = pending?.DeviceFlow.Interval,
        };

        if (pending is not null && DateTimeOffset.UtcNow >= pending.ExpiresAtUtc)
        {
            ClearPendingGitHubDeviceFlow();
            auth.DeviceFlowPending = false;
            auth.UserCode = null;
            auth.VerificationUri = null;
            auth.ExpiresAt = null;
            auth.PollIntervalSeconds = null;
        }

        if (!isAuthenticated)
        {
            return auth;
        }

        if (!string.IsNullOrWhiteSpace(auth.Login))
        {
            return auth;
        }

        try
        {
            var client = CreateAuthenticatedGitHubClient();
            var user = await client.User.Current();
            if (!string.IsNullOrWhiteSpace(user.Login))
            {
                Settings.SetValue(Settings.K.GitHubUserLogin, user.Login);
                auth.Login = user.Login;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }

        return auth;
    }

    private static bool HasConfiguredGitHubClient()
    {
        string clientId = Secrets.GetGitHubClientId();
        return !string.IsNullOrWhiteSpace(clientId)
            && !string.Equals(clientId, MissingClientId, StringComparison.Ordinal);
    }

    private static void EnsureGitHubClientConfigured()
    {
        if (!HasConfiguredGitHubClient())
        {
            throw new InvalidOperationException(
                "GitHub sign-in is not configured for this build. UNIGETUI_GITHUB_CLIENT_ID is missing."
            );
        }
    }

    private static GitHubClient CreateAnonymousGitHubClient()
    {
        return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName));
    }

    private static GitHubClient CreateAuthenticatedGitHubClient(string? token = null)
    {
        token ??= SecureGHTokenManager.GetToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("GitHub authentication is required for cloud backups.");
        }

        return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName))
        {
            Credentials = new Credentials(token),
        };
    }

    private static async Task<(GitHubClient Client, User User)> GetAuthenticatedGitHubContextAsync()
    {
        var client = CreateAuthenticatedGitHubClient();
        var user = await client.User.Current();
        if (!string.IsNullOrWhiteSpace(user.Login))
        {
            Settings.SetValue(Settings.K.GitHubUserLogin, user.Login);
        }

        return (client, user);
    }

    private static async Task<string> GetCloudBackupContentsAsync(string key)
    {
        var (client, user) = await GetAuthenticatedGitHubContextAsync();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: false);
        if (backupGist is null)
        {
            throw new InvalidOperationException("No cloud backups are available for the current account.");
        }

        var fullGist = await client.Gist.Get(backupGist.Id);
        var file = fullGist.Files.FirstOrDefault(candidate =>
            candidate.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal)
            && candidate.Key.EndsWith(key, StringComparison.Ordinal));

        if (file.Value?.Content is null)
        {
            throw new InvalidOperationException($"The cloud backup \"{key}\" was not found.");
        }

        return file.Value.Content;
    }

    private static async Task<Gist?> GetBackupGistAsync(
        GitHubClient client,
        string userLogin,
        bool createIfMissing
    )
    {
        var candidates = await client.Gist.GetAllForUser(userLogin);
        var backupGist = candidates.FirstOrDefault(candidate =>
            candidate.Description?.EndsWith(GistDescriptionEndingKey, StringComparison.Ordinal)
            == true
        );

        if (backupGist is not null || !createIfMissing)
        {
            return backupGist;
        }

        var newGist = new NewGist { Description = GistDescription, Public = false };
        newGist.Files.Add("- UniGetUI Package Backups", ReadMeContents);
        return await client.Gist.Create(newGist);
    }

    private static string BuildGistFileKey()
    {
        string deviceUser = (Environment.MachineName + "\\" + Environment.UserName).Replace(
            " ",
            string.Empty
        );
        return PackageBackupStartingKey + " " + deviceUser;
    }

    private static string ValidateBackupKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("The backup key is required.");
        }

        return key;
    }

    private static PendingGitHubDeviceFlow GetPendingGitHubDeviceFlow()
    {
        lock (GitHubAuthLock)
        {
            return _pendingGitHubDeviceFlow
                ?? throw new InvalidOperationException(
                    "No GitHub device flow is pending. Start sign-in first."
                );
        }
    }

    private static void ClearPendingGitHubDeviceFlow()
    {
        lock (GitHubAuthLock)
        {
            _pendingGitHubDeviceFlow = null;
        }
    }
}
