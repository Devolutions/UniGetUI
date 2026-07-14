using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia.Controls;
using UniGetUI.Core.Logging;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal static partial class WindowsAvaloniaRenderingPolicy
{
    private static bool? _hasHardwareGpu;
    private static bool? _shouldUseSoftwareRendering;
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public static bool ShouldUseSoftwareRendering
    {
        get
        {
            if (_shouldUseSoftwareRendering is not null)
                return _shouldUseSoftwareRendering.Value;

            if (!OperatingSystem.IsWindows() || Design.IsDesignMode)
                return false;

            if (CoreSettings.Get(CoreSettings.K.DisableAutoSoftwareRenderingOnGpuLessHosts))
                return false;

            _shouldUseSoftwareRendering = !HasHardwareGpu;
            if (_shouldUseSoftwareRendering.Value)
            {
                Logger.Warn(
                    "No hardware GPU detected. Using Avalonia software rendering and reduced motion.");
            }

            return _shouldUseSoftwareRendering.Value;
        }
    }

    public static bool ShouldReduceMotion => ShouldUseSoftwareRendering;

    [SupportedOSPlatform("windows")]
    private static bool HasHardwareGpu
    {
        get
        {
            if (_hasHardwareGpu is not null)
                return _hasHardwareGpu.Value;

            Stopwatch stopwatch = Stopwatch.StartNew();
            _hasHardwareGpu = DetectHardwareGpu();
            stopwatch.Stop();

            Logger.Info(
                $"DXGI hardware GPU detection took {stopwatch.Elapsed.TotalMilliseconds:F1} ms; hardware GPU: {_hasHardwareGpu.Value}");

            return _hasHardwareGpu.Value;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool DetectHardwareGpu()
    {
        try
        {
            Guid factoryIid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref factoryIid, out nint nativeFactory) != HResult.Ok
                || nativeFactory == IntPtr.Zero)
            {
                Logger.Warn("Could not create DXGI factory; assuming a hardware GPU is present.");
                return true;
            }

            try
            {
                var factory = (IDXGIFactory1)ComWrappers.GetOrCreateObjectForComInstance(
                    nativeFactory,
                    CreateObjectFlags.None
                );
                for (uint i = 0; ; i++)
                {
                    int hr = factory.EnumAdapters1(i, out nint nativeAdapter);
                    if (hr == HResult.DxgiErrorNotFound || hr != HResult.Ok || nativeAdapter == IntPtr.Zero)
                        break;

                    try
                    {
                        var adapter = (IDXGIAdapter1)ComWrappers.GetOrCreateObjectForComInstance(
                            nativeAdapter,
                            CreateObjectFlags.None
                        );
                        unsafe
                        {
                            DXGI_ADAPTER_DESC1 desc = default;
                            if (adapter.GetDesc1((nint)(&desc)) != HResult.Ok)
                                continue;

                            bool isSoftwareAdapter =
                                (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                                || (desc.VendorId == MicrosoftVendorId
                                    && desc.DeviceId == BasicRenderDriverDeviceId);

                            if (!isSoftwareAdapter)
                            {
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(nativeAdapter);
                    }
                }

                return false;
            }
            finally
            {
                Marshal.Release(nativeFactory);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not detect DXGI hardware GPU; assuming one is present.");
            Logger.Warn(ex);
            return true;
        }
    }

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
    private static partial int CreateDXGIFactory1(
        ref Guid riid,
        out nint factory
    );

    private static class HResult
    {
        public const int Ok = 0;
        public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    }

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    // Microsoft Basic Render Driver (WARP) is enumerated with this VendorId/DeviceId pair.
    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDriverDeviceId = 0x8C;

    [GeneratedComInterface]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDXGIFactory1
    {
        [PreserveSig]
        int SetPrivateData(in Guid name, uint dataSize, nint data);

        [PreserveSig]
        int SetPrivateDataInterface(in Guid name, nint unknown);

        [PreserveSig]
        int GetPrivateData(in Guid name, ref uint dataSize, nint data);

        [PreserveSig]
        int GetParent(in Guid riid, out nint parent);

        [PreserveSig]
        int EnumAdapters(uint adapter, out nint adapterPointer);

        [PreserveSig]
        int MakeWindowAssociation(nint window, uint flags);

        [PreserveSig]
        int GetWindowAssociation(out nint window);

        [PreserveSig]
        int CreateSwapChain(nint device, nint description, out nint swapChain);

        [PreserveSig]
        int CreateSoftwareAdapter(nint module, out nint adapter);

        [PreserveSig]
        int EnumAdapters1(uint adapter, out nint adapterPointer);

        [PreserveSig]
        int IsCurrent();
    }

    [GeneratedComInterface]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDXGIAdapter1
    {
        [PreserveSig]
        int SetPrivateData(in Guid name, uint dataSize, nint data);

        [PreserveSig]
        int SetPrivateDataInterface(in Guid name, nint unknown);

        [PreserveSig]
        int GetPrivateData(in Guid name, ref uint dataSize, nint data);

        [PreserveSig]
        int GetParent(in Guid riid, out nint parent);

        [PreserveSig]
        int EnumOutputs(uint output, out nint outputPointer);

        [PreserveSig]
        int GetDesc(out nint description);

        [PreserveSig]
        int CheckInterfaceSupport(in Guid interfaceName, out long userModeVersion);

        [PreserveSig]
        int GetDesc1(nint description);
    }

    [StructLayout(LayoutKind.Explicit, Size = 312)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [FieldOffset(256)]
        public uint VendorId;

        [FieldOffset(260)]
        public uint DeviceId;

        [FieldOffset(264)]
        public uint SubSysId;

        [FieldOffset(268)]
        public uint Revision;

        [FieldOffset(272)]
        public nuint DedicatedVideoMemory;

        [FieldOffset(280)]
        public nuint DedicatedSystemMemory;

        [FieldOffset(288)]
        public nuint SharedSystemMemory;

        [FieldOffset(296)]
        public uint AdapterLuidLowPart;

        [FieldOffset(300)]
        public int AdapterLuidHighPart;

        [FieldOffset(304)]
        public uint Flags;
    }
}
