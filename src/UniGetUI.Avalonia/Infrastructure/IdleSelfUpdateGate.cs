namespace UniGetUI.Avalonia.Infrastructure;

public readonly record struct IdleSelfUpdateSnapshot(
    bool IsEnabled,
    bool IsBusy,
    bool IsWindowVisible,
    bool HasUnsavedWork,
    bool IsQuitting);

public static class IdleSelfUpdateGate
{
    public const int RequiredIdleChecks = 2;

    public static bool ShouldInstall(IdleSelfUpdateSnapshot snapshot, ref int idleChecks)
    {
        if (!snapshot.IsEnabled
            || snapshot.IsBusy
            || snapshot.IsWindowVisible
            || snapshot.HasUnsavedWork
            || snapshot.IsQuitting)
        {
            idleChecks = 0;
            return false;
        }

        return ++idleChecks >= RequiredIdleChecks;
    }
}
