using UniGetUI.PackageEngine.Managers.NpmManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class NpmNvmDiscoveryTests
{
    private static string CreateNvmNode(string nvmDirectory, string version)
    {
        string binDirectory = Path.Join(nvmDirectory, "versions", "node", version, "bin");
        Directory.CreateDirectory(binDirectory);
        string npmPath = Path.Join(binDirectory, "npm");
        File.WriteAllText(npmPath, "#!/usr/bin/env node\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                npmPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        return npmPath;
    }

    private static void RunWithNvmDirectory(Action<string> test)
    {
        string nvmDirectory = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        string? previous = Environment.GetEnvironmentVariable("NVM_DIR");

        try
        {
            Environment.SetEnvironmentVariable("NVM_DIR", nvmDirectory);
            test(nvmDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NVM_DIR", previous);
            if (Directory.Exists(nvmDirectory))
                Directory.Delete(nvmDirectory, true);
        }
    }

    [Fact]
    public void DiscoveryFindsNpmInstalledByNvmOutsideThePath()
    {
        if (OperatingSystem.IsWindows())
            return;

        RunWithNvmDirectory(nvmDirectory =>
        {
            string npmPath = CreateNvmNode(nvmDirectory, "v24.21.0");

            Assert.Contains(npmPath, new Npm().FindCandidateExecutableFiles());
        });
    }

    [Fact]
    public void DiscoveryPrefersTheNvmDefaultAliasThenTheNewestVersion()
    {
        if (OperatingSystem.IsWindows())
            return;

        RunWithNvmDirectory(nvmDirectory =>
        {
            string oldest = CreateNvmNode(nvmDirectory, "v18.20.4");
            string lts = CreateNvmNode(nvmDirectory, "v22.11.0");
            string newest = CreateNvmNode(nvmDirectory, "v24.21.0");
            Directory.CreateDirectory(Path.Join(nvmDirectory, "alias", "lts"));
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "default"), "lts/*\n");
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "lts", "*"), "lts/jod\n");
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "lts", "jod"), "v22.11.0\n");

            var candidates = new Npm().FindCandidateExecutableFiles().ToList();

            Assert.True(candidates.IndexOf(lts) >= 0);
            Assert.True(candidates.IndexOf(lts) < candidates.IndexOf(newest));
            Assert.True(candidates.IndexOf(newest) < candidates.IndexOf(oldest));
        });
    }

    [Fact]
    public void DiscoveryMatchesAPartialNvmDefaultVersion()
    {
        if (OperatingSystem.IsWindows())
            return;

        RunWithNvmDirectory(nvmDirectory =>
        {
            string older = CreateNvmNode(nvmDirectory, "v20.18.0");
            string newer = CreateNvmNode(nvmDirectory, "v24.21.0");
            Directory.CreateDirectory(Path.Join(nvmDirectory, "alias"));
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "default"), "20\n");

            var candidates = new Npm().FindCandidateExecutableFiles().ToList();

            Assert.True(candidates.IndexOf(older) >= 0);
            Assert.True(candidates.IndexOf(older) < candidates.IndexOf(newer));
        });
    }

    [Fact]
    public void DiscoveryFollowsACustomNvmAliasWhoseNameStartsWithV()
    {
        if (OperatingSystem.IsWindows())
            return;

        RunWithNvmDirectory(nvmDirectory =>
        {
            string configured = CreateNvmNode(nvmDirectory, "v22.11.0");
            string newest = CreateNvmNode(nvmDirectory, "v24.21.0");
            Directory.CreateDirectory(Path.Join(nvmDirectory, "alias"));
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "default"), "verified\n");
            File.WriteAllText(Path.Join(nvmDirectory, "alias", "verified"), "v22.11.0\n");

            var candidates = new Npm().FindCandidateExecutableFiles().ToList();

            Assert.True(candidates.IndexOf(configured) >= 0);
            Assert.True(candidates.IndexOf(configured) < candidates.IndexOf(newest));
        });
    }

    [Fact]
    public void DiscoveryIgnoresNvmNpmWithoutExecutablePermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        RunWithNvmDirectory(nvmDirectory =>
        {
            string npmPath = CreateNvmNode(nvmDirectory, "v24.21.0");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(npmPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.DoesNotContain(npmPath, new Npm().FindCandidateExecutableFiles());
        });
    }
}
