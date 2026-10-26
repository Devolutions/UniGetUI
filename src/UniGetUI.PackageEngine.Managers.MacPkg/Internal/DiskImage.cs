using System.Diagnostics;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// A .dmg holding an app to drag to /Applications, the way most vendors ship Mac apps, or an installer
/// package. It is mounted read-only and out of sight of Finder; what is at its root describes it.
/// </summary>
internal sealed class DiskImage : IDisposable
{
    private const string HdiUtilPath = "/usr/bin/hdiutil";
    private static readonly TimeSpan MountTimeout = TimeSpan.FromMinutes(2);

    public string MountPoint { get; }

    private DiskImage(string mountPoint) => MountPoint = mountPoint;

    public static DiskImage Mount(string image)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidDataException("Disk images can only be read on macOS.");
        // Created at once with mode 0700 (mkdtemp), so no other user can own or swap any folder on its path.
        string mountPoint = Directory.CreateTempSubdirectory("UniGetUI-dmg-").FullName;
        // "Y" accepts the license agreement some vendors put in front of their image. An image another
        // process has attached is "busy" until that process detaches it, so a few retries cover it.
        (int code, string output) = (0, "");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(TimeSpan.FromSeconds(1));
            (code, output) = Run(["attach", "-nobrowse", "-readonly", "-noverify", "-noautoopen",
                "-mountpoint", mountPoint, image], "Y\n");
            if (code == 0 || !output.Contains("Resource busy", StringComparison.OrdinalIgnoreCase))
                break;
        }
        if (code != 0)
        {
            TryDeleteDirectory(mountPoint);
            throw new InvalidDataException($"The disk image {image} could not be mounted: {output.Trim()}");
        }
        return new DiskImage(mountPoint);
    }

    public void Dispose()
    {
        var (code, output) = Run(["detach", MountPoint, "-force"], null);
        if (code != 0)
            Logger.Warn($"Could not detach the disk image at {MountPoint}: {output.Trim()}");
        TryDeleteDirectory(MountPoint);
    }

    /// <summary>
    /// What the image installs: one installer package, else one app, at its root. Links (such as the
    /// usual one to /Applications) and an uninstaller beside it are ignored.
    /// </summary>
    public (string Path, bool IsPackage) FindContent()
    {
        var root = new DirectoryInfo(MountPoint).EnumerateFileSystemInfos()
            .Where(i => i.LinkTarget is null && !i.Name.StartsWith('.'))
            .ToList();
        var packages = root.OfType<FileInfo>()
            .Where(f => f.Extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".mpkg", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FullName).ToList();
        var apps = root.OfType<DirectoryInfo>()
            .Where(d => d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.FullName).ToList();
        if (packages.Count + apps.Count > 1)
        {
            packages.RemoveAll(p => FeedFile.IsUninstaller(Path.GetFileName(p)));
            apps.RemoveAll(a => FeedFile.IsUninstaller(Path.GetFileName(a)));
        }
        if (packages.Count == 1)
            return (packages[0], true);
        if (packages.Count == 0 && apps.Count == 1)
            return (apps[0], false);
        throw new InvalidDataException(packages.Count + apps.Count == 0
            ? "The disk image has no app or installer package at its root."
            : "The disk image holds several apps or packages at its root: "
                + string.Join(", ", packages.Concat(apps).Select(Path.GetFileName)) + ".");
    }

    /// <summary>
    /// Reads what a disk image carries; with <paramref name="iconFile"/>, also saves its icon as a PNG
    /// (for a wrapped package, only when <paramref name="iconFromPayload"/> allows reading its payload).
    /// </summary>
    public static PkgMetadata ReadMetadata(string image, string? iconFile = null, bool iconFromPayload = true)
    {
        using var mounted = Mount(image);
        var (content, isPackage) = mounted.FindContent();
        if (isPackage)
            return PkgMetadata.Read(content, iconFromPayload ? iconFile : null) with { WrapsPackage = true };
        var metadata = FromApp(content);
        if (iconFile is not null)
            TryExtractIcon(content, iconFile);
        return metadata;
    }

    internal static PkgMetadata FromApp(string app)
    {
        var plist = AppBundle.ReadInfoPlist(app);
        return AppBundle.Describe(Path.GetFileName(app), plist, AppBundle.ReadArchitectures(app, plist));
    }

    // An app without an .icns (or naming one it lacks) falls back to any .icns it carries, or has none.
    private static void TryExtractIcon(string app, string destination)
    {
        try
        {
            string resources = Path.Combine(app, "Contents", "Resources");
            string? source = AppBundle.IconFileName(AppBundle.ReadInfoPlist(app)) is { } icon
                ? Path.Combine(resources, icon)
                : null;
            if (source is null || !File.Exists(source))
                source = Directory.Exists(resources) ? Directory.EnumerateFiles(resources, "*.icns").FirstOrDefault() : null;
            if (source is not null)
                AppBundle.ConvertIcon(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.Warn($"Could not extract the icon of {app}: {ex.Message}");
        }
    }

    private static (int, string) Run(string[] arguments, string? input)
    {
        var result = Tool.Run(HdiUtilPath, arguments, input is null ? null : System.Text.Encoding.UTF8.GetBytes(input), MountTimeout);
        return (result.ExitCode, result.Output);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
