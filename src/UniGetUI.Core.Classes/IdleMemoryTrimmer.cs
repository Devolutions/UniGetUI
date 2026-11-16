using System.Runtime;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Classes;

/// <summary>
/// Hands memory back to the operating system once the app has gone quiet in the background.
///
/// The garbage collector only runs when the app allocates. A UniGetUI sitting in the tray
/// allocates almost nothing between update checks, so everything a check left behind (command
/// output, parsed package indexes, package lists it replaced) stayed in the heap as garbage
/// until the next check, and the heap stayed at its high-water mark the whole time.
///
/// Work that leaves such garbage calls <see cref="Request"/> when it finishes. Once no further
/// request has arrived for <see cref="QuietPeriod"/>, and only while the app is in the
/// background, one compacting collection runs and the freed memory is released. Nothing happens
/// while the user is looking at the window, where a blocking collection would only cost a pause.
/// </summary>
public static partial class IdleMemoryTrimmer
{
    /// <summary>
    /// Whether nobody is looking at the app right now. The front-end sets it: a window that is
    /// hidden or minimized, or a headless session. Defaults to "no", which disables trimming.
    /// </summary>
    public static Func<bool> IsAppInBackground { get; set; } = static () => false;

    /// <summary>How long the app must stay quiet after the last request before trimming.</summary>
    public static TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(20);

    private static long _lastRequest;

    /// <summary>The number of trims performed since the process started.</summary>
    public static int TrimCount => Volatile.Read(ref _trimCount);
    private static int _trimCount;

    /// <summary>
    /// Asks for a trim once the app has been quiet for <see cref="QuietPeriod"/>. Cheap and safe
    /// to call often, from any thread: a later request replaces the ones before it.
    /// </summary>
    public static void Request()
    {
        long request = Interlocked.Increment(ref _lastRequest);
        TimeSpan delay = QuietPeriod;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (request != Interlocked.Read(ref _lastRequest) || !IsAppInBackground())
                    return;

                TrimNow();
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not trim memory while idle");
                Logger.Warn(ex);
            }
        });
    }

    /// <summary>Collects and compacts the whole heap now, and releases what that frees.</summary>
    public static void TrimNow()
    {
        long before = GC.GetTotalMemory(forceFullCollection: false);
        long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long after = GC.GetTotalMemory(forceFullCollection: false);
        long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
        TrimNativeHeap();
        Interlocked.Increment(ref _trimCount);
        Logger.Debug(
            $"Trimmed memory while idle: managed heap {before / 1048576.0:F1} MB -> {after / 1048576.0:F1} MB"
                + $" (committed {committedBefore / 1048576.0:F1} MB -> {committedAfter / 1048576.0:F1} MB)"
        );
    }

    // On Linux, memory freed by native code (the runtime itself, TLS, compression, child
    // process plumbing) goes back to glibc's allocator, which keeps it for reuse rather than
    // returning it to the system, so the process creeps up over many update checks.
    // malloc_trim hands those free pages back. Other C libraries (musl) do not have it.
    private static bool _nativeTrimUnavailable;

    private static void TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux() || _nativeTrimUnavailable)
            return;

        try
        {
            _ = malloc_trim(0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _nativeTrimUnavailable = true;
        }
    }

    [LibraryImport("libc")]
    private static partial int malloc_trim(nuint pad);
}
