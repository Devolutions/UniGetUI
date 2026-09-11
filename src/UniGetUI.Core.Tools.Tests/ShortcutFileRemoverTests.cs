namespace UniGetUI.Core.Tools.Tests;

public sealed class ShortcutFileRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        nameof(ShortcutFileRemoverTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly string _outsideRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(ShortcutFileRemoverTests),
        Guid.NewGuid().ToString("N")
    );

    public ShortcutFileRemoverTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outsideRoot);
        ShortcutFileRemover.TEST_ShortcutRootsOverride = [_root];
    }

    public void Dispose()
    {
        ShortcutFileRemover.TEST_ShortcutRootsOverride = null;
        foreach (string directory in new[] { _root, _outsideRoot })
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "shortcut");
        return path;
    }

    [Fact]
    public void ShortcutsUnderAKnownRootAreRemovable()
    {
        Assert.True(
            ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "LibreOffice 26.8.lnk"))
        );
        Assert.True(ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "Site.URL")));
        Assert.True(
            ShortcutFileRemover.IsRemovableShortcutPath(
                Path.Combine(_root, "LibreOffice", "Writer.lnk")
            )
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPathsAreNotRemovable(string path)
    {
        Assert.False(ShortcutFileRemover.IsRemovableShortcutPath(path));
    }

    [Fact]
    public void PathsOutsideEveryKnownRootAreNotRemovable()
    {
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_outsideRoot, "Payload.lnk"))
        );
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(
                Path.Combine(_root, "..", "Payload.lnk")
            )
        );
    }

    [Fact]
    public void OnlyShortcutFilesAreRemovable()
    {
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "Payload.exe"))
        );
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "Payload"))
        );
    }

    [Fact]
    public void AlternateDataStreamsAreNotRemovable()
    {
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(
                Path.Combine(_root, "Writer.lnk:stream.lnk")
            )
        );
        Assert.False(
            ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "Payload.exe:.lnk"))
        );
    }

    [Fact]
    public void WildcardsAreNotRemovable()
    {
        Assert.False(ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "*.lnk")));
        Assert.False(ShortcutFileRemover.IsRemovableShortcutPath(Path.Combine(_root, "?.lnk")));
    }

    [Fact]
    public void DeleteRemovesAnOrdinaryShortcut()
    {
        string shortcut = CreateFile(_root, "Writer.lnk");

        Assert.True(ShortcutFileRemover.Delete(shortcut));
        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void DeleteRemovesAReadOnlyShortcut()
    {
        string shortcut = CreateFile(_root, "ReadOnly.lnk");
        File.SetAttributes(shortcut, File.GetAttributes(shortcut) | FileAttributes.ReadOnly);

        Assert.True(ShortcutFileRemover.Delete(shortcut));
        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void DeleteReportsFailureForAShortcutThatIsInUse()
    {
        string shortcut = CreateFile(_root, "InUse.lnk");
        using var handle = new FileStream(
            shortcut,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None
        );

        Assert.False(ShortcutFileRemover.Delete(shortcut));
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public void DeletingABatchRemovesEveryShortcut()
    {
        string[] shortcuts =
        [
            CreateFile(_root, "One.lnk"),
            CreateFile(_root, "Two.lnk"),
            CreateFile(Path.Combine(_root, "Nested"), "Three.lnk"),
        ];

        ShortcutFileRemover.Delete(shortcuts);

        Assert.All(shortcuts, shortcut => Assert.False(File.Exists(shortcut)));
    }

    [Fact]
    public void TheElevatedInstanceRemovesShortcutsUnderAKnownRoot()
    {
        string shortcut = CreateFile(_root, "Writer.lnk");

        Assert.Equal(0, ShortcutFileRemover.DeleteAsElevatedInstance([shortcut]));
        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void TheElevatedInstanceRefusesAShortcutReachedThroughAJunction()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string outside = CreateFile(_outsideRoot, "Payload.lnk");
        string junction = Path.Combine(_root, "Vendor");
        if (!TryCreateJunction(junction, _outsideRoot))
            return;

        try
        {
            Assert.Equal(
                1,
                ShortcutFileRemover.DeleteAsElevatedInstance(
                    [Path.Combine(junction, "Payload.lnk")]
                )
            );
            Assert.True(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void TheElevatedInstanceRemovesAShortcutInsideARealSubfolder()
    {
        string shortcut = CreateFile(Path.Combine(_root, "Vendor"), "Writer.lnk");

        Assert.Equal(0, ShortcutFileRemover.DeleteAsElevatedInstance([shortcut]));
        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void TheElevatedInstanceTreatsAMissingShortcutAsDeleted()
    {
        Assert.Equal(
            0,
            ShortcutFileRemover.DeleteAsElevatedInstance([Path.Combine(_root, "Missing.lnk")])
        );
    }

    [Fact]
    public void TheElevatedInstanceReportsAShortcutHeldOpenWithoutDeleteSharing()
    {
        string shortcut = CreateFile(_root, "Locked.lnk");
        using var handle = new FileStream(
            shortcut,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );

        Assert.Equal(1, ShortcutFileRemover.DeleteAsElevatedInstance([shortcut]));
        Assert.True(File.Exists(shortcut));
    }

    [Fact]
    public void TheElevatedInstanceRemovesAReadOnlyShortcut()
    {
        string shortcut = CreateFile(_root, "ReadOnlyElevated.lnk");
        File.SetAttributes(shortcut, File.GetAttributes(shortcut) | FileAttributes.ReadOnly);

        Assert.Equal(0, ShortcutFileRemover.DeleteAsElevatedInstance([shortcut]));
        Assert.False(File.Exists(shortcut));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "/c", "mklink", "/J", link, target },
            }
        );

        process!.WaitForExit();
        return process.ExitCode is 0 && Directory.Exists(link);
    }

    [Fact]
    public void TheElevatedInstanceRefusesPathsItDoesNotManage()
    {
        string outside = CreateFile(_outsideRoot, "Payload.lnk");
        string wrongExtension = CreateFile(_root, "Payload.exe");

        Assert.Equal(1, ShortcutFileRemover.DeleteAsElevatedInstance([outside, wrongExtension]));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(wrongExtension));
    }

    [Fact]
    public void TheElevatedInstanceReportsFailureWhenOnlySomePathsAreManaged()
    {
        string managed = CreateFile(_root, "Writer.lnk");
        string outside = CreateFile(_outsideRoot, "Payload.lnk");

        Assert.Equal(1, ShortcutFileRemover.DeleteAsElevatedInstance([managed, outside]));
        Assert.False(File.Exists(managed));
        Assert.True(File.Exists(outside));
    }
}
