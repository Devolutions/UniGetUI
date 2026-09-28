using UniGetUI.Core.Logging;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.FakeData;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>A backup stored in the cloud.</summary>
internal sealed record CloudBackup(string Key, string Display);

/// <summary>
/// Cloud backup of the installed-package list (desktop Backup page, cloud half). With real data it uses
/// the same GitHub gist (same description and file-name keys) as the desktop app's GitHubCloudBackupService and signs in with
/// GitHub's device flow, which suits a terminal: the user enters a short code on github.com, from any
/// device. In fake-data mode the "cloud" is a folder in the sandbox and sign-in is simulated.
/// </summary>
internal static class TuiCloudBackup
{
    private static readonly string[] Scopes = ["read:user", "gist"];

    public static event Action? StatusChanged;

    private static string FakeCloudDirectory => Path.Join(FakeDataEnvironment.Current!.SandboxDirectory, "FakeCloud");

    private static string FakeAccountFile => Path.Join(FakeCloudDirectory, "signed-in-as.txt");

    public static bool IsConfigured => TuiEngine.IsFakeData || TuiSecrets.GitHubClientId != "CLIENT_ID_UNSET";

    public static bool IsSignedIn => TuiEngine.IsFakeData
        ? File.Exists(FakeAccountFile)
        : !string.IsNullOrEmpty(SecureGHTokenManager.GetToken());

    public static string UserLogin => TuiEngine.IsFakeData
        ? (File.Exists(FakeAccountFile) ? File.ReadAllText(FakeAccountFile).Trim() : "")
        : Settings.GetValue(Settings.K.GitHubUserLogin);

    /// <summary>
    /// Signs in. <paramref name="showCode"/> is called with the verification URL and the user code; the
    /// method then waits until the user has authorized the app (or the code expires / is cancelled).
    /// </summary>
    public static async Task<bool> SignInAsync(Func<string, string, Task> showCode, CancellationToken cancellation)
    {
        if (TuiEngine.IsFakeData)
        {
            await showCode("https://fake.unigetui.invalid/login/device", "FAKE-0000");
            Directory.CreateDirectory(FakeCloudDirectory);
            await File.WriteAllTextAsync(FakeAccountFile, "fake-user", cancellation);
            StatusChanged?.Invoke();
            return true;
        }

        if (!IsConfigured)
        {
            Logger.Error("GitHub sign-in is not configured for this build (missing OAuth client ID).");
            return false;
        }

        using var client = new GitHubApiClient();
        GitHubDeviceFlow flow = await client.InitiateDeviceFlowAsync(TuiSecrets.GitHubClientId, Scopes, cancellation);
        await showCode(flow.VerificationUri, flow.UserCode);

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, flow.Interval));
        DateTime expires = DateTime.UtcNow.AddSeconds(Math.Max(60, flow.ExpiresIn));
        while (DateTime.UtcNow < expires)
        {
            await Task.Delay(interval, cancellation);
            GitHubOAuthToken token = await client.CreateAccessTokenForDeviceFlowAsync(TuiSecrets.GitHubClientId, flow, cancellation);
            if (string.IsNullOrEmpty(token.AccessToken)) continue; // authorization_pending / slow_down

            SecureGHTokenManager.StoreToken(token.AccessToken);
            using var userClient = new GitHubApiClient(token.AccessToken);
            GitHubUser user = await userClient.GetCurrentUserAsync(cancellation);
            Settings.SetValue(Settings.K.GitHubUserLogin, user.Login);
            StatusChanged?.Invoke();
            return true;
        }

        return false;
    }

    public static void SignOut()
    {
        if (TuiEngine.IsFakeData)
        {
            if (File.Exists(FakeAccountFile)) File.Delete(FakeAccountFile);
        }
        else
        {
            Settings.SetValue(Settings.K.GitHubUserLogin, "");
            SecureGHTokenManager.DeleteToken();
        }

        StatusChanged?.Invoke();
    }

    /// <summary>Uploads a backup of the installed packages (desktop <c>DoCloudBackupStatic</c>).</summary>
    public static async Task<bool> BackupNowAsync()
    {
        try
        {
            if (!IsSignedIn) throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));
            string contents = await TuiBundleService.CreateBundleJsonAsync(InstalledPackagesLoader.Instance.Packages.ToList());
            if (TuiEngine.IsFakeData)
            {
                Directory.CreateDirectory(FakeCloudDirectory);
                string key = (Environment.MachineName + "_" + Environment.UserName).Replace(' ', '_');
                await File.WriteAllTextAsync(Path.Join(FakeCloudDirectory, key + ".ubundle"), contents);
            }
            else
            {
                await GistStore.UploadAsync(contents);
            }

            Logger.ImportantInfo("Cloud backup uploaded");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while performing a CLOUD backup:");
            Logger.Error(ex);
            return false;
        }
    }

    public static async Task<IReadOnlyList<CloudBackup>> ListAsync()
    {
        if (TuiEngine.IsFakeData)
        {
            if (!Directory.Exists(FakeCloudDirectory)) return [];
            return Directory.GetFiles(FakeCloudDirectory, "*.ubundle")
                .Select(f => new CloudBackup(Path.GetFileNameWithoutExtension(f),
                    Path.GetFileNameWithoutExtension(f) + " (" + CoreTools.FormatAsSize(new FileInfo(f).Length) + ")"))
                .ToList();
        }

        return await GistStore.ListAsync();
    }

    public static async Task<string> DownloadAsync(string key)
        => TuiEngine.IsFakeData
            ? await File.ReadAllTextAsync(Path.Join(FakeCloudDirectory, key + ".ubundle"))
            : await GistStore.DownloadAsync(key);
}

/// <summary>The backup gist, in the exact format the desktop app reads and writes.</summary>
internal static class GistStore
{
    private const string GistDescriptionEndingKey = "@[UNIGETUI_BACKUP_V1]";
    private const string PackageBackupStartingKey = "@[PACKAGES]";
    private const string GistDescription = "UniGetUI package backups - DO NOT RENAME OR MODIFY " + GistDescriptionEndingKey;
    private const string ReadMeContents = "This special Gist is used by UniGetUI to store your package backups.\n"
        + "Please DO NOT EDIT the contents or the description of this gist, or unexpected behaviours may occur.\n"
        + "Learn more about UniGetUI at https://github.com/Devolutions/UniGetUI\n";

    private static GitHubApiClient CreateClient()
    {
        string? token = SecureGHTokenManager.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));
        return new GitHubApiClient(token);
    }

    public static async Task UploadAsync(string bundleContents)
    {
        using var client = CreateClient();
        var gist = await GetBackupGistAsync(client, createIfMissing: true)
                   ?? throw new InvalidOperationException(CoreTools.Translate("Backup Failed"));
        string deviceUser = (Environment.MachineName + "\\" + Environment.UserName).Replace(" ", string.Empty);
        await client.EditGistAsync(gist.Id, GistDescription,
            new Dictionary<string, string> { [PackageBackupStartingKey + " " + deviceUser] = bundleContents });
    }

    public static async Task<IReadOnlyList<CloudBackup>> ListAsync()
    {
        using var client = CreateClient();
        var gist = await GetBackupGistAsync(client, createIfMissing: false);
        if (gist is null) return [];
        return gist.Files
            .Where(f => f.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal))
            .Select(f => new CloudBackup(f.Key.Split(' ')[^1], f.Key.Split(' ')[^1] + " (" + CoreTools.FormatAsSize(f.Value.Size) + ")"))
            .ToList();
    }

    public static async Task<string> DownloadAsync(string backupKey)
    {
        using var client = CreateClient();
        var gist = await GetBackupGistAsync(client, createIfMissing: false)
                   ?? throw new KeyNotFoundException(CoreTools.Translate("Log in to enable cloud backup"));
        var full = await client.GetGistAsync(gist.Id);
        var file = full.Files.FirstOrDefault(f => f.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal)
                                                  && f.Key.EndsWith(backupKey, StringComparison.Ordinal));
        return file.Value?.Content ?? throw new KeyNotFoundException(CoreTools.Translate("Downloading backup..."));
    }

    private static async Task<GitHubGist?> GetBackupGistAsync(GitHubApiClient client, bool createIfMissing)
    {
        var candidates = await client.GetCurrentUserGistsAsync();
        var gist = candidates.FirstOrDefault(g => g.Description?.EndsWith(GistDescriptionEndingKey, StringComparison.Ordinal) == true);
        if (gist is not null || !createIfMissing) return gist;
        return await client.CreateGistAsync(GistDescription, isPublic: false,
            new Dictionary<string, string> { ["- UniGetUI Package Backups"] = ReadMeContents });
    }
}
