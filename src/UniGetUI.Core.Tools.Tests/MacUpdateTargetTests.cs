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
        string[] writableDirectories) =>
        MacUpdateTargetResolver.Resolve(
            executablePath,
            _ => translocatedOriginal,
            existingDirectories.Contains,
            writableDirectories.Contains);

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
}
