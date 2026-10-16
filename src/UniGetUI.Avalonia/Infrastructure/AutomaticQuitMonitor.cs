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

public static class AutomaticQuitMonitor
{
    public const int RequiredIdleChecks = 2;

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
