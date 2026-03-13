using Octokit;
using UniGetUI.Core.Data;
using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class GitHubCloudBackupService
{
    private const string GistDescriptionEndingKey = "@[UNIGETUI_BACKUP_V1]";
    private const string PackageBackupStartingKey = "@[PACKAGES]";
    private const string GistDescription = "UniGetUI package backups - DO NOT RENAME OR MODIFY " + GistDescriptionEndingKey;
    private const string ReadMeContents = "This special Gist is used by UniGetUI to store your package backups.\n"
        + "Please DO NOT EDIT the contents or the description of this gist, or unexpected behaviours may occur.\n"
        + "Learn more about UniGetUI at https://github.com/Devolutions/UniGetUI\n";

    internal sealed class CloudBackupEntry
    {
        public required string Key { get; init; }
        public required string Display { get; init; }
    }

    public static GitHubClient? CreateGitHubClient()
    {
        string? token = SecureGHTokenManager.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return new GitHubClient(new ProductHeaderValue("UniGetUI", CoreData.VersionName))
        {
            Credentials = new Credentials(token),
        };
    }

    public static async Task<(string Login, string DisplayName)> GetCurrentUserAsync()
    {
        var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var user = await client.User.Current();
        string login = user.Login ?? string.Empty;
        string displayName = string.IsNullOrWhiteSpace(user.Name) ? login : user.Name;
        return (login, displayName);
    }

    public static async Task UploadPackageBundleAsync(string bundleContents)
    {
        var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var user = await client.User.Current();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: true)
            ?? throw new InvalidOperationException(CoreTools.Translate("Backup Failed"));

        string fileKey = BuildGistFileKey();
        var update = new GistUpdate { Description = GistDescription };

        if (backupGist.Files.ContainsKey(fileKey))
            update.Files[fileKey] = new GistFileUpdate { Content = bundleContents };
        else
            update.Files.Add(fileKey, new GistFileUpdate { Content = bundleContents });

        await client.Gist.Edit(backupGist.Id, update);
    }

    public static async Task<IReadOnlyList<CloudBackupEntry>> GetAvailableBackupsAsync()
    {
        var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var user = await client.User.Current();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: false);
        if (backupGist is null)
            return [];

        return backupGist.Files
            .Where(f => f.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal))
            .Select(f => new CloudBackupEntry
            {
                Key = f.Key.Split(' ')[^1],
                Display = f.Key.Split(' ')[^1] + " (" + CoreTools.FormatAsSize(f.Value.Size) + ")",
            })
            .ToArray();
    }

    public static async Task<string> GetBackupContentsAsync(string backupKey)
    {
        var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var user = await client.User.Current();
        var backupGist = await GetBackupGistAsync(client, user.Login, createIfMissing: false);

        if (backupGist is null)
            throw new KeyNotFoundException(CoreTools.Translate("Log in to enable cloud backup"));

        var fullGist = await client.Gist.Get(backupGist.Id);
        var file = fullGist.Files.FirstOrDefault(f =>
            f.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal)
            && f.Key.EndsWith(backupKey, StringComparison.Ordinal));

        if (file.Value?.Content is null)
            throw new KeyNotFoundException(CoreTools.Translate("Downloading backup..."));

        return file.Value.Content;
    }

    private static async Task<Gist?> GetBackupGistAsync(GitHubClient client, string userLogin, bool createIfMissing)
    {
        var candidates = await client.Gist.GetAllForUser(userLogin);
        var backupGist = candidates.FirstOrDefault(g =>
            g.Description?.EndsWith(GistDescriptionEndingKey, StringComparison.Ordinal) == true);

        if (backupGist is not null || !createIfMissing)
            return backupGist;

        var newGist = new NewGist { Description = GistDescription, Public = false };
        newGist.Files.Add("- UniGetUI Package Backups", ReadMeContents);
        return await client.Gist.Create(newGist);
    }

    private static string BuildGistFileKey()
    {
        string deviceUser = (Environment.MachineName + "\\" + Environment.UserName).Replace(" ", string.Empty);
        return PackageBackupStartingKey + " " + deviceUser;
    }
}
