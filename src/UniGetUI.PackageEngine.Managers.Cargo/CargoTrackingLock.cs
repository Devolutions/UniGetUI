using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed partial class CargoTrackingLock : IDisposable
{
    internal FileStream V1 { get; }
    internal FileStream V2 { get; }

    private CargoTrackingLock(FileStream v1, FileStream v2) => (V1, V2) = (v1, v2);

    internal static async Task<CargoTrackingLock> AcquireAsync(string root, CancellationToken cancellationToken)
    {
        CargoConfiguration.RequireRegularPath(root);
        Directory.CreateDirectory(root);
        FileStream v1 = await OpenAsync(Path.Join(root, ".crates.toml"), cancellationToken);
        try
        {
            FileStream v2 = await OpenAsync(Path.Join(root, ".crates2.json"), cancellationToken);
            return new(v1, v2);
        }
        catch
        {
            Unlock(v1);
            throw;
        }
    }

    private static async Task<FileStream> OpenAsync(string path, CancellationToken cancellationToken)
    {
        CargoConfiguration.RequireRegularPath(path);
        FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        stream.Lock(0, long.MaxValue);
                        return stream;
                    }
                    catch (IOException ex) when ((ex.HResult & 0xFFFF) == 33)
                    { }
                }
                else
                {
                    if (Flock(stream.SafeFileHandle, 2 | 4) == 0)
                        return stream;
                    int error = Marshal.GetLastPInvokeError();
                    if (error is not (11 or 35 or 4))
                        throw new IOException("Unable to lock Cargo tracking metadata.", new Win32Exception(error));
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void Unlock(FileStream stream)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                stream.Unlock(0, long.MaxValue);
            else if (Flock(stream.SafeFileHandle, 8) != 0)
                throw new IOException("Unable to unlock Cargo tracking metadata.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally
        {
            stream.Dispose();
        }
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle file, int operation);

    public void Dispose()
    {
        try { Unlock(V2); }
        finally { Unlock(V1); }
    }
}
