#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyElevationNativeLayoutTests
{
    [Fact]
    public void ByHandleFileInformation_MatchesWindowsAbi()
    {
        Assert.Equal(52, Marshal.SizeOf<PolicyElevationNative.ByHandleFileInformation>());
        Assert.Equal(
            0,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.dwFileAttributes)).ToInt32());
        Assert.Equal(
            4,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.ftCreationTime)).ToInt32());
        Assert.Equal(
            12,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.ftLastAccessTime)).ToInt32());
        Assert.Equal(
            20,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.ftLastWriteTime)).ToInt32());
        Assert.Equal(
            28,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.dwVolumeSerialNumber)).ToInt32());
        Assert.Equal(
            32,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.nFileSizeHigh)).ToInt32());
        Assert.Equal(
            36,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.nFileSizeLow)).ToInt32());
        Assert.Equal(
            40,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.nNumberOfLinks)).ToInt32());
        Assert.Equal(
            44,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.nFileIndexHigh)).ToInt32());
        Assert.Equal(
            48,
            Marshal.OffsetOf<PolicyElevationNative.ByHandleFileInformation>(
                nameof(PolicyElevationNative.ByHandleFileInformation.nFileIndexLow)).ToInt32());
    }

    [Fact]
    public void GetFileInformationByHandle_ReportsStableRealFileIdentity()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Assert.True(PolicyElevationNative.GetFileInformationByHandle(handle, out var first));
            Assert.True(PolicyElevationNative.GetFileInformationByHandle(handle, out var second));

            Assert.Equal(0u, first.nFileSizeHigh);
            Assert.Equal(5u, first.nFileSizeLow);
            Assert.NotEqual(
                (0u, 0u, 0u),
                (first.dwVolumeSerialNumber, first.nFileIndexHigh, first.nFileIndexLow));
            Assert.Equal(first.dwVolumeSerialNumber, second.dwVolumeSerialNumber);
            Assert.Equal(first.nFileIndexHigh, second.nFileIndexHigh);
            Assert.Equal(first.nFileIndexLow, second.nFileIndexLow);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
#endif
