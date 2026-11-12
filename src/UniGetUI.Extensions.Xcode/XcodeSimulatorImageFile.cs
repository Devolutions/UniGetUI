using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.Extensions.Xcode;

internal static partial class XcodeSimulatorImageFile
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenNonBlocking = 0x00000004;
    private const int MacOpenCloseOnExec = 0x01000000;

    internal static bool IsValidBuild(string? build) => build is not null && BuildPattern().IsMatch(build);

    internal static async Task VerifyAsync(string path, byte[] expectedHash, CancellationToken cancellationToken)
    {
        await using FileStream copy = OpenWithoutFollowingLinks(path);
        if (!copy.CanSeek || copy.Length <= 0)
            throw new XcodesException("The copied simulator runtime disk image could not be verified.");
        byte[] actualHash = await SHA256.HashDataAsync(copy, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            throw new XcodesException("The copied simulator runtime disk image could not be verified.");
    }

    internal static FileStream OpenWithoutFollowingLinks(string path)
    {
        if (!OperatingSystem.IsMacOS())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);

        int descriptor = OpenMac(path, MacOpenNoFollow | MacOpenNonBlocking | MacOpenCloseOnExec);
        if (descriptor < 0)
            throw new IOException("Could not open the simulator runtime disk image without following links.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read, CopyBufferSize, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenMac(string path, int flags);

    [GeneratedRegex(@"\A[0-9]{1,4}[A-Za-z][A-Za-z0-9]{1,15}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();
}
