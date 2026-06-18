using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Management.Deployment;
using Microsoft.Win32;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;

namespace UniGetUI.PackageEngine.Managers.WinGet.ClientHelpers;

internal static class WinGetIconsHelper
{
    private static readonly Dictionary<string, string> __msstore_package_manifests = [];

    public static string? GetMicrosoftStoreManifest(IPackage package)
    {
        if (__msstore_package_manifests.TryGetValue(package.Id, out var manifest))
            return manifest;

        string CountryCode = CultureInfo.CurrentCulture.Name.Split("-")[^1];
        string Locale = CultureInfo.CurrentCulture.Name;
        string url =
            $"https://storeedgefd.dsx.mp.microsoft.com/v8.0/sdk/products?market={CountryCode}&locale={Locale}&deviceFamily=Windows.Desktop";

#pragma warning disable SYSLIB0014
        var httpRequest = (HttpWebRequest)WebRequest.Create(url);
#pragma warning restore SYSLIB0014

        httpRequest.Method = "POST";
        httpRequest.ContentType = "application/json";

        string data = "{\"productIds\": \"" + package.Id.ToLower() + "\"}";

        using (StreamWriter streamWriter = new(httpRequest.GetRequestStream()))
            streamWriter.Write(data);

        var httpResponse = httpRequest.GetResponse() as HttpWebResponse;
        if (httpResponse is null)
        {
            Logger.Warn($"Null MS Store response for uri={url} and data={data}");
            return null;
        }

        string result;
        using (StreamReader streamReader = new(httpResponse.GetResponseStream()))
            result = streamReader.ReadToEnd();

        Logger.Debug("Microsoft Store API call status code: " + httpResponse.StatusCode);

        if (result != "" && httpResponse.StatusCode == HttpStatusCode.OK)
            __msstore_package_manifests[package.Id] = result;

        return result;
    }

    public static CacheableIcon? GetMicrosoftStoreIcon(IPackage package)
    {
        string? ResponseContent = GetMicrosoftStoreManifest(package);
        if (ResponseContent is null)
            return null;

        Match IconArray = Regex.Match(ResponseContent, "(?:\"|')Images(?:\"|'): ?\\[([^\\]]+)\\]");
        if (!IconArray.Success)
        {
            Logger.Warn("Could not parse Images array from Microsoft Store response");
            return null;
        }

        Dictionary<int, string> FoundIcons = [];

        foreach (Match ImageEntry in Regex.Matches(IconArray.Groups[1].Value, "{([^}]+)}"))
        {
            string CurrentImage = ImageEntry.Groups[1].Value;

            if (!ImageEntry.Success)
                continue;

            Match ImagePurpose = Regex.Match(
                CurrentImage,
                "(?:\"|')ImagePurpose(?:\"|'): ?(?:\"|')([^'\"]+)(?:\"|')"
            );
            if (!ImagePurpose.Success || ImagePurpose.Groups[1].Value != "Tile")
                continue;

            Match ImageUrl = Regex.Match(
                CurrentImage,
                "(?:\"|')Uri(?:\"|'): ?(?:\"|')([^'\"]+)(?:\"|')"
            );
            Match ImageSize = Regex.Match(CurrentImage, "(?:\"|')Height(?:\"|'): ?([^,]+)");

            if (!ImageUrl.Success || !ImageSize.Success)
                continue;

            FoundIcons[int.Parse(ImageSize.Groups[1].Value)] = ImageUrl.Groups[1].Value;
        }

        if (FoundIcons.Count == 0)
        {
            Logger.Warn(
                $"No Logo image found for package {package.Id} in Microsoft Store response"
            );
            return null;
        }

        Logger.Debug(
            "Choosing icon with size "
                + FoundIcons.Keys.Max()
                + " for package "
                + package.Id
                + " from Microsoft Store"
        );

        string uri = "https:" + FoundIcons[FoundIcons.Keys.Max()];

        return new CacheableIcon(new Uri(uri));
    }

    public static CacheableIcon? GetWinGetPackageIcon(IPackage package)
    {
        CatalogPackageMetadata? NativeDetails = NativePackageHandler.GetDetails(package);
        if (NativeDetails is null)
            return null;

        // Get the actual icon and return it
        foreach (Icon? icon in NativeDetails.Icons.ToArray())
            if (icon is not null && icon.Url is not null)
                // Logger.Debug($"Found WinGet native icon for {package.Id} with URL={icon.Url}");
                return new CacheableIcon(new Uri(icon.Url), icon.Sha256);

        // Logger.Debug($"Native WinGet icon for Package={package.Id} on catalog={package.Source.Name} was not found :(");
        return null;
    }

    public static CacheableIcon? GetAppxPackageIcon(IPackage package)
    {
        string appxId = package.Id.Replace("MSIX\\", "");
        string globalPath = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps",
            appxId
        );

        if (!Directory.Exists(globalPath))
            globalPath = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "SystemApps",
                appxId
            );

        if (!Directory.Exists(globalPath))
            return null;

        string content = File.ReadAllText(Path.Join(globalPath, "AppxManifest.xml"));
        Match? match = Regex.Match(content, "Square44x44Logo\\s*=\\s*[\"']([^\"']+)[\"']");
        if (!match.Success)
        {
            // There is no icon on the manifest
            return null;
        }

        string path = string.Join(
            '.',
            Path.Join(globalPath, match.Groups[1].ToString()).Split('.')[..^1]
        );
        foreach (
            string ending in new[]
            {
                ".png",
                ".scale-100.png",
                ".scale-125.png",
                ".scale-150.png",
                ".scale-175.png",
                ".scale-200.png",
            }
        )
            if (Path.Exists(path + ending))
            {
                return new CacheableIcon(path + ending);
            }

        return null;
    }

    public static CacheableIcon? GetARPPackageIcon(IPackage package)
    {
        var bits = package.Id.Split("\\");
        if (bits.Length < 4)
            return null;

        string regKey = "";
        regKey += bits[1] == "Machine" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        regKey += "\\SOFTWARE";
        if (bits[2] == "X86")
            regKey += "\\WOW6432Node";

        regKey += "\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\";
        regKey += bits[3];

        string? displayIcon = (string?)Registry.GetValue(regKey, "DisplayIcon", null);
        if (
            !string.IsNullOrEmpty(displayIcon)
            && File.Exists(displayIcon)
            && !displayIcon.EndsWith(".exe")
        )
            return new CacheableIcon(displayIcon);

        return null;
    }

    private const long MsixCacheTtlMs = 30_000;
    private static readonly Lock _msixLock = new();
    private static Dictionary<string, string>? _msixIndex; // normalized name -> install path
    private static long _msixBuiltAt = long.MinValue;

    /// <summary>
    /// Resolves an MSIX/Store package whose full name is encoded in the Id (Microsoft Store
    /// source). Cheap — no package enumeration.
    /// </summary>
    public static string? GetMsixLocationFromId(IPackage package)
    {
        if (!package.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
            return null;

        var fullName = package.Id["MSIX\\".Length..];
        foreach (
            var root in new[]
            {
                Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps",
                    fullName
                ),
                Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SystemApps",
                    fullName
                ),
            }
        )
            if (Directory.Exists(root))
                return root;

        return null;
    }

    /// <summary>
    /// Resolves an MSIX/Store package by matching it against the installed AppX packages. These do
    /// not appear in the "Add/Remove programs" registry, so this enumerates them via the platform
    /// API (cached). Used as a last resort because the enumeration has a cost.
    /// </summary>
    public static string? GetMsixLocationByName(IPackage package)
    {
        var index = GetMsixIndex();
        if (index.Count == 0)
            return null;

        string[] candidates = [NormalizeMsix(package.Id), NormalizeMsix(package.Name)];

        // Exact match first (most reliable).
        foreach (var candidate in candidates)
            if (candidate.Length >= 3 && index.TryGetValue(candidate, out var location))
                return location;

        // Then a length-guarded fuzzy match (prefix or containment) to bridge identity/display-name
        // differences, e.g. winget "Microsoft.AppInstaller" / "App Installer" vs the MSIX identity
        // "Microsoft.DesktopAppInstaller". Only reached as a last resort, over the small MSIX set.
        foreach (var candidate in candidates)
        {
            if (candidate.Length < 6)
                continue;
            foreach (var (key, location) in index)
                if (
                    key.StartsWith(candidate, StringComparison.Ordinal)
                    || candidate.StartsWith(key, StringComparison.Ordinal)
                    || key.Contains(candidate, StringComparison.Ordinal)
                )
                    return location;
        }

        return null;
    }

    private static Dictionary<string, string> GetMsixIndex()
    {
        lock (_msixLock)
        {
            var now = Environment.TickCount64;
            if (_msixIndex is null || now - _msixBuiltAt > MsixCacheTtlMs)
            {
                _msixIndex = BuildMsixIndex();
                _msixBuiltAt = now;
            }
            return _msixIndex;
        }
    }

    private const string AppxRepositoryKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static Dictionary<string, string> BuildMsixIndex()
    {
        // Read MSIX install locations straight from the AppModel repository registry rather than the
        // WinRT PackageManager API: the latter fails to activate inside this process (the WinGet COM
        // interop reconfigures process-wide COM security), whereas the registry is always readable.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(AppxRepositoryKey);
            if (root is null)
                return map;

            foreach (var packageFullName in root.GetSubKeyNames())
            {
                try
                {
                    using var entry = root.OpenSubKey(packageFullName);
                    if (entry?.GetValue("PackageRootFolder") is not string path)
                        continue;
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                        continue;

                    // Full name is "<Name>_<version>_<arch>__<publisherHash>".
                    AddMsixKey(map, packageFullName.Split('_')[0], path);

                    if (
                        entry.GetValue("DisplayName") is string displayName
                        && !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                    )
                        AddMsixKey(map, displayName, path);
                }
                catch
                {
                    // Skip unreadable entries.
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not read installed MSIX packages from the registry: {ex.Message}");
        }
        return map;
    }

    private static void AddMsixKey(Dictionary<string, string> map, string? key, string path)
    {
        var normalized = NormalizeMsix(key);
        if (normalized.Length >= 3 && !map.ContainsKey(normalized))
            map[normalized] = path;
    }

    private static string NormalizeMsix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
