namespace UniGetUI.Avalonia.Infrastructure;

public readonly record struct AutomaticQuitSnapshot(
    bool IsBusy,
    bool IsEnabled,
    bool IsAlreadyQuitting,
    bool IsWindowVisible,
    bool HasUnsavedWork);

public enum AutomaticQuitDecision
{
    KeepWaiting,
    Quit,
    Cancelled,
    WindowVisible,
    UnsavedWork,
}

public sealed class OneTimeClaim
{
    private int _claimed;

    public bool IsClaimed => Volatile.Read(ref _claimed) == 1;

    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}

public sealed class AutomaticQuitRequest
{
    private readonly Lock _lock = new();
    private bool _waiting;
    private bool _nothingToInstall;
    private bool? _lateNothingToInstall;

    public bool NothingToInstall
    {
        get
        {
            lock (_lock)
                return _nothingToInstall;
        }
    }

    public bool TryBegin(bool nothingToInstall)
    {
        lock (_lock)
        {
            if (_waiting)
            {
                if (!nothingToInstall)
                    _nothingToInstall = false;
                _lateNothingToInstall = (_lateNothingToInstall ?? true) && nothingToInstall;
                return false;
            }

            _waiting = true;
            _nothingToInstall = nothingToInstall;
            _lateNothingToInstall = null;
            return true;
        }
    }

    public void NoteUpdatePass()
    {
        lock (_lock)
        {
            if (_waiting)
                _nothingToInstall = false;
        }
    }

    public void MarkSampled()
    {
        lock (_lock)
            _lateNothingToInstall = null;
    }

    public bool? End()
    {
        lock (_lock)
        {
            bool? late = _lateNothingToInstall;
            _waiting = false;
            _lateNothingToInstall = null;
            return late;
        }
    }
}

public static class AutomaticQuitMonitor
{
    public const int RequiredIdleChecks = 2;

    public static bool IsQuitEnabled(bool quitAfterUpdates, bool quitWhenNothingToInstall, bool nothingToInstall)
        => nothingToInstall ? quitWhenNothingToInstall : quitAfterUpdates;

    public static bool ShouldQuitAfterEmptyPass(bool checkSucceeded, bool installDue, bool firstUpdateAppsPass)
        => checkSucceeded && (installDue || firstUpdateAppsPass);

    public static bool ShouldQuitAfterInstallPass(bool launched, bool checkSucceeded)
        => launched || checkSucceeded;

    public static bool IsFirstUpdateAppsPass(bool hasUpdateAppsArgument, bool checkSucceeded, bool foundUpdates, OneTimeClaim handled)
        => hasUpdateAppsArgument
            && (checkSucceeded || foundUpdates)
            && handled.TryClaim();

    public static AutomaticQuitDecision Evaluate(AutomaticQuitSnapshot snapshot, ref int idleChecks)
    {
        if (snapshot.IsBusy)
        {
            idleChecks = 0;
            return AutomaticQuitDecision.KeepWaiting;
        }

        if (++idleChecks < RequiredIdleChecks)
            return AutomaticQuitDecision.KeepWaiting;

        if (!snapshot.IsEnabled || snapshot.IsAlreadyQuitting)
            return AutomaticQuitDecision.Cancelled;

        if (snapshot.IsWindowVisible)
            return AutomaticQuitDecision.WindowVisible;

        if (snapshot.HasUnsavedWork)
            return AutomaticQuitDecision.UnsavedWork;

        return AutomaticQuitDecision.Quit;
    }

    public static async Task<AutomaticQuitDecision> RunAsync(
        Task? launching,
        Func<Task> waitForNextSample,
        Func<Func<AutomaticQuitDecision>, Task<AutomaticQuitDecision>> runOnUiThread,
        Func<AutomaticQuitSnapshot?> sample,
        Action quit)
    {
        if (launching is not null)
            await launching;

        int idleChecks = 0;
        while (true)
        {
            await waitForNextSample();
            AutomaticQuitDecision decision = await runOnUiThread(() =>
            {
                if (sample() is not { } snapshot)
                    return AutomaticQuitDecision.Cancelled;

                AutomaticQuitDecision result = Evaluate(snapshot, ref idleChecks);
                if (result is AutomaticQuitDecision.Quit)
                    quit();
                return result;
            });

            if (decision is not AutomaticQuitDecision.KeepWaiting)
                return decision;
        }
    }
}
