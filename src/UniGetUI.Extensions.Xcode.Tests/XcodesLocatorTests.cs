namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodesLocatorTests
{
    private static string SearchPath(params string[] folders) => string.Join(Path.PathSeparator, folders);

    [Fact]
    public void Find_ReturnsTheXcodesOnThePath()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("exit 0\n");

        string? found = XcodesLocator.Find(SearchPath("/definitely/not/here", script.Folder), []);

        Assert.Equal(script.Path, found);
    }

    [Fact]
    public void Find_FallsBackToTheFoldersHomebrewAndMintUse()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("exit 0\n");

        Assert.Equal(script.Path, XcodesLocator.Find("/definitely/not/here", ["/also/not/here", script.Folder]));
        Assert.Equal(script.Path, XcodesLocator.Find(null, [script.Folder]));
        Assert.Equal(script.Path, XcodesLocator.Find("", [script.Folder]));
    }

    [Fact]
    public void Find_PrefersThePathOverTheFallbacks()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var onPath = new FakeXcodesScript("exit 0\n");
        using var fallback = new FakeXcodesScript("exit 0\n");

        Assert.Equal(onPath.Path, XcodesLocator.Find(onPath.Folder, [fallback.Folder]));
    }

    [Fact]
    public void Find_SkipsAFileThatCannotBeRun()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("exit 0\n");
        File.SetUnixFileMode(script.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Null(XcodesLocator.Find(script.Folder, []));
    }

    [Fact]
    public void Find_OnlyLooksForAFileNamedXcodes()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("exit 0\n", name: "xcodes-helper");

        Assert.Null(XcodesLocator.Find(script.Folder, []));
    }

    [Fact]
    public void Find_ReturnsNullWhenXcodesIsNowhere()
    {
        Assert.Null(XcodesLocator.Find("/definitely/not/here", ["/also/not/here"]));
        Assert.Null(XcodesLocator.Find(null, []));
    }

    [Fact]
    public void Find_ToleratesEntriesThatAreNotPaths()
    {
        string folder = Path.Combine(Path.GetTempPath(), "xcodes-tests-no-such-folder");

        Assert.Null(XcodesLocator.Find(SearchPath("", " ", "\0", folder), ["\0"]));
    }

    [Fact]
    public void FallbackFolders_CoverBothHomebrewPrefixesAndMint()
    {
        Assert.Contains("/opt/homebrew/bin", XcodesLocator.FallbackFolders);
        Assert.Contains("/usr/local/bin", XcodesLocator.FallbackFolders);
        Assert.Contains(XcodesLocator.FallbackFolders, folder => folder.EndsWith(Path.Combine(".mint", "bin"), StringComparison.Ordinal));
    }
}
