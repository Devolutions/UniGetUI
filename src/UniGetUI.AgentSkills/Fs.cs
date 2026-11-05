// File-system helpers with Node semantics: readdir entries whose "directory"
// flag excludes links (Dirent.isDirectory), lstat-style checks, realpath,
// rm -rf that deletes links rather than their targets, and Windows junctions
// (what Node creates for `symlink(target, path, 'junction')`).

using System.Runtime.InteropServices;
using System.Text;

namespace Skills;

internal sealed record DirEntry(string Name, string FullPath, bool IsDirectory, bool IsFile, bool IsSymlink);

internal static partial class Fs
{
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget != null;
        }
        catch
        {
            return false;
        }
    }

    /// `fs.readdir(dir, { withFileTypes: true })`. Throws when the directory
    /// cannot be read.
    public static List<DirEntry> ReadDir(string dir)
    {
        var result = new List<DirEntry>();
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var link = IsLink(info);
            result.Add(new DirEntry(info.Name, NodePath.Join(dir, info.Name), !link && info is DirectoryInfo, !link && info is FileInfo, link));
        }
        if (!OperatingSystem.IsWindows()) result.Sort((a, b) => CompareNames(a.Name, b.Name));
        return result;
    }

    /// Node's readdir order: libuv sorts scandir results with strcmp on Unix
    /// (byte order of the UTF-8 names), while on Windows it returns the file
    /// system's order, which is what .NET enumerates. Sorts in place on Unix.
    public static void SortNamesLikeReaddir(List<string> names)
    {
        if (!OperatingSystem.IsWindows()) names.Sort(CompareNames);
    }

    private static int CompareNames(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    /// ReadDir, or an empty list when the directory is missing/unreadable.
    public static List<DirEntry> TryReadDir(string dir)
    {
        try
        {
            return ReadDir(dir);
        }
        catch
        {
            return [];
        }
    }

    /// `existsSync` (follows links).
    public static bool Exists(string p)
    {
        try
        {
            if (Directory.Exists(p)) return true;
            if (!File.Exists(p)) return false;
            var fi = new FileInfo(p);
            return fi.LinkTarget == null || fi.ResolveLinkTarget(true)?.Exists == true;
        }
        catch
        {
            return false;
        }
    }

    /// `stat(p).isDirectory()` (follows links).
    public static bool IsDir(string p)
    {
        try
        {
            var di = new DirectoryInfo(p);
            if (!di.Exists) return false;
            if (di.LinkTarget == null) return true;
            return di.ResolveLinkTarget(true) is DirectoryInfo { Exists: true };
        }
        catch
        {
            return false;
        }
    }

    /// `stat(p).isFile()` (follows links).
    public static bool IsFile(string p)
    {
        try
        {
            var fi = new FileInfo(p);
            if (!fi.Exists) return false;
            if (fi.LinkTarget == null) return true;
            return fi.ResolveLinkTarget(true) is FileInfo { Exists: true };
        }
        catch
        {
            return false;
        }
    }

    /// `lstat(p)` succeeds (the entry itself exists, even a broken link).
    public static bool LExists(string p)
    {
        try
        {
            File.GetAttributes(p);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// `lstat(p).isSymbolicLink()` (junctions count, as in Node on Windows).
    public static bool IsSymlink(string p)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            return IsLink(info);
        }
        catch
        {
            return false;
        }
    }

    /// `fs.readlink(p)`
    public static string? ReadLink(string p)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            var t = info.LinkTarget;
            if (t == null) return null;
            if (t.StartsWith(@"\??\")) t = t[4..];
            if (t.StartsWith(@"\\?\")) t = t[4..];
            return t;
        }
        catch
        {
            return null;
        }
    }

    /// `fs.realpath(p)`; null when the path does not exist.
    public static string? RealPath(string p)
    {
        var full = NodePath.Resolve(p);
        if (!LExists(full)) return null;
        var root = NodePath.Win ? full[..(full.IndexOf('\\') + 1)] : "/";
        var current = root;
        foreach (var part in full[root.Length..].Split(NodePath.Win ? '\\' : '/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = NodePath.Join(current, part);
            for (var hops = 0; hops < 40; hops++)
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                string? target;
                try
                {
                    target = info.LinkTarget;
                }
                catch
                {
                    target = null;
                }
                if (target == null) break;
                if (target.StartsWith(@"\??\")) target = target[4..];
                current = NodePath.Resolve(NodePath.Dirname(current), target);
            }
            if (!LExists(current)) return null;
        }
        return current;
    }

    /// `rm -rf`: tolerates missing paths and read-only files, and removes links
    /// (including junctions) without touching their targets.
    public static void RemoveAll(string p)
    {
        FileAttributes attrs;
        try
        {
            attrs = File.GetAttributes(p);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        var isDir = attrs.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = isDir ? new DirectoryInfo(p) : new FileInfo(p);
        if (IsLink(info) || !isDir)
        {
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
            if (isDir) Directory.Delete(p, false);
            else File.Delete(p);
            return;
        }
        foreach (var child in new DirectoryInfo(p).EnumerateFileSystemInfos()) RemoveAll(child.FullName);
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
        Directory.Delete(p, false);
    }

    /// Copy a file, preserving the permission bits on Unix (`chmod(dest, mode &amp; 0o777)`).
    public static void CopyFile(string src, string dest)
    {
        File.Copy(src, dest, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(src);
            File.SetUnixFileMode(dest, mode & (UnixFileMode)0x1FF);
        }
    }

    /// Create a directory link: a junction on Windows (absolute target), a
    /// relative symlink elsewhere.
    public static void CreateDirLink(string absoluteTarget, string linkPath, string relativeTarget)
    {
        if (OperatingSystem.IsWindows()) Junction.Create(absoluteTarget, linkPath);
        else File.CreateSymbolicLink(linkPath, relativeTarget);
    }

    private static partial class Junction
    {
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FsctlSetReparsePoint = 0x000900A4;
        private const uint IoReparseTagMountPoint = 0xA0000003;

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(IntPtr device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);

        public static void Create(string target, string link)
        {
            var full = Path.GetFullPath(target).TrimEnd('\\');
            Directory.CreateDirectory(link);
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + full);
            var print = Encoding.Unicode.GetBytes(full);
            var pathBuffer = new byte[substitute.Length + 2 + print.Length + 2];
            substitute.CopyTo(pathBuffer, 0);
            print.CopyTo(pathBuffer, substitute.Length + 2);
            var dataLength = 8 + pathBuffer.Length;
            var buf = new byte[8 + dataLength];
            BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buf, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buf, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, 8); // substitute offset
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buf, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buf, 12); // print offset
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buf, 14);
            pathBuffer.CopyTo(buf, 16);

            var handle = CreateFile(link, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle == new IntPtr(-1))
            {
                var err = Marshal.GetLastPInvokeError();
                Directory.Delete(link);
                throw new IOException($"Could not open {link} (error {err})");
            }
            try
            {
                if (!DeviceIoControl(handle, FsctlSetReparsePoint, buf, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    var err = Marshal.GetLastPInvokeError();
                    throw new IOException($"Could not create junction {link} (error {err})");
                }
            }
            catch
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
                try { Directory.Delete(link); } catch { /* best effort */ }
                throw;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }
    }
}
