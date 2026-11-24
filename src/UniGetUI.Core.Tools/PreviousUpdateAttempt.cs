namespace UniGetUI.Core.Tools;

public sealed record UpdateAttemptSnapshot(string Content, DateTime WrittenAt);

/// <summary>
/// The update log left by the previous run, read once before this run's first attempt overwrites it,
/// with a copy kept at <see cref="CopyPath"/> so it can still be opened afterwards.
/// </summary>
public sealed class PreviousUpdateAttempt(string logPath, string copyPath)
{
    private readonly Lock _lock = new();
    private bool _captured;
    private UpdateAttemptSnapshot? _snapshot;

    public string CopyPath { get; } = copyPath;

    public UpdateAttemptSnapshot? Capture()
    {
        lock (_lock)
        {
            if (_captured)
                return _snapshot;
            _captured = true;

            if (!File.Exists(logPath))
                return null;

            _snapshot = new UpdateAttemptSnapshot(File.ReadAllText(logPath), File.GetLastWriteTime(logPath));
            File.WriteAllText(CopyPath, _snapshot.Content);
            return _snapshot;
        }
    }
}
