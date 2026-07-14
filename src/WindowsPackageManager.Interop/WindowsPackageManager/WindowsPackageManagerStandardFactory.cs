// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.System.Com;
using WinRT;

namespace WindowsPackageManager.Interop;

[SupportedOSPlatform("windows5.0")]
public class WindowsPackageManagerStandardFactory : WindowsPackageManagerFactory
{
    public WindowsPackageManagerStandardFactory(
        ClsidContext clsidContext = ClsidContext.Prod,
        bool allowLowerTrustRegistration = false
    )
        : base(clsidContext, allowLowerTrustRegistration) { }

    protected override T CreateInstance<T>(Guid clsid, Guid iid)
    {
        CLSCTX clsctx = CLSCTX.CLSCTX_LOCAL_SERVER;
        if (_allowLowerTrustRegistration)
        {
            clsctx |= CLSCTX.CLSCTX_ALLOW_LOWER_TRUST_REGISTRATION;
        }

        int errorCode = CoCreateInstanceRaw(
            in clsid,
            IntPtr.Zero,
            (uint)clsctx,
            in iid,
            out IntPtr instance
        );

        if (errorCode < 0)
        {
            throw new WinGetComActivationException(
                clsid,
                iid,
                errorCode,
                _allowLowerTrustRegistration
            );
        }

        try
        {
            return MarshalGeneric<T>.FromAbi(instance);
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                Marshal.Release(instance);
            }
        }
    }

    [DllImport(
        "api-ms-win-core-com-l1-1-0.dll",
        EntryPoint = "CoCreateInstance",
        ExactSpelling = true,
        PreserveSig = true
    )]
    private static extern int CoCreateInstanceRaw(
        in Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid iid,
        out IntPtr instance
    );
}
