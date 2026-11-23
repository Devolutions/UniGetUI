namespace UniGetUI.PackageEngine.Interfaces;

/// <summary>
/// Marks package listings started by a remote rescan or the periodic inventory refresh. Inside
/// this scope a manager lists as it would for the user's Reload, but refreshes its indexes only
/// when that updates nothing but a catalogue, does not repair itself or recover interrupted work,
/// and fails a listing it cannot make without doing so.
/// </summary>
/// <remarks>
/// A loader enters it only around its own call to each manager, so it never reaches the loader's
/// events or the work their handlers start; they learn the mode from the event arguments.
/// </remarks>
public static class PackageListingScope
{
    private static readonly AsyncLocal<bool> ListingOnly = new();
    private static readonly AsyncLocal<Action<Exception>?> ReportFailure = new();

    public static bool IsListingOnly => ListingOnly.Value;

    /// <summary>
    /// Marks this call incomplete without discarding local UI rows or triggering fast repair.
    /// A listing-only call instead stops here, because it cannot supply partial inventory.
    /// </summary>
    public static void ReportIncomplete(Exception error)
    {
        ReportFailure.Value?.Invoke(error);
        if (IsListingOnly)
            throw error;
    }

    public static IDisposable TrackFailures(Action<Exception> reportFailure)
    {
        var previous = ReportFailure.Value;
        ReportFailure.Value = reportFailure;
        return new RestoreTracking(previous);
    }

    private sealed class RestoreTracking(Action<Exception>? previous) : IDisposable
    {
        public void Dispose() => ReportFailure.Value = previous;
    }

    public static IDisposable EnterListingOnly()
    {
        bool previous = ListingOnly.Value;
        ListingOnly.Value = true;
        return new Exit(previous);
    }

    private sealed class Exit(bool previous) : IDisposable
    {
        public void Dispose() => ListingOnly.Value = previous;
    }
}
