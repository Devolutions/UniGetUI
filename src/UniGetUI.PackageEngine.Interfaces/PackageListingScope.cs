namespace UniGetUI.PackageEngine.Interfaces;

/// <summary>
/// Marks package listings started by a remote rescan. Inside this scope a manager lists exactly as
/// it would for the user's Reload (index refreshes included) but does not repair itself or recover
/// interrupted work, and fails a listing it cannot make without doing so.
/// </summary>
/// <remarks>
/// The flag flows with the execution context, so it reaches the tasks a loader starts for each
/// manager, but not a listing already running for someone else that a caller merely waits for.
/// </remarks>
public static class PackageListingScope
{
    private static readonly AsyncLocal<bool> ListingOnly = new();

    public static bool IsListingOnly => ListingOnly.Value;

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
