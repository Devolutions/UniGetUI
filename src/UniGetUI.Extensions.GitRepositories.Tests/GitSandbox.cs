namespace UniGetUI.Extensions.GitRepositories.Tests;

/// <summary>
/// A temporary folder of real git repositories: a bare origin and its clones. Git runs with a private
/// global configuration and no system one, so the machine's hooks, signing and line-ending settings
/// cannot change what the tests see.
/// </summary>
internal sealed class GitSandbox : IDisposable
{
    private readonly string _temporaryFolder;

    public GitSandbox()
    {
        _temporaryFolder = Path.Combine(Path.GetTempPath(), "UniGetUI.GitTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryFolder);

        string configuration = Path.Combine(_temporaryFolder, "gitconfig");
        File.WriteAllText(configuration, """
            [user]
                name = UniGetUI Tests
                email = tests@example.com
            [init]
                defaultBranch = main
            [commit]
                gpgsign = false
            [core]
                autocrlf = false
            """);
        Runner = new GitRunner(environment: new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = configuration,
        });
        Service = new GitRepositoryService(Runner);

        // Compare against the path git reports, which has no 8.3 short names (as CI temp folders do).
        string probe = Path.Combine(_temporaryFolder, "probe");
        Git(_temporaryFolder, "init", "--quiet", probe);
        Root = Path.GetDirectoryName(GitRepositoryService.NormalizePath(Git(probe, "rev-parse", "--show-toplevel")))!;
        DeleteFolder(probe);
    }

    public string Root { get; }
    public GitRunner Runner { get; }
    public GitRepositoryService Service { get; }

    public string Origin => Path.Combine(Root, "origin.git");

    /// <summary>Creates the bare origin with one commit on <paramref name="branch"/>, its default.</summary>
    public void CreateOrigin(string branch = "main")
    {
        Git(Root, "init", "--quiet", "--bare", "-b", branch, Origin);
        string seed = Path.Combine(Root, "seed");
        Git(Root, "clone", "--quiet", Origin, seed);
        Commit(seed, "README.md", "first");
        Git(seed, "push", "--quiet", "-u", "origin", branch);
    }

    public string Clone(string name)
    {
        string path = Path.Combine(Root, name);
        Git(Root, "clone", "--quiet", Origin, path);
        return path;
    }

    /// <summary>Pushes new commits to the origin's default branch from a separate clone.</summary>
    public void PushFromElsewhere(int commits = 1)
    {
        string other = Path.Combine(Root, "elsewhere");
        if (Directory.Exists(other)) Git(other, "pull", "--quiet", "--ff-only");
        else Git(Root, "clone", "--quiet", Origin, other);
        for (int i = 0; i < commits; i++)
            Commit(other, "remote.txt", Guid.NewGuid().ToString());
        Git(other, "push", "--quiet");
    }

    public void Commit(string repository, string file, string content)
    {
        string path = Path.Combine(repository, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Git(repository, "add", "--", file);
        Git(repository, "commit", "--quiet", "-m", $"Change {file}");
    }

    public string Head(string repository, string revision = "HEAD")
        => Git(repository, "rev-parse", revision);

    public bool RefExists(string repository, string reference)
        => Runner.RunAsync(repository, "rev-parse", "--verify", "--quiet", reference).GetAwaiter().GetResult().Success;

    /// <summary>Runs git and fails the test unless it succeeds; returns its trimmed output.</summary>
    public string Git(string workingDirectory, params string[] arguments)
    {
        GitResult result = Runner.RunAsync(workingDirectory, arguments).GetAwaiter().GetResult();
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.Output}");
        return result.StandardOutput.Trim();
    }

    public void Dispose()
    {
        try
        {
            DeleteFolder(_temporaryFolder);
        }
        catch (IOException)
        {
            // A git process that outlived its test may still hold a file; the temp folder is cleaned eventually.
        }
    }

    /// <summary>Deletes a folder that may hold git objects, which git marks read-only on Windows.</summary>
    public static void DeleteFolder(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
