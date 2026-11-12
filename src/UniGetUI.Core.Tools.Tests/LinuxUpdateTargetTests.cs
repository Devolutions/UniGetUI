using System.Diagnostics;
using System.Runtime.Versioning;

namespace UniGetUI.Core.Tools.Tests;

public class LinuxUpdateTargetTests
{
    private static LinuxUpdateTarget Resolve(
        string executablePath,
        string installDirectory,
        string? owningPackageFormat,
        string[] writableDirectories,
        string[]? readOnlyPaths = null) =>
        LinuxUpdateTargetResolver.Resolve(
            executablePath,
            installDirectory,
            _ => owningPackageFormat is null ? PackageOwnership.NotOwned : PackageOwnership.OwnedBy(owningPackageFormat),
            path => readOnlyPaths?.Contains(path) ?? false,
            writableDirectories.Contains);

    [Theory]
    [InlineData(".deb")]
    [InlineData(".rpm")]
    public void Resolve_RefusesAnInstallOwnedByASystemPackage(string format)
    {
        LinuxUpdateTarget target = Resolve("/opt/unigetui/UniGetUI", "/opt/unigetui", format, []);

        Assert.Equal(LinuxUpdateTargetStatus.PackageManaged, target.Status);
        Assert.Equal(format, target.PackageFormat);
        Assert.Equal("/opt/unigetui", target.InstallDirectory);
    }

    [Fact]
    public void Resolve_RefusesAPackageOwnedInstallEvenWhenItIsWritable()
    {
        LinuxUpdateTarget target = Resolve("/opt/unigetui/UniGetUI", "/opt/unigetui", ".deb", ["/opt", "/opt/unigetui"]);

        Assert.Equal(LinuxUpdateTargetStatus.PackageManaged, target.Status);
    }

    [Fact]
    public void Resolve_PrefersThePackageOverAReadOnlyFileSystem()
    {
        LinuxUpdateTarget target = Resolve("/opt/unigetui/UniGetUI", "/opt/unigetui", ".rpm", [], ["/opt/unigetui"]);

        Assert.Equal(LinuxUpdateTargetStatus.PackageManaged, target.Status);
    }

    [Fact]
    public void Resolve_AsksTheOwnershipQueryAboutTheExecutable()
    {
        string? asked = null;
        LinuxUpdateTargetResolver.Resolve(
            "/opt/unigetui/UniGetUI",
            "/opt/unigetui",
            path =>
            {
                asked = path;
                return PackageOwnership.NotOwned;
            },
            _ => false,
            _ => true);

        Assert.Equal("/opt/unigetui/UniGetUI", asked);
    }

    [Fact]
    public void Resolve_RefusesToSwapWhenOwnershipCouldNotBeDetermined()
    {
        LinuxUpdateTarget target = LinuxUpdateTargetResolver.Resolve(
            "/home/me/apps/unigetui/UniGetUI",
            "/home/me/apps/unigetui",
            _ => PackageOwnership.Unknown,
            _ => false,
            _ => true);

        Assert.Equal(LinuxUpdateTargetStatus.OwnershipUnknown, target.Status);
    }

    [Fact]
    public void Resolve_ReportsAnUnownedInstallOnAReadOnlyFileSystem()
    {
        LinuxUpdateTarget target = Resolve("/mnt/ro/unigetui/UniGetUI", "/mnt/ro/unigetui", null, [], ["/mnt/ro/unigetui"]);

        Assert.Equal(LinuxUpdateTargetStatus.ReadOnlyFileSystem, target.Status);
    }

    [Fact]
    public void Resolve_ReportsAnUnownedInstallInAFolderTheUserCannotRename()
    {
        LinuxUpdateTarget target = Resolve("/opt/unigetui/UniGetUI", "/opt/unigetui", null, ["/opt/unigetui"]);

        Assert.Equal(LinuxUpdateTargetStatus.NotWritable, target.Status);
        Assert.Null(target.PackageFormat);
    }

    [Fact]
    public void Resolve_ReportsAnUnownedInstallTheUserCannotWriteTo()
    {
        LinuxUpdateTarget target = Resolve("/home/me/apps/unigetui/UniGetUI", "/home/me/apps/unigetui", null, ["/home/me/apps"]);

        Assert.Equal(LinuxUpdateTargetStatus.NotWritable, target.Status);
    }

    [Theory]
    [InlineData("/home/me/apps/unigetui", "/home/me/apps")]
    [InlineData("/home/me/apps/unigetui/", "/home/me/apps")]
    [InlineData("/unigetui", "/")]
    public void Resolve_SwapsAnUnownedInstallTheUserCanReplace(string installDirectory, string parent)
    {
        LinuxUpdateTarget target = Resolve(installDirectory + "/UniGetUI", installDirectory, null, [parent, installDirectory]);

        Assert.Equal(LinuxUpdateTargetStatus.Ready, target.Status);
    }

    [Theory]
    [InlineData("/opt/unigetui", "/opt")]
    [InlineData("/opt/unigetui/", "/opt")]
    [InlineData("/Applications/UniGetUI.app", "/Applications")]
    [InlineData("/unigetui", "/")]
    [InlineData("/", "/")]
    public void ParentDirectory_ReturnsTheFolderHoldingTheTarget(string target, string parent)
    {
        Assert.Equal(parent, SelfUpdateSwap.ParentDirectory(target));
    }

    private const string RpmNotOwned = "file /opt/unigetui/UniGetUI is not owned by any package";

    private static PackageOwnership Find(
        bool hasDpkg,
        bool hasRpm,
        PackageQueryResult? dpkg,
        PackageQueryResult? rpm,
        List<string>? calls = null) =>
        LinuxPackageOwnership.Find(
            "/opt/unigetui/UniGetUI",
            hasDpkg,
            hasRpm,
            (tool, flag, path) =>
            {
                calls?.Add($"{tool} {flag} {path}");
                return tool == "dpkg-query" ? dpkg : rpm;
            });

    [Fact]
    public void Find_ReportsADebOwnedFileWithoutAskingRpm()
    {
        List<string> calls = [];

        PackageOwnership ownership = Find(true, true, new(0, "unigetui: /opt/unigetui/UniGetUI"), null, calls);

        Assert.Equal(PackageOwnership.OwnedBy(".deb"), ownership);
        Assert.Equal(["dpkg-query -S /opt/unigetui/UniGetUI"], calls);
    }

    [Fact]
    public void Find_ReportsAnRpmOwnedFile()
    {
        PackageOwnership ownership = Find(false, true, null, new(0, "unigetui-2026.3.1-1.x86_64"));

        Assert.Equal(PackageOwnership.OwnedBy(".rpm"), ownership);
    }

    [Fact]
    public void Find_ReportsNotOwnedWhenEveryDatabaseSaysSo()
    {
        PackageOwnership ownership = Find(true, true, new(1, "dpkg-query: no path found matching pattern"), new(1, RpmNotOwned));

        Assert.Equal(PackageOwnership.NotOwned, ownership);
    }

    [Fact]
    public void Find_OnlyAsksTheToolsWhoseDatabaseExists()
    {
        List<string> calls = [];

        PackageOwnership none = Find(false, false, null, null, calls);
        Find(false, true, null, new(1, RpmNotOwned), calls);

        Assert.Equal(PackageOwnership.NotOwned, none);
        Assert.Equal(["rpm -qf /opt/unigetui/UniGetUI"], calls);
    }

    public static TheoryData<bool, bool, PackageQueryResult?, PackageQueryResult?> UnansweredQueries => new()
    {
        { true, false, null, null },
        { true, false, new(2, "dpkg-query: error: parsing file '/var/lib/dpkg/status'"), null },
        { false, true, null, null },
        { false, true, null, new(1, "error: rpmdb: BDB0113 Thread/process failed") },
        { true, true, new(1, "dpkg-query: no path found matching pattern"), null },
    };

    [Theory]
    [MemberData(nameof(UnansweredQueries))]
    public void Find_IsUnknownWhenADatabaseCouldNotBeAsked(bool hasDpkg, bool hasRpm, PackageQueryResult? dpkg, PackageQueryResult? rpm)
    {
        Assert.Equal(PackageOwnership.Unknown, Find(hasDpkg, hasRpm, dpkg, rpm));
    }

    [Fact]
    public void Find_PrefersAnOwnerOverAFailedQuery()
    {
        Assert.Equal(PackageOwnership.OwnedBy(".rpm"), Find(true, true, null, new(0, "unigetui-2026.3.1-1.x86_64")));
    }

    [Theory]
    [InlineData("/opt/unigetui/UniGetUI", "/opt/unigetui/UniGetUI")]
    [InlineData("/opt/unigetui[beta]/UniGetUI", "/opt/unigetui\\[beta\\]/UniGetUI")]
    [InlineData("/opt/a*b?c/UniGetUI", "/opt/a\\*b\\?c/UniGetUI")]
    [InlineData("/opt/back\\slash/UniGetUI", "/opt/back\\\\slash/UniGetUI")]
    public void Find_PassesDpkgALiteralPathAndRpmTheRawOne(string path, string dpkgPattern)
    {
        Dictionary<string, string> asked = [];
        LinuxPackageOwnership.Find(
            path,
            true,
            true,
            (tool, _, argument) =>
            {
                asked[tool] = argument;
                return new PackageQueryResult(1, RpmNotOwned);
            });

        Assert.Equal(dpkgPattern, asked["dpkg-query"]);
        Assert.Equal(path, asked["rpm"]);
    }

    [Fact]
    public void RunQuery_ReturnsNullWhenTheToolIsNotInstalled()
    {
        Assert.Null(LinuxPackageOwnership.RunQuery("unigetui-no-such-package-tool", "-S", "/opt/unigetui/UniGetUI"));
    }

    [Fact]
    public void RunQuery_GivesUpWhenAChildKeepsTheOutputOpenAfterTheToolExits()
    {
        (string tool, string flag, string script) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c", "start /b ping -n 6 127.0.0.1")
            : ("/bin/sh", "-c", "sleep 5 &");
        Stopwatch watch = Stopwatch.StartNew();

        PackageQueryResult? result = LinuxPackageOwnership.RunQuery(tool, flag, script, TimeSpan.FromSeconds(1));

        Assert.Null(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"RunQuery took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [LinuxFact]
    public void Find_RecognisesAFileOwnedByTheSystemPackageManager()
    {
        if (File.Exists(LinuxPackageOwnership.DpkgDatabase) && File.Exists("/usr/bin/dpkg-query"))
        {
            Assert.Equal(PackageOwnership.OwnedBy(".deb"), LinuxPackageOwnership.Find("/usr/bin/dpkg-query"));
        }
        else if (File.Exists("/usr/bin/rpm"))
        {
            Assert.Equal(PackageOwnership.OwnedBy(".rpm"), LinuxPackageOwnership.Find("/usr/bin/rpm"));
        }
        else
        {
            Assert.Fail("This Linux system has neither dpkg nor rpm.");
        }
    }

    [LinuxFact]
    public void Find_ReportsAFileNoPackageOwnsAsNotOwned()
    {
        string file = Path.Join(Path.GetTempPath(), $"unigetui-ownership-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            Assert.Equal(PackageOwnership.NotOwned, LinuxPackageOwnership.Find(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class LinuxSwapScriptTests
{
    private const string Shown = "<install dir>";

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void SwapScript_ReplacesTheInstallAndLaunchesTheNewVersion()
    {
        using SwapSandbox box = new();

        box.Run();

        Assert.Equal(box.Install, box.WaitForLaunch());
        Assert.Equal("new", box.InstalledVersion());
        Assert.False(Directory.Exists(box.Install + ".old"));
        Assert.False(SelfUpdateSwap.TryGetFailure(box.Log(), out _));
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void SwapScript_RollsBackAndReportsWhenTheNewVersionCannotBeMoved()
    {
        using SwapSandbox box = new();
        Directory.Delete(box.NewRoot, recursive: true);

        box.Run();

        Assert.Equal(box.Install, box.WaitForLaunch());
        Assert.Equal("old", box.InstalledVersion());
        Assert.True(SelfUpdateSwap.TryGetFailure(box.Log(), out string shown));
        Assert.Equal(Shown, shown);
        Assert.Contains("Update swap failed while moving the new version into place: No such file or directory", box.Log());
        Assert.DoesNotContain(box.Root, box.Log());
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void SwapScript_ReportsWhenTheInstalledVersionCannotBeMovedAside()
    {
        using SwapSandbox box = new();
        box.FailMvWhen("\"$2\" = \"$INSTALL.old\"");

        box.Run();

        Assert.Equal(box.Install, box.WaitForLaunch());
        Assert.Equal("old", box.InstalledVersion());
        Assert.True(SelfUpdateSwap.TryGetFailure(box.Log(), out _));
        Assert.Contains("Update swap failed while moving the installed version aside: Permission denied", box.Log());
        Assert.DoesNotContain(box.Root, box.Log());
    }

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void SwapScript_LaunchesThePreviousVersionFromWhereItWasLeftWhenTheRollbackFails()
    {
        using SwapSandbox box = new();
        box.FailMvWhen("\"$2\" = \"$INSTALL\"");

        box.Run();

        Assert.Equal(box.Install + ".old", box.WaitForLaunch());
        Assert.False(Directory.Exists(box.Install));
        Assert.True(SelfUpdateSwap.TryGetFailure(box.Log(), out string shown));
        Assert.Equal(Shown, shown);
        Assert.Contains($"restoring the previous version, which was left in {Shown}.old: Permission denied", box.Log());
        Assert.DoesNotContain(box.Root, box.Log());
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class SwapSandbox : IDisposable
    {
        private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private string? _fakeBin;

        public string Root { get; } = Directory.CreateTempSubdirectory("unigetui-linux-swap-").FullName;
        public string Install => Path.Join(Root, "opt", "unigetui");
        public string NewRoot => Path.Join(Root, "staging", "unigetui");
        private string LogPath => Path.Join(Root, "last-update-attempt.log");
        private string LaunchedPath => Path.Join(Root, "launched");

        public SwapSandbox()
        {
            WriteTree(Install, "old");
            WriteTree(NewRoot, "new");
            File.WriteAllText(LogPath, "Target version: 2026.1.0\n");
        }

        private static void WriteTree(string dir, string version)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "version"), version);
            string exe = Path.Join(dir, "UniGetUI");
            File.WriteAllText(exe, "#!/bin/sh\ndirname \"$0\" > \"$LAUNCHED\"\n");
            File.SetUnixFileMode(exe, Executable);
        }

        public void FailMvWhen(string condition)
        {
            _fakeBin = Path.Join(Root, "bin");
            Directory.CreateDirectory(_fakeBin);
            string fakeMv = Path.Join(_fakeBin, "mv");
            File.WriteAllText(
                fakeMv,
                "#!/bin/sh\n"
                + $"if [ {condition} ]; then echo \"mv: cannot move '$1' to '$2': Permission denied\" >&2; exit 1; fi\n"
                + "for d in $(echo \"$ORIGINAL_PATH\" | tr ':' ' '); do [ -x \"$d/mv\" ] && exec \"$d/mv\" \"$@\"; done\n"
                + "exit 127\n");
            File.SetUnixFileMode(fakeMv, Executable);
        }

        public string InstalledVersion() => File.ReadAllText(Path.Join(Install, "version"));

        public string Log() => File.ReadAllText(LogPath);

        public void Run()
        {
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (string arg in new[]
                     {
                         "-c", LinuxUpdateTargetResolver.SwapScript, "sh", ExitedProcessId().ToString(),
                         Install, NewRoot, "UniGetUI", LogPath, SelfUpdateSwap.FailedMarker, Shown,
                     })
            {
                startInfo.ArgumentList.Add(arg);
            }

            string path = startInfo.Environment["PATH"] ?? "/usr/bin:/bin";
            startInfo.Environment["ORIGINAL_PATH"] = path;
            startInfo.Environment["INSTALL"] = Install;
            startInfo.Environment["LAUNCHED"] = LaunchedPath;
            if (_fakeBin is not null)
            {
                startInfo.Environment["PATH"] = _fakeBin + ":" + path;
            }

            using Process process = Process.Start(startInfo)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        public string WaitForLaunch()
        {
            Assert.True(SpinWait.SpinUntil(() => File.Exists(LaunchedPath) && new FileInfo(LaunchedPath).Length > 0, TimeSpan.FromSeconds(10)));
            return File.ReadAllText(LaunchedPath).Trim();
        }

        private static int ExitedProcessId()
        {
            using Process process = Process.Start("true")!;
            process.WaitForExit();
            return process.Id;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
