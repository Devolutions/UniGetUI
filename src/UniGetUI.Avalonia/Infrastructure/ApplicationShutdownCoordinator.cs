namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class ApplicationShutdownCoordinator
{
    private int _isQuitting;
    private int _requestPending;

    public bool IsQuitting => Volatile.Read(ref _isQuitting) != 0;

    public async Task<bool> RequestAsync(
        Func<Task<bool>> authorizeShutdown,
        Func<Task> shutdown,
        Action? onAuthorized = null)
    {
        ArgumentNullException.ThrowIfNull(authorizeShutdown);
        ArgumentNullException.ThrowIfNull(shutdown);

        if (IsQuitting || Interlocked.Exchange(ref _requestPending, 1) != 0)
            return false;

        try
        {
            if (!await authorizeShutdown())
                return false;

            if (Interlocked.Exchange(ref _isQuitting, 1) != 0)
                return false;

            try
            {
                onAuthorized?.Invoke();
            }
            catch
            {
                Interlocked.Exchange(ref _isQuitting, 0);
                throw;
            }

            await shutdown();
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _requestPending, 0);
        }
    }
}
