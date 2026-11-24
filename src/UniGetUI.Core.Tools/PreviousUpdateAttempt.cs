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

    // False when the copy could not be written, so an older copy must not be shown in its place.
    public bool CopyWritten { get; private set; }

    // A failed read is not remembered, so a later call can still capture the attempt.
    public UpdateAttemptSnapshot? Capture()
    {
        lock (_lock)
        {
            if (_captured)
                return _snapshot;

            if (File.Exists(logPath))
                _snapshot = new UpdateAttemptSnapshot(File.ReadAllText(logPath), File.GetLastWriteTime(logPath));
            _captured = true;

            if (_snapshot is not null)
            {
                try
                {
                    File.WriteAllText(CopyPath, _snapshot.Content);
                    CopyWritten = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The snapshot is still usable; View log then falls back to the live log.
                }
            }

            return _snapshot;
        }
    }
}
