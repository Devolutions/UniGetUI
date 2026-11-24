namespace UniGetUI.Core.Tools.Tests;

public class PreviousUpdateAttemptTests
{
    [Fact]
    public void Capture_KeepsThePreviousAttemptAfterTheLogIsOverwritten()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-previous-attempt-").FullName;
        try
        {
            string log = Path.Join(root, "last-update-attempt.log");
            string copy = Path.Join(root, "previous-update-attempt.log");
            File.WriteAllText(log, "Target version: 2026.3.5.0\n=== Swap failed: /Applications/UniGetUI.app\n");
            var previous = new PreviousUpdateAttempt(log, copy);

            UpdateAttemptSnapshot? first = previous.Capture();
            File.WriteAllText(log, "=== UniGetUI update attempt started ===\n");
            UpdateAttemptSnapshot? second = previous.Capture();

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Contains("=== Swap failed:", second.Content);
            Assert.True(previous.CopyWritten);
            Assert.Equal(first.Content, File.ReadAllText(copy));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Capture_ReportsNothingWhenThereWasNoPreviousAttempt()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-previous-attempt-").FullName;
        try
        {
            string log = Path.Join(root, "last-update-attempt.log");
            var previous = new PreviousUpdateAttempt(log, Path.Join(root, "previous-update-attempt.log"));

            Assert.Null(previous.Capture());
            File.WriteAllText(log, "=== UniGetUI update attempt started ===\n");
            Assert.Null(previous.Capture());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Capture_StillReturnsTheSnapshotWhenTheCopyCannotBeWritten()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-previous-attempt-").FullName;
        try
        {
            string log = Path.Join(root, "last-update-attempt.log");
            File.WriteAllText(log, "=== Swap failed: /Applications/UniGetUI.app\n");
            var previous = new PreviousUpdateAttempt(log, Path.Join(root, "missing-folder", "previous-update-attempt.log"));

            UpdateAttemptSnapshot? snapshot = previous.Capture();

            Assert.NotNull(snapshot);
            Assert.Contains("=== Swap failed:", snapshot.Content);
            Assert.False(previous.CopyWritten);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
