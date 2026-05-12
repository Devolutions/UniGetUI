// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "WinGet COM projected activation types come from the Windows Package Manager WinMD and registered COM server; this path does not depend on app-owned trimmed constructors.")]
    protected override T CreateInstance<T>(Guid clsid, Guid iid)
    {
        if (!_allowLowerTrustRegistration)
        {
            Type? projectedType = Type.GetTypeFromCLSID(clsid);
            if (projectedType is null)
            {
                throw new WinGetComActivationException(
                    clsid,
                    iid,
                    unchecked((int)0x80040154),
                    _allowLowerTrustRegistration
                );
            }

            object? activatedInstance = Activator.CreateInstance(projectedType);
            if (activatedInstance is null)
            {
                throw new WinGetComActivationException(
                    clsid,
                    iid,
                    unchecked((int)0x80004003),
                    _allowLowerTrustRegistration
                );
            }

            IntPtr pointer = Marshal.GetIUnknownForObject(activatedInstance);
            return MarshalGeneric<T>.FromAbi(pointer);
        }

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

        return MarshalGeneric<T>.FromAbi(instance);
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
