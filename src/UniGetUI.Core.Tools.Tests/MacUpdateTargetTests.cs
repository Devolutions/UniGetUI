using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace UniGetUI.Core.Tools.Tests;

public class MacUpdateTargetTests
{
    private const string TranslocatedBundle =
        "/private/var/folders/76/pyfy9ch97zg773pw3v188r980000gn/T/AppTranslocation/A19F29BA-B27D-440A-821C-E438AF47F812/d/UniGetUI.app";

    private const string TranslocatedExecutable = TranslocatedBundle + "/Contents/MacOS/UniGetUI";

    private static MacUpdateTarget Resolve(
        string? executablePath,
        string? translocatedOriginal,
        string[] existingDirectories,
        string[] writableDirectories,
        string[]? readOnlyVolumePaths = null) =>
        MacUpdateTargetResolver.Resolve(
            executablePath,
            _ => translocatedOriginal,
            existingDirectories.Contains,
            writableDirectories.Contains,
            path => readOnlyVolumePaths?.Contains(path) ?? false);

    [Fact]
    public void Resolve_UpdatesTheOriginalBundleWhenRunningFromATranslocatedCopy()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            "/Applications/UniGetUI.app",
            [TranslocatedBundle, "/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
        Assert.Equal(TranslocatedBundle, target.RunningBundlePath);
    }

    [Fact]
    public void Resolve_UpdatesTheOriginalBundleOutsideApplicationsWhenTheSystemReportsIt()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            "/Users/me/Apps/UniGetUI.app",
            [TranslocatedBundle, "/Users/me/Apps/UniGetUI.app", "/Applications/UniGetUI.app"],
            ["/Users/me/Apps", "/Users/me/Apps/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Users/me/Apps/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_FallsBackToApplicationsWhenTheTranslocatedOriginalCannotBeResolved()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            null,
            [TranslocatedBundle, "/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_RefusesToUpdateATranslocatedCopyWithNoOriginalToReplace()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            null,
            [TranslocatedBundle],
            [TranslocatedBundle]);

        Assert.Equal(MacUpdateTargetStatus.TranslocatedUnresolved, target.Status);
    }

    [Fact]
    public void Resolve_ReportsARootOwnedBundleAsNotWritable()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications"]);

        Assert.Equal(MacUpdateTargetStatus.NotWritable, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_ReportsAnUnwritableParentFolderAsNotWritable()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.NotWritable, target.Status);
    }

    [Fact]
    public void Resolve_ReportsABundleOnAMountedDiskImageAsReadOnly()
    {
        MacUpdateTarget target = Resolve(
            "/Volumes/UniGetUI/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Volumes/UniGetUI/UniGetUI.app"],
            [],
            ["/Volumes/UniGetUI/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.ReadOnlyVolume, target.Status);
        Assert.Equal("/Volumes/UniGetUI/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_ReportsATranslocatedCopyFromAMountedDiskImageAsReadOnly()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            "/Volumes/UniGetUI/UniGetUI.app",
            [TranslocatedBundle, "/Volumes/UniGetUI/UniGetUI.app", "/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"],
            ["/Volumes/UniGetUI/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.ReadOnlyVolume, target.Status);
        Assert.Equal("/Volumes/UniGetUI/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_IsReadyForAWritableInstalledBundle()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_FallsBackToApplicationsWhenNotRunningFromABundle()
    {
        MacUpdateTarget target = Resolve(
            "/usr/local/bin/unigetui",
            null,
            [],
            []);

        Assert.Equal(MacUpdateTargetStatus.BundleNotFound, target.Status);
        Assert.Equal(MacUpdateTargetResolver.DefaultInstallPath, target.BundlePath);
        Assert.Null(target.RunningBundlePath);
    }

    [Theory]
    [InlineData("/Applications/UniGetUI.app/Contents/MacOS/UniGetUI", "/Applications/UniGetUI.app")]
    [InlineData(TranslocatedExecutable, TranslocatedBundle)]
    [InlineData("/usr/local/bin/unigetui", null)]
    [InlineData(null, null)]
    public void FindEnclosingAppBundle_ReturnsTheInnermostBundle(string? executable, string? expected)
    {
        Assert.Equal(expected, MacUpdateTargetResolver.FindEnclosingAppBundle(executable));
    }

    [Fact]
    public void TryGetSwapFailure_ReadsTheBundleTheHelperCouldNotReplace()
    {
        string log =
            "Target version: 2026.1.0\n"
            + "[INFO ] Replacing /Applications/UniGetUI.app with the freshly-extracted bundle and relaunching.\n"
            + "\n"
            + MacUpdateTargetResolver.SwapFailedMarker + " /Applications/UniGetUI.app\n";

        Assert.True(MacUpdateTargetResolver.TryGetSwapFailure(log, out string bundle));
        Assert.Equal("/Applications/UniGetUI.app", bundle);
    }

    [Fact]
    public void TryGetSwapFailure_IgnoresALogWithoutTheMarker()
    {
        Assert.False(MacUpdateTargetResolver.TryGetSwapFailure("Target version: 2026.1.0\n", out _));
    }

    [MacOSFact]
    public void ElevatedSwapScript_ReplacesTheBundleAndDeletesTheOldOneOnceTheAppHasExited()
    {
        using SwapSandbox box = new();

        Assert.Equal(0, box.Run(ExitedProcessId()));

        Assert.Equal("new", box.InstalledVersion());
        Assert.True(SpinWait.SpinUntil(() => !Directory.Exists(box.Target + ".old"), TimeSpan.FromSeconds(5)));
    }

    [MacOSFact]
    public void ElevatedSwapScript_KeepsTheOldBundleWhileTheAppIsStillRunning()
    {
        using SwapSandbox box = new();

        Assert.Equal(0, box.Run(Environment.ProcessId));

        Assert.Equal("new", box.InstalledVersion());
        Assert.Equal("old", File.ReadAllText(Path.Join(box.Target + ".old", "version")));
    }

    [MacOSFact]
    public void ElevatedSwapScript_RefusesAnArchiveThatNoLongerMatchesItsHash()
    {
        using SwapSandbox box = new();
        string expected = box.Hash;
        box.ReplaceArchive("tampered");

        Assert.NotEqual(0, box.Run(ExitedProcessId(), expected));

        Assert.Equal("old", box.InstalledVersion());
        Assert.False(Directory.Exists(box.Target + ".old"));
    }

    [MacOSFact]
    [SupportedOSPlatform("macos")]
    public void ElevatedSwapScript_RestoresTheOldBundleWhenTheNewOneIsOnlyPartlyMoved()
    {
        using SwapSandbox box = new();
        // A mv that leaves a partial destination behind, as a failed cross-volume copy can.
        string fakeBin = Path.Join(box.Root, "bin");
        Directory.CreateDirectory(fakeBin);
        string fakeMv = Path.Join(fakeBin, "mv");
        File.WriteAllText(
            fakeMv,
            "#!/bin/sh\n"
            + "if [ \"$2\" = \"$TARGET\" ] && [ \"$1\" != \"$TARGET.old\" ]; then mkdir -p \"$2\"; touch \"$2/partial\"; exit 1; fi\n"
            + "exec /bin/mv \"$@\"\n");
        File.SetUnixFileMode(fakeMv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.NotEqual(0, box.Run(ExitedProcessId(), pathPrefix: fakeBin));

        Assert.Equal("old", box.InstalledVersion());
        Assert.False(File.Exists(Path.Join(box.Target, "partial")));
        Assert.False(Directory.Exists(Path.Join(box.Target, "UniGetUI.app.old")));
    }

    private static int ExitedProcessId()
    {
        using Process process = Process.Start("/usr/bin/true");
        process.WaitForExit();
        return process.Id;
    }

    private sealed class SwapSandbox : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("unigetui-swap-").FullName;
        public string Target => Path.Join(Root, "Applications", "UniGetUI.app");
        public string Archive => Path.Join(Root, "UniGetUI Updater.tar.gz");
        public string Hash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Archive))).ToLowerInvariant();

        public SwapSandbox()
        {
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Join(Target, "version"), "old");
            ReplaceArchive("new");
        }

        public void ReplaceArchive(string version)
        {
            string source = Path.Join(Root, "source-" + version);
            Directory.CreateDirectory(Path.Join(source, "UniGetUI.app"));
            File.WriteAllText(Path.Join(source, "UniGetUI.app", "version"), version);
            File.Delete(Archive);
            using Process tar = Process.Start("/usr/bin/tar", ["-czf", Archive, "-C", source, "UniGetUI.app"]);
            tar.WaitForExit();
            Assert.Equal(0, tar.ExitCode);
        }

        public string InstalledVersion() => File.ReadAllText(Path.Join(Target, "version"));

        public int Run(int pid, string? hash = null, string? pathPrefix = null)
        {
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (string arg in new[] { "-c", MacUpdateTargetResolver.ElevatedSwapScript, "sh", Target, Archive, hash ?? Hash, pid.ToString() })
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.Environment["TARGET"] = Target;
            if (pathPrefix is not null)
            {
                startInfo.Environment["PATH"] = pathPrefix + ":" + startInfo.Environment["PATH"];
            }

            using Process process = Process.Start(startInfo)!;
            process.WaitForExit();
            return process.ExitCode;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
