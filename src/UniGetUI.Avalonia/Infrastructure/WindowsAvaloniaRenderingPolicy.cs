using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using UniGetUI.Core.Logging;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class WindowsAvaloniaRenderingPolicy
{
    private static bool? _hasHardwareGpu;
    private static bool? _shouldUseSoftwareRendering;

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
    [UnconditionalSuppressMessage("Trimming", "IL2050",
        Justification = "The DXGI COM interfaces are declared in full and referenced directly here, so their members are preserved by trimming.")]
    private static bool DetectHardwareGpu()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            Logger.Info(
                "Skipping DXGI hardware GPU detection because NativeAOT does not support legacy COM activation."
            );
            return true;
        }

        try
        {
            Guid factoryIid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref factoryIid, out object factoryObj) != HResult.Ok
                || factoryObj is not IDXGIFactory1 factory)
            {
                Logger.Warn("Could not create DXGI factory; assuming a hardware GPU is present.");
                return true;
            }

            try
            {
                for (uint i = 0; ; i++)
                {
                    int hr = factory.EnumAdapters1(i, out IDXGIAdapter1 adapter);
                    if (hr == HResult.DxgiErrorNotFound || hr != HResult.Ok || adapter is null)
                        break;

                    try
                    {
                        if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) != HResult.Ok)
                            continue;

                        bool isSoftwareAdapter =
                            (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0
                            || (desc.VendorId == MicrosoftVendorId
                                && desc.DeviceId == BasicRenderDriverDeviceId);

                        if (!isSoftwareAdapter)
                            return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not detect DXGI hardware GPU; assuming one is present.");
            Logger.Warn(ex);
            return true;
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory
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

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);

        [PreserveSig]
        bool IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public uint AdapterLuidLowPart;
        public int AdapterLuidHighPart;
        public uint Flags;
    }
}
