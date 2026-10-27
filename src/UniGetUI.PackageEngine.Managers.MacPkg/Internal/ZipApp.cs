using System.IO.Compression;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// A .zip holding an app, as some vendors ship it (Visual Studio Code, for one). It is described from
/// its entries without being extracted; installing extracts it with ditto, which keeps symlinks.
/// </summary>
internal static class ZipApp
{
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private const int MaxAppDepth = 2;

    public static PkgMetadata ReadMetadata(string zip, string? iconFile = null)
    {
        using var archive = ZipFile.OpenRead(zip);
        string app = FindApp(archive.Entries.Select(e => e.FullName));
        var plist = AppBundle.ParsePlist(ReadEntry(archive, $"{app}/Contents/Info.plist")
            ?? throw new InvalidDataException($"{app} has no Info.plist."));

        IReadOnlyList<string> architectures = [];
        if (plist.GetValueOrDefault("CFBundleExecutable") is { Length: > 0 } executable && !executable.Contains('/')
            && archive.GetEntry($"{app}/Contents/MacOS/{executable}") is { } entry)
        {
            using var stream = entry.Open();
            byte[] header = new byte[8 + 20 * 32];
            architectures = AppBundle.ParseArchitectures(header.AsSpan(0, stream.ReadAtLeast(header, 8, throwOnEndOfStream: false)));
        }

        if (iconFile is not null)
            TryExtractIcon(archive, app, plist, iconFile);
        return AppBundle.Describe(Path.GetFileName(app), plist, architectures);
    }

    /// <summary>
    /// The app in a zip: the one folder named *.app with a Contents/Info.plist, at its root or one
    /// folder down. macOS's own __MACOSX metadata and an uninstaller beside the app are ignored.
    /// </summary>
    internal static string FindApp(IEnumerable<string> entryNames)
    {
        var apps = entryNames
            .Where(name => name.EndsWith("/Contents/Info.plist", StringComparison.Ordinal))
            .Select(name => name[..^"/Contents/Info.plist".Length])
            .Where(path => path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("__MACOSX", StringComparison.Ordinal)
                && path.Split('/') is { Length: <= MaxAppDepth } segments
                && segments.All(s => s.Length > 0 && s is not ("." or ".."))
                && segments[..^1].All(s => !s.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (apps.Count > 1)
            apps.RemoveAll(a => FeedFile.IsUninstaller(Path.GetFileName(a)));
        return apps.Count switch
        {
            1 => apps[0],
            0 => throw new InvalidDataException("The zip archive holds no app."),
            _ => throw new InvalidDataException("The zip archive holds several apps: " + string.Join(", ", apps) + "."),
        };
    }

    private static byte[]? ReadEntry(ZipArchive archive, string name)
    {
        if (archive.GetEntry(name) is not { } entry)
            return null;
        if (entry.Length > MaxEntryBytes)
            throw new InvalidDataException($"{name} in the zip archive is larger than {MaxEntryBytes} bytes.");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            // The declared length can lie, so the inflated size is checked as well.
            if (output.Length + read > MaxEntryBytes)
                throw new InvalidDataException($"{name} in the zip archive inflates beyond {MaxEntryBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void TryExtractIcon(ZipArchive archive, string app, IReadOnlyDictionary<string, string> plist,
        string destination)
    {
        string resources = $"{app}/Contents/Resources/";
        var icons = archive.Entries
            .Where(e => e.FullName.StartsWith(resources, StringComparison.Ordinal)
                && e.FullName.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)
                && !e.FullName[resources.Length..].Contains('/'))
            // A zip may repeat a name; the first entry wins, as it does for GetEntry.
            .GroupBy(e => e.FullName[resources.Length..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        string? named = AppBundle.IconFileName(plist);
        var entry = (named is not null ? icons.GetValueOrDefault(named) : null) ?? icons.Values.FirstOrDefault();
        if (entry is null)
            return;
        PkgPayload.SaveIcon(plist, new Dictionary<string, byte[]> { [entry.Name] = ReadEntry(archive, entry.FullName)! },
            destination);
    }
}
