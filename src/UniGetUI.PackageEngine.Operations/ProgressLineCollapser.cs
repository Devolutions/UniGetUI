namespace UniGetUI.PackageOperations;

// Folds a live log stream for in-place display: progress indicators (carriage-return redraws such
// as installer spinners or download bars) repaint the previous line instead of stacking up,
// mirroring how a terminal repaints in place. The first non-progress line that follows settles
// into that same line. Because a progress line is always the most recent one, "replace" always
// targets the last rendered line.
public sealed class ProgressLineCollapser
{
    public enum Fold { Append, ReplaceLast }

    private bool _lastWasProgress;

    public Fold Next(AbstractOperation.LineType type)
    {
        bool wasProgress = _lastWasProgress;
        _lastWasProgress = type == AbstractOperation.LineType.ProgressIndicator;
        return wasProgress ? Fold.ReplaceLast : Fold.Append;
    }
}
