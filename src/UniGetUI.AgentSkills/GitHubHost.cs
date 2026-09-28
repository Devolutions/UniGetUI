// GitHub host selection via GH_HOST (port of github-host.ts).

namespace Skills;

internal static class GitHubHost
{
    private const string DefaultHost = "github.com";

    /// The host selected by the GitHub CLI. GH_HOST must be a bare hostname;
    /// anything else falls back to github.com.
    public static string Get()
    {
        var configured = Sys.EnvTrimmed("GH_HOST");
        if (configured == null) return DefaultHost;
        var u = WebUrl.Parse("https://" + configured);
        if (u == null || u.Username.Length > 0 || u.Password.Length > 0 || u.Port.Length > 0 || u.Pathname != "/" || u.Search.Length > 0 || u.Hash.Length > 0)
            return DefaultHost;
        return u.Hostname;
    }

    /// Whether a host is GitHub.com or the configured GitHub Enterprise host.
    public static bool IsGitHubHost(string host)
    {
        var h = host.ToLowerInvariant();
        return h == DefaultHost || h == Get().ToLowerInvariant();
    }
}
