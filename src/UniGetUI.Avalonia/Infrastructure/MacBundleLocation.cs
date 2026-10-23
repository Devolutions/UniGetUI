using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

[SupportedOSPlatform("macos")]
internal static partial class MacBundleLocation
{
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const int W_OK = 2;
    private const int EROFS = 30;
    private const int MaxPathBytes = 4096;

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

    public static bool IsOnReadOnlyVolume(string path)
    {
        try
        {
            return access(path, W_OK) != 0 && Marshal.GetLastPInvokeError() == EROFS;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not check whether '{path}' is on a read-only volume: {ex.Message}");
            return false;
        }
    }

    public static string? TryGetTranslocatedOriginalPath(string translocatedPath)
    {
        nint url = 0;
        nint original = 0;
        try
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(translocatedPath);
            url = CFURLCreateFromFileSystemRepresentation(0, pathBytes, pathBytes.Length, 1);
            if (url == 0)
            {
                return null;
            }

            original = SecTranslocateCreateOriginalPathForURL(url, 0);
            if (original == 0)
            {
                return null;
            }

            byte[] buffer = new byte[MaxPathBytes];
            if (CFURLGetFileSystemRepresentation(original, 1, buffer, buffer.Length) == 0)
            {
                return null;
            }

            int length = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not resolve the original location of translocated app '{translocatedPath}': {ex.Message}");
            return null;
        }
        finally
        {
            if (original != 0)
            {
                CFRelease(original);
            }

            if (url != 0)
            {
                CFRelease(url);
            }
        }
    }

    [LibraryImport(SystemLibrary, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int access(string path, int mode);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(
        nint allocator,
        byte[] buffer,
        nint bufferLength,
        byte isDirectory);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial byte CFURLGetFileSystemRepresentation(
        nint url,
        byte resolveAgainstBase,
        [Out] byte[] buffer,
        nint maxBufferLength);

    [LibraryImport(CoreFoundationLibrary)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(SecurityLibrary)]
    private static partial nint SecTranslocateCreateOriginalPathForURL(nint translocatedPath, nint error);
}
