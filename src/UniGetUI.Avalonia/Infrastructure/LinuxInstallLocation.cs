using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

[SupportedOSPlatform("linux")]
internal static partial class LinuxInstallLocation
{
    private const int W_OK = 2;
    private const int EROFS = 30;

    public static bool IsWritable(string path)
    {
        try
        {
            return access(path, W_OK) == 0;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not check write access to '{path}': {ex.Message}");
            return false;
        }
    }

    public static bool IsOnReadOnlyFileSystem(string path)
    {
        try
        {
            return access(path, W_OK) != 0 && Marshal.GetLastPInvokeError() == EROFS;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not check whether '{path}' is on a read-only file system: {ex.Message}");
            return false;
        }
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int access(string path, int mode);
}
