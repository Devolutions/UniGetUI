using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record InstallerDetails(
    InstallerKind Kind,
    string? Publisher = null,
    string? ProductCode = null,
    string? UpgradeCode = null,
    string? UninstallKey = null,
    string? DisplayName = null,
    string? InstallArguments = null,
    string? UninstallArguments = null,
    string? Scope = null)
{
    public bool PerUser => Scope == UserScope;

    public bool PerMachine => Scope == MachineScope;

    internal const string UserScope = "user";
    internal const string MachineScope = "machine";

    public FeedIndexWindows ToIndex() => new()
    {
        Installer = InstallerKinds.Name(Kind),
        Publisher = Publisher,
        ProductCode = ProductCode,
        UpgradeCode = UpgradeCode,
        UninstallKey = UninstallKey,
        DisplayName = DisplayName,
        InstallArguments = InstallArguments,
        UninstallArguments = UninstallArguments,
        Scope = Scope,
    };

    public static InstallerDetails? FromIndex(FeedIndexWindows? index) =>
        index is null || InstallerKinds.Parse(index.Installer) is not { } kind
            ? null
            : new InstallerDetails(kind, Clean(index.Publisher), Guid(index.ProductCode), Guid(index.UpgradeCode),
                Clean(index.UninstallKey), Clean(index.DisplayName), Clean(index.InstallArguments), Clean(index.UninstallArguments),
                ParseScope(index.Scope));

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string? Guid(string? value) =>
        System.Guid.TryParse(value, out Guid guid) ? guid.ToString("B").ToUpperInvariant() : null;

    internal static string? ParseScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        UserScope => UserScope,
        MachineScope => MachineScope,
        _ => null,
    };
}

internal sealed record InstallerMetadata(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> Architectures,
    InstallerDetails Details,
    byte[]? Icon = null);

internal static partial class InstallerFile
{
    public static readonly string[] Extensions = [".exe", ".msi"];
    private const int MaxManifestBytes = 64 * 1024;

    public static bool IsMsi(string path) => Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase);

    public static bool IsUninstaller(string path) =>
        Path.GetFileName(path).Contains("uninst", StringComparison.OrdinalIgnoreCase);

    public static string ManifestPath(string file) => Path.ChangeExtension(file, ".json");

    public static InstallerMetadata Read(string file)
    {
        var manifest = ReadManifest(file);
        string stem = Path.GetFileNameWithoutExtension(file);
        return IsMsi(file)
            ? FromMsi(MsiDatabase.Read(file), manifest, stem)
            : FromExe(PeReader.Read(file), manifest, stem);
    }

    internal static InstallerManifest? ReadManifest(string file)
    {
        string path = ManifestPath(file);
        if (!File.Exists(path))
            return null;
        if (new FileInfo(path).Length > MaxManifestBytes)
            throw new InvalidDataException($"Its manifest {Path.GetFileName(path)} is larger than {MaxManifestBytes} bytes.");
        try
        {
            return FeedJson.Deserialize(File.ReadAllBytes(path), InstallerManifestJsonContext.Default.InstallerManifest);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Its manifest {Path.GetFileName(path)} is not valid: {ex.Message}");
        }
    }

    internal static InstallerMetadata FromExe(PeInfo pe, InstallerManifest? manifest, string stem)
    {
        var kind = InstallerKinds.Parse(manifest?.Installer) ?? InstallerKinds.Detect(pe);
        if (kind == InstallerKind.Msi)
            throw new InvalidDataException("An .exe cannot be installed as an .msi.");
        string name = InstallerDetails.Clean(manifest?.Name)
            ?? CleanName(pe.String("ProductName")) ?? CleanName(pe.String("FileDescription"), isProductName: false)
            ?? CleanName(stem, isProductName: false) ?? stem;
        string version = InstallerDetails.Clean(manifest?.Version)
            ?? CleanVersion(pe.String("ProductVersion")) ?? CleanVersion(pe.FixedProductVersion)
            ?? CleanVersion(pe.String("FileVersion")) ?? CleanVersion(pe.FixedFileVersion)
            ?? throw new InvalidDataException($"Its version resource gives no version. Write it as \"version\" in {stem}.json.");
        string arguments = InstallerDetails.Clean(manifest?.InstallArguments) ?? InstallerKinds.InstallArguments(kind)
            ?? throw new InvalidDataException($"UniGetUI does not know how to run this {InstallerKinds.Describe(kind)} silently. "
                + $"Write its silent switches as \"installArguments\" in {stem}.json.");
        IReadOnlyList<string> architectures = manifest?.Architectures is { } listed ? Architectures(listed)
            : pe.Machine switch
            {
                PeReader.MachineAmd64 => ["x64"],
                PeReader.MachineArm64 => ["arm64"],
                _ => [],
            };
        var details = new InstallerDetails(kind,
            InstallerDetails.Clean(manifest?.Publisher) ?? pe.String("CompanyName"),
            UpgradeCode: InstallerDetails.Guid(manifest?.UpgradeCode),
            UninstallKey: InstallerDetails.Clean(manifest?.UninstallKey),
            DisplayName: InstallerDetails.Clean(manifest?.DisplayName),
            InstallArguments: arguments,
            UninstallArguments: InstallerDetails.Clean(manifest?.UninstallArguments),
            Scope: InstallerDetails.ParseScope(manifest?.Scope));
        return new InstallerMetadata(InstallerDetails.Clean(manifest?.Id) ?? MakeId(name), name, version, architectures, details, pe.Icon);
    }

    internal static InstallerMetadata FromMsi(MsiInfo msi, InstallerManifest? manifest, string stem)
    {
        string? productCode = InstallerDetails.Guid(msi.Property("ProductCode"))
            ?? throw new InvalidDataException("It has no ProductCode.");
        if (msi.ExternalMedia is { } external)
            throw new InvalidDataException($"It installs files that are not inside it ({external}). Publish a single-file .msi, with its cabinets embedded.");
        string? upgradeCode = InstallerDetails.Guid(manifest?.UpgradeCode) ?? InstallerDetails.Guid(msi.Property("UpgradeCode"));
        string version = InstallerDetails.Clean(manifest?.Version) ?? msi.Property("ProductVersion")
            ?? throw new InvalidDataException("It has no ProductVersion.");
        string? productName = msi.Property("ProductName");
        string name = InstallerDetails.Clean(manifest?.Name) ?? CleanName(productName) ?? stem;
        string? scope = InstallerDetails.ParseScope(manifest?.Scope)
            ?? (msi.Property("ALLUSERS") is null || (msi.Property("ALLUSERS") == "2" && msi.Property("MSIINSTALLPERUSER") == "1")
                ? InstallerDetails.UserScope
                : msi.Property("ALLUSERS") == "1" ? InstallerDetails.MachineScope : null);
        var details = new InstallerDetails(InstallerKind.Msi,
            InstallerDetails.Clean(manifest?.Publisher) ?? msi.Property("Manufacturer"),
            productCode,
            upgradeCode,
            InstallerDetails.Clean(manifest?.UninstallKey),
            InstallerDetails.Clean(manifest?.DisplayName) ?? productName,
            InstallerDetails.Clean(manifest?.InstallArguments) ?? InstallerKinds.InstallArguments(InstallerKind.Msi),
            InstallerDetails.Clean(manifest?.UninstallArguments),
            scope);
        string id = InstallerDetails.Clean(manifest?.Id) ?? upgradeCode ?? productCode;
        IReadOnlyList<string> architectures = manifest?.Architectures is { } listed ? Architectures(listed)
            : TemplateArchitectures(msi.Template);
        return new InstallerMetadata(id, name, version, architectures, details, msi.Icon);
    }

    internal static IReadOnlyList<string> TemplateArchitectures(string? template)
    {
        string platform = (template ?? "").Split(';')[0].Trim();
        return platform.ToLowerInvariant() switch
        {
            "x64" or "amd64" => ["x64"],
            "arm64" => ["arm64"],
            "intel" or "" => ["x86"],
            _ => [platform.ToLowerInvariant()],
        };
    }

    internal static IReadOnlyList<string> Architectures(IEnumerable<string?> listed) =>
        listed.OfType<string>().Select(a => a.Trim().ToLowerInvariant() switch
        {
            "amd64" or "x86_64" => "x64",
            "aarch64" => "arm64",
            "i386" or "i686" => "x86",
            var other => other,
        }).Where(a => a.Length > 0).Distinct().ToArray();

    public static int ArchitectureFit(IReadOnlyList<string> architectures, Architecture machine)
    {
        if (architectures.Count == 0)
            return 1;
        int best = 0;
        foreach (string architecture in architectures)
        {
            int fit = architecture switch
            {
                "x86" => machine == Architecture.X86 ? 3 : machine is Architecture.X64 or Architecture.Arm64 ? 1 : 0,
                "x64" => machine == Architecture.X64 ? 3 : machine == Architecture.Arm64 ? 2 : 0,
                "arm64" => machine == Architecture.Arm64 ? 3 : 0,
                _ => 0,
            };
            best = Math.Max(best, fit);
        }
        return best;
    }

    [GeneratedRegex(@"^\s*v?(\d+(?:\s*[.,]\s*\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingVersion();

    internal static string? CleanVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || LeadingVersion().Match(text) is not { Success: true } match)
            return null;
        string version = string.Concat(match.Groups[1].Value.Where(c => !char.IsWhiteSpace(c))).Replace(',', '.');
        return version.Split('.').All(part => part.TrimStart('0').Length == 0) ? null : version;
    }

    [GeneratedRegex(@"(\s*\([^()]*\b(?:x64|x86|arm64|64-bit|32-bit|bit|edition)\b[^()]*\)|[\s._-]+(?:(?:version\s+\d+(?:\.\d+)*|v?\d+(?:\.\d+)+)[a-z]?|x64|x86|arm64|win(?:32|64)?|64-bit|32-bit))\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionOrArchitecture();

    [GeneratedRegex(@"[\s._-]+(?:setup|installer|install)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSetupWord();

    internal static string? CleanName(string? text, bool isProductName = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string name = text.Trim();
        for (string previous = ""; previous != name;)
        {
            previous = name;
            string shorter = TrailingVersionOrArchitecture().Replace(name, "").Trim();
            if (!isProductName)
                shorter = TrailingSetupWord().Replace(shorter, "").Trim();
            if (shorter.Length > 0)
                name = shorter;
        }
        return name.Length > 0 ? name : null;
    }

    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}+_-]+")]
    private static partial Regex IdSeparators();

    internal static string MakeId(string name)
    {
        string id = IdSeparators().Replace(name.Normalize(System.Text.NormalizationForm.FormC), ".").Trim('.', '-');
        return id.Length > 0 ? id : "package";
    }
}

internal sealed class InstallerManifest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("architectures")]
    public string?[]? Architectures { get; init; }

    [JsonPropertyName("installer")]
    public string? Installer { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("upgradeCode")]
    public string? UpgradeCode { get; init; }

    [JsonPropertyName("uninstallKey")]
    public string? UninstallKey { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("installArguments")]
    public string? InstallArguments { get; init; }

    [JsonPropertyName("uninstallArguments")]
    public string? UninstallArguments { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(InstallerManifest))]
internal partial class InstallerManifestJsonContext : JsonSerializerContext;
