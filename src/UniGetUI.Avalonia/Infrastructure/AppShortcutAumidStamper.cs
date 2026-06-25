#if WINDOWS
using System;
using System.IO;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Stamps the UniGetUI <c>AppUserModelID</c> onto the Start Menu shortcut the installer
/// creates, which is the shell's "app is installed" signal required before Windows will
/// surface Action-Center toasts for an unpackaged Win32 app.
/// </summary>
/// <remarks>
/// Inno Setup cannot write shortcut property-store values directly, so the app performs
/// this idempotent fix-up on first launch (and on every launch; it is cheap). The AUMID
/// must match the one set on the process by <see cref="Win32ToastNotifier"/>.
/// </remarks>
internal static class AppShortcutAumidStamper
{
    private const string ShortcutName = "UniGetUI";
    private const string Aumid = Win32ToastNotifier.AppUserModelId;

    // System.AppUserModel.Id property key: {9F4C2855-9F79-4B39-A8D0-E1A8F39EFCDC}, pid 5
    private static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1A8F39EFCDC"), 5);

    public static void EnsureStamped()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            string? shortcutPath = ResolveStartMenuShortcut();
            if (shortcutPath is null || !File.Exists(shortcutPath))
                return;

            StampIfMissing(shortcutPath);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not stamp AUMID on Start Menu shortcut");
            Logger.Warn(ex);
        }
    }

    private static string? ResolveStartMenuShortcut()
    {
        // {autostartmenu} in Inno Setup resolves to the per-user Start Menu Programs folder.
        string baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.StartMenu, Environment.SpecialFolderOption.DoNotVerify);
        string userPath = Path.Combine(baseDir, "Programs", ShortcutName + ".lnk");
        if (File.Exists(userPath))
            return userPath;

        // Fallback to the common Start Menu Programs folder.
        string commonBase = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolderOption.DoNotVerify);
        string commonPath = Path.Combine(commonBase, "Programs", ShortcutName + ".lnk");
        return File.Exists(commonPath) ? commonPath : null;
    }

    private static void StampIfMissing(string shortcutPath)
    {
        // ShellLink (CLSID 00021401-0000-0000-C000-000000000046) implements IPersistFile and
        // IPropertyStore for .lnk files. We read the AppUserModel.Id value; if it already
        // matches, there's nothing to do, otherwise we set it and persist.
        var shellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046");
        object? linkObj = null;
        try
        {
            linkObj = Activator.CreateInstance(Type.GetTypeFromCLSID(shellLinkClsid)
                ?? throw new InvalidOperationException("ShellLink CLSID is not registered"));
            if (linkObj is not IPersistFile persistFile)
                return;

            persistFile.Load(shortcutPath, 0);

            if (linkObj is not IPropertyStore props)
                return;

            if (TryGetAumid(props, out string? existing) && existing == Aumid)
                return; // Already correct.

            var aumidValue = new PropVariant(Aumid);
            try
            {
                props.SetValue(AppUserModelIdKey, aumidValue);
                props.Commit();
                persistFile.Save(shortcutPath, fRemember: false);
            }
            finally
            {
                aumidValue.Clear();
            }
        }
        finally
        {
            if (linkObj is not null)
                Marshal.ReleaseComObject(linkObj);
        }
    }

    private static bool TryGetAumid(IPropertyStore props, out string? value)
    {
        value = null;
        try
        {
            uint count = props.GetCount();
            for (uint i = 0; i < count; i++)
            {
                props.GetAt(i, out PropertyKey key);
                if (key.fmtid == AppUserModelIdKey.fmtid && key.pid == AppUserModelIdKey.pid)
                {
                    var pv = new PropVariant();
                    try
                    {
                        props.GetValue(key, out pv);
                        value = pv.Value as string;
                        return true;
                    }
                    finally
                    {
                        pv.Clear();
                    }
                }
            }
        }
        catch
        {
            // Reading failed; fall through to write.
        }
        return false;
    }

    // ── COM interop ──────────────────────────────────────────────────────────

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal interface IPropertyStore
    {
        uint GetCount();
        void GetAt(uint i, out PropertyKey key);
        void GetValue([In] ref PropertyKey key, out PropVariant value);
        void SetValue([In] ref PropertyKey key, [In] ref PropVariant value);
        void Commit();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    // Minimal PropVariant supporting VT_LPWSTR (string) — the only type we set/read for AUMID.
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        private ushort vt;
        private readonly ushort reserved1;
        private readonly ushort reserved2;
        private readonly ushort reserved3;
        private IntPtr value1;
        private readonly IntPtr value2;

        public PropVariant(string value)
        {
            vt = (ushort)VarEnum.VT_LPWSTR;
            reserved1 = reserved2 = reserved3 = 0;
            value1 = Marshal.StringToCoTaskMemUni(value);
            value2 = IntPtr.Zero;
        }

        public string? Value
        {
            get
            {
                if ((VarEnum)vt != VarEnum.VT_LPWSTR || value1 == IntPtr.Zero)
                    return null;
                return Marshal.PtrToStringUni(value1);
            }
        }

        public void Clear()
        {
            if ((VarEnum)vt == VarEnum.VT_LPWSTR && value1 != IntPtr.Zero)
                Marshal.FreeCoTaskMem(value1);
            vt = 0;
            value1 = IntPtr.Zero;
        }
    }
}
#endif
