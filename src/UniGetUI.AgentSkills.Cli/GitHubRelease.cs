// Latest GitHub release lookup, used by `skills add --pin latest` and the
// `gh skill` update check (extension; not in the reference CLI).
//
// Auth follows Blob.FetchRepoTree: an anonymous request first; when it fails
// in a way credentials can fix (401, 404, or a 403 rate limit), retry with
// GITHUB_TOKEN/GH_TOKEN, then with `gh api`.

using System.Text.Json.Nodes;

namespace Skills;

internal sealed class GitHubReleaseException(string message) : Exception(message);

internal static class GitHubRelease
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private const int GhApiMaxBuffer = 16 * 1024 * 1024;

    /// `tag_name` of a release object, when present and non-empty.
    public static string? ReleaseTag(JsonNode? data) => Json.NonEmpty(data, "tag_name");

    private abstract record Attempt;

    private sealed record Found(JsonNode? Value) : Attempt;

    private sealed record StatusAttempt(int Status, bool Retryable) : Attempt;

    private sealed record Failed(string Message) : Attempt;

    private static Attempt Fetch(string ownerRepo, string? token)
    {
        var req = HttpRequest.Get($"{Blob.GitHubApiBase()}/repos/{ownerRepo}/releases/latest")
            .Timeout(FetchTimeout).Header("Accept", "application/vnd.github.v3+json").Header("User-Agent", "skills-cli");
        if (token != null) req.Header("Authorization", $"Bearer {token}");
        HttpResponse resp;
        try
        {
            resp = req.Send();
        }
        catch (HttpException e)
        {
            return new Failed(e.Message);
        }
        catch (HttpTooLargeException e)
        {
            return new Failed(e.Message);
        }
        if (resp.Ok) return resp.TryJson(out var v) ? new Found(v) : new Failed("invalid response from GitHub");
        return new StatusAttempt(resp.Status, resp.Status is 401 or 404 || (resp.Status == 403 && resp.Header("x-ratelimit-remaining") == "0"));
    }

    private static bool TryFetchWithGitHubCli(string ownerRepo, out JsonNode? value)
    {
        value = null;
        try
        {
            var output = Proc.Command("gh", "api", $"repos/{ownerRepo}/releases/latest", "--method", "GET", "--hostname", GitHubHost.Get())
                .Env("GH_PROMPT_DISABLED", "1").Timeout(FetchTimeout).MaxOutput(GhApiMaxBuffer).HideWindow().Output();
            return output.Success && Json.TryParse(output.StdoutText, out value);
        }
        catch (ProcException)
        {
            return false;
        }
    }

    private static string StatusError(int status) => $"GitHub API returned HTTP {status}";

    /// The newest release tag of `owner/repo`, or null when the repository has
    /// no releases (HTTP 404 or no tag).
    /// <exception cref="GitHubReleaseException">Any other failure (the reason).</exception>
    public static string? ResolveLatestRelease(string ownerRepo)
    {
        int lastStatus;
        switch (Fetch(ownerRepo, null))
        {
            case Found f:
                return ReleaseTag(f.Value);
            case Failed e:
                throw new GitHubReleaseException(e.Message);
            case StatusAttempt { Retryable: false } s:
                throw new GitHubReleaseException(StatusError(s.Status));
            case StatusAttempt s:
                lastStatus = s.Status;
                break;
            default:
                throw new InvalidOperationException();
        }
        if (SkillLock.GetGitHubToken() is { } token)
        {
            switch (Fetch(ownerRepo, token))
            {
                case Found f:
                    return ReleaseTag(f.Value);
                case StatusAttempt s:
                    lastStatus = s.Status;
                    break;
            }
        }
        if (TryFetchWithGitHubCli(ownerRepo, out var data)) return ReleaseTag(data);
        if (lastStatus == 404) return null;
        throw new GitHubReleaseException(StatusError(lastStatus));
    }
}
