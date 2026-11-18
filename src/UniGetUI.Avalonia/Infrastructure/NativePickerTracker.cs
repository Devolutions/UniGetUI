namespace UniGetUI.Avalonia.Infrastructure;

internal static class NativePickerTracker
{
    private static int _openPickers;

    public static bool IsOpen => Volatile.Read(ref _openPickers) > 0;

    public static async Task<T> Track<T>(Task<T> picker)
    {
        Interlocked.Increment(ref _openPickers);
        try
        {
            return await picker;
        }
        finally
        {
            Interlocked.Decrement(ref _openPickers);
        }
    }
}
