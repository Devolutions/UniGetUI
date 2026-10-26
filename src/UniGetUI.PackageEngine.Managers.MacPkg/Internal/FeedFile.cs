namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>The kinds of file a feed can publish, told apart by their extension.</summary>
internal enum FeedFileKind
{
    /// <summary>A flat installer package (.pkg, .mpkg), installed with /usr/sbin/installer.</summary>
    Package,

    /// <summary>A disk image (.dmg) holding an app to copy, or an installer package.</summary>
    DiskImage,

    /// <summary>A zip archive (.zip) holding an app to copy.</summary>
    Archive,
}

internal static class FeedFile
{
    public static readonly string[] Extensions = [".pkg", ".mpkg", ".dmg", ".zip"];

    public static FeedFileKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dmg" => FeedFileKind.DiskImage,
        ".zip" => FeedFileKind.Archive,
        _ => FeedFileKind.Package,
    };

    // A vendor's uninstaller often ships beside the app or package; it is never the thing to install.
    public static bool IsUninstaller(string name) => name.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
}
