using System.Diagnostics;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// A .dmg holding an app to drag to /Applications, the way most vendors ship Mac apps. It is
/// mounted read-only and out of sight of Finder; the app at its root describes the package.
/// </summary>
internal sealed class DiskImage : IDisposable
{
    private const string HdiUtilPath = "/usr/bin/hdiutil";
    private static readonly TimeSpan MountTimeout = TimeSpan.FromMinutes(2);

    public string MountPoint { get; }

    private DiskImage(string mountPoint) => MountPoint = mountPoint;

    public static bool IsDiskImage(string path) => path.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase);

    public static DiskImage Mount(string image)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidDataException("Disk images can only be read on macOS.");
        string mountPoint = Path.Combine(Path.GetTempPath(), "UniGetUI-dmg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mountPoint);
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

    /// <summary>The app at the image's root. Links (such as the usual one to /Applications) are ignored.</summary>
    public string FindApp()
    {
        var apps = new DirectoryInfo(MountPoint).EnumerateDirectories("*.app")
            .Where(d => d.LinkTarget is null)
            .Select(d => d.FullName)
            .ToList();
        if (apps.Count > 1)
            apps.RemoveAll(a => Path.GetFileName(a).Contains("uninstall", StringComparison.OrdinalIgnoreCase));
        return apps.Count switch
        {
            1 => apps[0],
            0 => throw new InvalidDataException("The disk image has no app at its root."),
            _ => throw new InvalidDataException("The disk image has several apps at its root: "
                + string.Join(", ", apps.Select(Path.GetFileName)) + "."),
        };
    }

    /// <summary>Reads the app a disk image carries; with <paramref name="iconFile"/>, also saves its icon as a PNG.</summary>
    public static PkgMetadata ReadMetadata(string image, string? iconFile = null)
    {
        using var mounted = Mount(image);
        string app = mounted.FindApp();
        var metadata = FromApp(app);
        if (iconFile is not null)
            TryExtractIcon(app, iconFile);
        return metadata;
    }

    internal static PkgMetadata FromApp(string app)
    {
        var plist = AppBundle.ReadInfoPlist(app);
        string id = plist.GetValueOrDefault("CFBundleIdentifier") ?? "";
        string? version = AppBundle.VersionOf(plist);
        if (id.Length == 0 || string.IsNullOrEmpty(version))
            throw new InvalidDataException($"{Path.GetFileName(app)} has no bundle identifier or version.");
        string name = Path.GetFileName(app);
        string? title = plist.GetValueOrDefault("CFBundleDisplayName") is { Length: > 0 } display ? display
            : Path.GetFileNameWithoutExtension(name);
        return new PkgMetadata(id, version, title, AppBundle.ReadArchitectures(app, plist),
            new AppBundleInfo(id, "/Applications/" + name, version));
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
