namespace UniGetUI.Extensions.GitRepositories.Tests;

public sealed class DirectorySizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(DirectorySizeTests), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Measure_AddsUpEveryFileUnderTheFolder()
    {
        Write("a.txt", 100);
        Write(Path.Combine(".git", "objects", "pack.bin"), 2_000);
        Write(Path.Combine("bin", "Debug", "app.dll"), 30_000);

        Assert.Equal(32_100, DirectorySize.Measure(_root));
    }

    [Fact]
    public void Measure_LeavesOutExcludedFolders_ButNotTheFolderItself()
    {
        Write("a.txt", 100);
        Write(Path.Combine(".worktrees", "agent", "b.txt"), 5_000);
        string nested = Path.Combine(_root, ".worktrees", "agent");

        Assert.Equal(100, DirectorySize.Measure(_root, [nested + Path.DirectorySeparatorChar]));
        Assert.Equal(5_000, DirectorySize.Measure(nested, [nested]));
    }

    [Fact]
    public void Measure_IsNullForAMissingFolder_AndHonoursCancellation()
    {
        Assert.Null(DirectorySize.Measure(Path.Combine(_root, "missing")));

        Write(Path.Combine("sub", "a.txt"), 1);
        Assert.Throws<OperationCanceledException>(() => DirectorySize.Measure(_root, null, new CancellationToken(true)));
    }

    private void Write(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }
}
