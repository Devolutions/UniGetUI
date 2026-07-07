using UniGetUI.Core.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface;

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

    public static GitHubApiClient? CreateGitHubClient()
    {
        string? token = SecureGHTokenManager.GetToken();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return new GitHubApiClient(token);
    }

    public static async Task<(string Login, string DisplayName)> GetCurrentUserAsync()
    {
        using var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var user = await client.GetCurrentUserAsync();
        string login = user.Login ?? string.Empty;
        string displayName = string.IsNullOrWhiteSpace(user.Name) ? login : user.Name;
        return (login, displayName);
    }

    public static async Task UploadPackageBundleAsync(string bundleContents)
    {
        using var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var backupGist = await GetBackupGistAsync(client, createIfMissing: true)
            ?? throw new InvalidOperationException(CoreTools.Translate("Backup Failed"));

        string fileKey = BuildGistFileKey();
        await client.EditGistAsync(
            backupGist.Id,
            GistDescription,
            new Dictionary<string, string> { [fileKey] = bundleContents }
        );
    }

    public static async Task<IReadOnlyList<CloudBackupEntry>> GetAvailableBackupsAsync()
    {
        using var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var backupGist = await GetBackupGistAsync(client, createIfMissing: false);
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
        using var client = CreateGitHubClient()
            ?? throw new InvalidOperationException(CoreTools.Translate("Log in to enable cloud backup"));

        var backupGist = await GetBackupGistAsync(client, createIfMissing: false);

        if (backupGist is null)
            throw new KeyNotFoundException(CoreTools.Translate("Log in to enable cloud backup"));

        var fullGist = await client.GetGistAsync(backupGist.Id);
        var file = fullGist.Files.FirstOrDefault(f =>
            f.Key.StartsWith(PackageBackupStartingKey, StringComparison.Ordinal)
            && f.Key.EndsWith(backupKey, StringComparison.Ordinal));

        if (file.Value?.Content is null)
            throw new KeyNotFoundException(CoreTools.Translate("Downloading backup..."));

        return file.Value.Content;
    }

    private static async Task<GitHubGist?> GetBackupGistAsync(
        GitHubApiClient client,
        bool createIfMissing
    )
    {
        var candidates = await client.GetCurrentUserGistsAsync();
        var backupGist = candidates.FirstOrDefault(g =>
            g.Description?.EndsWith(GistDescriptionEndingKey, StringComparison.Ordinal) == true);

        if (backupGist is not null || !createIfMissing)
            return backupGist;

        return await client.CreateGistAsync(
            GistDescription,
            isPublic: false,
            new Dictionary<string, string> { ["- UniGetUI Package Backups"] = ReadMeContents }
        );
    }

    private static string BuildGistFileKey()
    {
        string deviceUser = (Environment.MachineName + "\\" + Environment.UserName).Replace(" ", string.Empty);
        return PackageBackupStartingKey + " " + deviceUser;
    }
}
