// Git clone operations (port of git.ts). Invokes `git` directly with the same
// config overrides and environment the TS CLI passes through simple-git.

using System.Text.RegularExpressions;

namespace Skills;

internal sealed class GitCloneException(string message, string url, bool isTimeout, bool isAuthError) : Exception(message)
{
    public string Url { get; } = url;
    public bool IsTimeout { get; } = isTimeout;
    public bool IsAuthError { get; } = isAuthError;
}

internal sealed record GitHubRepoInfo(string Owner, string Repo, string Slug, string SshUrl);

internal static partial class Git
{
    private const long DefaultCloneTimeoutMs = 300_000;
    private const string AllowedGitProtocols = "https:http:ssh:git:file";

    private static long CloneTimeoutMs =>
        Sys.Env("SKILLS_CLONE_TIMEOUT_MS") is { } raw && ParseIntPrefix(raw) is { } v && v > 0 ? v : DefaultCloneTimeoutMs;

    /// `Number.parseInt(s, 10)`; null for NaN.
    public static long? ParseIntPrefix(string s)
    {
        var t = s.TrimStart();
        var sign = 1;
        if (t.StartsWith('-'))
        {
            sign = -1;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }
        var digits = new string(t.TakeWhile(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return null;
        return long.TryParse(digits, out var v) ? v * sign : long.MaxValue;
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase)] private static partial Regex Sha40();
    [GeneratedRegex("Remote branch .* not found in upstream origin", RegexOptions.IgnoreCase)] private static partial Regex RemoteBranchMissing();
    [GeneratedRegex("couldn't find remote ref", RegexOptions.IgnoreCase)] private static partial Regex CouldntFindRef();
    [GeneratedRegex("upload-pack: not our ref", RegexOptions.IgnoreCase)] private static partial Regex NotOurRef();
    [GeneratedRegex(@"^git@([^:]+):([^/]+)/([^/]+?)(?:\.git)?$", RegexOptions.IgnoreCase)] private static partial Regex SshRepo();
    [GeneratedRegex(@"^/([^/]+)/([^/]+?)(?:\.git)?/?$")] private static partial Regex HttpRepoPath();
    [GeneratedRegex("^git@([^:]+):")] private static partial Regex SshHost();
    [GeneratedRegex(@"Git operations protocol:\s+ssh", RegexOptions.IgnoreCase)] private static partial Regex GhSshProtocol();

    public static bool IsCommitSha(string r) => Sha40().IsMatch(r);

    public static bool IsMissingRefError(string message) =>
        RemoteBranchMissing().IsMatch(message) || CouldntFindRef().IsMatch(message) || NotOurRef().IsMatch(message);

    public static GitHubRepoInfo? ParseGitHubRepoUrl(string url)
    {
        var m = SshRepo().Match(url);
        if (m.Success && GitHubHost.IsGitHubHost(m.Groups[1].Value))
        {
            var (host, owner, repo) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            return new GitHubRepoInfo(owner, repo, $"{owner}/{repo}", $"git@{host}:{owner}/{repo}.git");
        }
        var u = WebUrl.Parse(url);
        if (u == null || !GitHubHost.IsGitHubHost(u.Host)) return null;
        var pm = HttpRepoPath().Match(u.Pathname);
        if (!pm.Success) return null;
        var (o, r) = (pm.Groups[1].Value, pm.Groups[2].Value);
        return new GitHubRepoInfo(o, r, $"{o}/{r}", $"git@{u.Host}:{o}/{r}.git");
    }

    public static bool IsGitHubHttpsCloneUrl(string url) => WebUrl.Parse(url) is { Scheme: "https" } u && GitHubHost.IsGitHubHost(u.Host);

    public static bool IsGitHubSsoAuthError(string message)
    {
        var l = message.ToLowerInvariant();
        return l.Contains("saml sso") || l.Contains("enforced sso") || l.Contains("enabled or enforced saml") || l.Contains("re-authorize the oauth application");
    }

    private static bool IsAuthFailure(string m) =>
        m.Contains("Authentication failed") || m.Contains("could not read Username") || m.Contains("Permission denied")
        || m.Contains("Repository not found") || m.Contains("requested URL returned error: 403") || IsGitHubSsoAuthError(m);

    /// Run git with the LFS-disabling config and safe environment. Returns null
    /// on success, else the error message.
    private static string? RunGit(IEnumerable<string> args, string? cwd, params (string Key, string Value)[] extraEnv)
    {
        var full = new List<string> { "-c", "filter.lfs.required=false", "-c", "filter.lfs.smudge=", "-c", "filter.lfs.clean=", "-c", "filter.lfs.process=" };
        if (cwd != null)
        {
            full.Add("-C");
            full.Add(cwd);
        }
        full.AddRange(args);
        var cmd = Proc.Command("git", full)
            .Env("GIT_TERMINAL_PROMPT", "0")
            .Env("GIT_ALLOW_PROTOCOL", AllowedGitProtocols)
            .Env("GIT_LFS_SKIP_SMUDGE", "1")
            .Timeout(TimeSpan.FromMilliseconds(CloneTimeoutMs));
        foreach (var (k, v) in extraEnv) cmd.Env(k, v);
        try
        {
            var o = cmd.Output();
            if (o.Success) return null;
            var msg = o.StderrText.Trim();
            return msg.Length == 0 ? $"git exited with code {o.Status}" : msg;
        }
        catch (ProcException e) when (e.Kind == ProcErrorKind.Timeout)
        {
            return "block timeout reached";
        }
        catch (ProcException e) when (e.Kind == ProcErrorKind.NotFound)
        {
            return "spawn git ENOENT: git is not installed or not on PATH";
        }
        catch (ProcException e)
        {
            return e.Message;
        }
    }

    private static List<string> CloneArgs(string url, string dir, string? r)
    {
        var v = new List<string> { "clone", "--depth", "1" };
        if (r != null)
        {
            v.Add("--branch");
            v.Add(r);
        }
        v.AddRange(["--", url, dir]);
        return v;
    }

    private static string? CloneAtSha(string url, string sha, string dir, params (string, string)[] env) =>
        RunGit(["init"], dir, env) ?? RunGit(["remote", "add", "origin", url], dir, env)
        ?? RunGit(["fetch", "--depth", "1", "origin", sha], dir, env) ?? RunGit(["checkout", "FETCH_HEAD"], dir, env);

    private static void ResetTempDir(string dir)
    {
        try { Fs.RemoveAll(dir); } catch { /* best effort */ }
        Directory.CreateDirectory(dir);
    }

    private static bool TryGhClone(GitHubRepoInfo repo, string dir, string? r)
    {
        var sshHost = SshHost().Match(repo.SshUrl);
        var host = sshHost.Success ? sshHost.Groups[1].Value : "github.com";
        var target = repo.Slug;
        try
        {
            var status = Proc.Command("gh", "auth", "status", "-h", host).Env("GIT_TERMINAL_PROMPT", "0").Timeout(TimeSpan.FromSeconds(5)).Output();
            if (!status.Success) return false;
            if (GhSshProtocol().IsMatch(status.StdoutText + status.StderrText)) target = repo.SshUrl;
        }
        catch (ProcException)
        {
            return false;
        }
        var args = new List<string> { "repo", "clone", target, dir, "--", "--depth=1" };
        if (r != null)
        {
            args.Add("--branch");
            args.Add(r);
        }
        try
        {
            return Proc.Command("gh", args).Env("GIT_TERMINAL_PROMPT", "0").Env("GIT_ALLOW_PROTOCOL", AllowedGitProtocols)
                .Timeout(TimeSpan.FromMilliseconds(CloneTimeoutMs)).Output().Success;
        }
        catch (ProcException)
        {
            return false;
        }
    }

    private static string BuildGitHubAuthError(string url, GitHubRepoInfo? repo, string message)
    {
        var host = repo != null && SshHost().Match(repo.SshUrl) is { Success: true } hm ? hm.Groups[1].Value : "github.com";
        if (repo != null && IsGitHubSsoAuthError(message))
            return $"GitHub blocked HTTPS access to {url} because the organization enforces SAML SSO.\n" +
                   "  skills tried your existing git credentials and available fallbacks, but none succeeded.\n" +
                   "  - Re-authorize your GitHub credentials/app for that org's SSO policy\n" +
                   $"  - Or rerun with SSH: skills add {repo.SshUrl}\n" +
                   $"  - Verify access with: gh auth status -h {host} or ssh -T git@{host}";
        if (repo != null)
            return $"Authentication failed for {url}.\n" +
                   "  - For private repos, ensure you have access\n" +
                   $"  - Retry with SSH: skills add {repo.SshUrl}\n" +
                   $"  - Check access with: gh auth status -h {host} or ssh -T git@{host}";
        return $"Authentication failed for {url}.\n" +
               "  - For private repos, ensure you have access\n" +
               "  - For SSH: Check your keys with 'ssh -T git@github.com'\n" +
               "  - For HTTPS: Run 'gh auth login' or configure git credentials";
    }

    /// Shallow-clone `url` (optionally at `ref`) into a fresh temp directory.
    /// <exception cref="GitCloneException"/>
    public static string CloneRepo(string url, string? r)
    {
        if (url.StartsWith("ext::", StringComparison.OrdinalIgnoreCase)) throw new GitCloneException("Unsupported Git transport: ext", url, false, false);
        var tempDir = Sys.MkdTemp("skills-");
        var refCanBeSha = r != null && IsCommitSha(r);
        var repo = ParseGitHubRepoUrl(url);

        var error = RunGit(CloneArgs(url, tempDir, r), null);
        if (error == null) return tempDir;

        if (refCanBeSha && IsMissingRefError(error))
        {
            ResetTempDir(tempDir);
            if (CloneAtSha(url, r!, tempDir) == null) return tempDir;
        }

        var isTimeout = error.Contains("block timeout") || error.Contains("timed out");
        var isAuth = IsAuthFailure(error);
        if (isTimeout)
        {
            try { Fs.RemoveAll(tempDir); } catch { /* ignore */ }
            var seconds = Math.Round(CloneTimeoutMs / 1000.0, MidpointRounding.AwayFromZero);
            throw new GitCloneException(
                $"Clone timed out after {seconds}s. Common causes:\n" +
                "  - Large repository: raise the timeout with SKILLS_CLONE_TIMEOUT_MS=600000 (10m)\n" +
                "  - Slow network: retry, or clone manually and pass the local path to 'skills add'\n" +
                "  - Private repo without credentials: ensure auth is configured\n" +
                "      - For SSH: ssh-add -l (to check loaded keys)\n" +
                "      - For HTTPS: gh auth status (if using GitHub CLI)",
                url, true, false);
        }

        if (isAuth && repo != null && IsGitHubHttpsCloneUrl(url))
        {
            ResetTempDir(tempDir);
            if (TryGhClone(repo, tempDir, r)) return tempDir;
            ResetTempDir(tempDir);
            var sshCmd = Sys.EnvRaw("GIT_SSH_COMMAND") ?? "ssh -o BatchMode=yes";
            var sshError = RunGit(CloneArgs(repo.SshUrl, tempDir, r), null, ("GIT_SSH_COMMAND", sshCmd));
            if (sshError != null && refCanBeSha && IsMissingRefError(sshError))
            {
                ResetTempDir(tempDir);
                sshError = CloneAtSha(repo.SshUrl, r!, tempDir, ("GIT_SSH_COMMAND", sshCmd));
            }
            if (sshError == null) return tempDir;
        }

        try { Fs.RemoveAll(tempDir); } catch { /* ignore */ }
        if (isAuth) throw new GitCloneException(BuildGitHubAuthError(url, repo, error), url, false, true);
        throw new GitCloneException($"Failed to clone {url}: {error}", url, false, false);
    }

    /// Git tree object for a locked skill path (matches the Trees API SHA).
    public static string? GetGitTreeHash(string repoDir, string skillPath)
    {
        var segments = skillPath.Replace('\\', '/').Split('/').ToList();
        segments.RemoveAt(segments.Count - 1);
        var folder = string.Join("/", segments);
        var revision = folder.Length == 0 ? "HEAD^{tree}" : $"HEAD:{folder}";
        try
        {
            var o = Proc.Command("git", "-C", repoDir, "rev-parse", "--verify", "--end-of-options", revision)
                .Env("GIT_OPTIONAL_LOCKS", "0").Env("GIT_TERMINAL_PROMPT", "0").Timeout(TimeSpan.FromMilliseconds(CloneTimeoutMs)).Output();
            if (!o.Success) return null;
            var hash = o.StdoutText.Trim();
            return Sha40().IsMatch(hash) ? hash.ToLowerInvariant() : null;
        }
        catch (ProcException)
        {
            return null;
        }
    }

    /// Remove a temp directory, refusing paths outside the system temp dir.
    public static void CleanupTempDir(string dir)
    {
        var normalized = NodePath.Normalize(NodePath.Resolve(dir));
        var tmp = NodePath.Normalize(NodePath.Resolve(Sys.TmpDir()));
        if (!normalized.StartsWith(tmp + NodePath.Sep, StringComparison.Ordinal) && normalized != tmp)
            throw new InvalidOperationException("Attempted to clean up directory outside of temp directory");
        Fs.RemoveAll(dir);
    }

    public static void TryCleanup(string? dir)
    {
        if (dir == null) return;
        try
        {
            CleanupTempDir(dir);
        }
        catch
        {
            // ignore cleanup errors
        }
    }
}
