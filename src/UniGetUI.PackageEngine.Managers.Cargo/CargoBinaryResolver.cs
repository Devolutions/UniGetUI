using System.Security.Cryptography;
using System.Text;
using Tomlyn.Model;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoBinaryPlan(
    string Id, string Version, string Target, string[] Binaries, Uri Url,
    string Format, string Hash, string? BinaryPath);

internal sealed record CargoRelease
{
    public string? tag_name { get; init; }
    public bool draft { get; init; }
    public CargoReleaseAsset[] assets { get; init; } = [];
}

internal sealed record CargoReleaseAsset
{
    public required string name { get; init; }
    public required string browser_download_url { get; init; }
    public string? digest { get; init; }
}

internal sealed class CargoBinaryResolver(CargoHttp http)
{
    internal static bool IsSupportedTarget(string target) =>
        (target.StartsWith("x86_64-", StringComparison.Ordinal)
            || target.StartsWith("aarch64-", StringComparison.Ordinal))
        && (target.EndsWith("-pc-windows-msvc", StringComparison.Ordinal)
            || target.EndsWith("-unknown-linux-gnu", StringComparison.Ordinal)
            || target.EndsWith("-unknown-linux-musl", StringComparison.Ordinal)
            || target.EndsWith("-apple-darwin", StringComparison.Ordinal));

    internal async Task<CargoBinaryPlan?> ResolveAsync(string id, string version, string target,
        CargoManifest manifest, Action<string> log, CancellationToken cancellationToken)
    {
        if (!IsSupportedTarget(target))
            return Fallback("The Cargo host target is not supported by the binary installer.", log);
        CargoManifestVersion selected = manifest.versions.Single(v => v.num == version);
        if (selected.bin_names is not { Length: > 0 } binaries)
            return Fallback("The published crate has no known executable names.", log);
        foreach (string binary in binaries)
            ValidateBinaryName(binary);
        Uri crateUrl = new($"https://crates.io/api/v1/crates/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/download");
        await CargoHttp.PaceApiAsync(cancellationToken);
        byte[] source = await http.GetAsync(crateUrl, CargoHttp.ArchiveLimit, false, cancellationToken)
            ?? throw new InvalidDataException("The published Cargo source archive is missing.");
        VerifyHash(source, "sha256:" + selected.checksum);
        string manifestPath = $"{id}-{version}/Cargo.toml";
        var files = CargoArchive.Read(source, "tgz", name => name == manifestPath, cancellationToken,
            CargoHttp.ManifestLimit);
        if (!files.TryGetValue(manifestPath, out byte[]? manifestBytes)
            || manifestBytes.Length > CargoHttp.ManifestLimit)
            throw new InvalidDataException("The Cargo source archive has no valid package manifest.");
        TomlTable document = CargoConfiguration.Parse(Encoding.UTF8.GetString(manifestBytes));
        TomlTable package = CargoConfiguration.Table(document, "package")
            ?? throw new InvalidDataException("Cargo manifest has no package table.");
        if (CargoConfiguration.String(package, "name") != id
            || CargoConfiguration.String(package, "version") != version)
            throw new InvalidDataException("Cargo source manifest does not match the selected package.");
        string? repository = CargoConfiguration.String(package, "repository");
        TomlTable? metadata = CargoConfiguration.Table(package, "metadata");
        TomlTable? binstall = metadata is null ? null : CargoConfiguration.Table(metadata, "binstall");
        if (binstall is not null)
        {
            if (binstall.ContainsKey("signing"))
                return Fallback("Publisher signing metadata requires an unsupported signature verifier.", log);
            if (binstall.TryGetValue("disabled-strategies", out object? disabled)
                && disabled is TomlArray strategies && strategies.OfType<string>().Contains("crate-meta-data"))
                return Fallback("The publisher disabled release-binary installation.", log);
            if (CargoConfiguration.Table(binstall, "overrides") is { } overrides)
            {
                if (overrides.Keys.Any(key => key.StartsWith("cfg(", StringComparison.Ordinal)))
                    return Fallback("The publisher uses unsupported cfg target overrides.", log);
                if (CargoConfiguration.Table(overrides, target) is { } targetOptions)
                {
                    var merged = new TomlTable();
                    foreach (var item in binstall)
                        merged[item.Key] = item.Value;
                    foreach (var item in targetOptions)
                        merged[item.Key] = item.Value;
                    binstall = merged;
                }
                if (binstall.ContainsKey("signing")
                    || binstall.Keys.Any(key => key is not ("pkg-url" or "pkg-fmt" or "bin-dir"
                        or "overrides" or "disabled-strategies")))
                    return Fallback("The publisher uses unsupported binary installation metadata.", log);
            }
        }

        string? template = binstall is null ? null : CargoConfiguration.String(binstall, "pkg-url");
        string? binaryPath = binstall is null ? null : CargoConfiguration.String(binstall, "bin-dir");
        string? configuredFormat = binstall is null ? null : CargoConfiguration.String(binstall, "pkg-fmt");
        if (template is not null)
        {
            if (!template.Replace(" ", "").Contains("{version}", StringComparison.Ordinal))
                return Fallback("The publisher binary URL is not pinned to the package version.", log);
            string? expanded = Expand(template, id, version, target, repository, configuredFormat, null);
            if (expanded is null || !Uri.TryCreate(expanded, UriKind.Absolute, out Uri? url)
                || url.Scheme != Uri.UriSchemeHttps)
                return Fallback("The publisher binary URL uses unsupported metadata.", log);
            string? format = configuredFormat ?? CargoArchive.Format(url.AbsolutePath);
            if (!SupportedFormat(format))
                return Fallback("The publisher binary archive format is unsupported.", log);
            string? hash = await ReadSidecarAsync(url, cancellationToken);
            return hash is null ? Fallback("The publisher binary has no supported checksum.", log)
                : new(id, version, target, binaries, url, format!, hash, binaryPath);
        }

        if (!TryGitHubRepository(repository, out string? ownerAndRepo))
            return Fallback("The publisher has no supported release repository or explicit binary URL.", log);
        foreach (string tag in new[] { $"v{version}", version })
        {
            Uri releaseUrl = new($"https://api.github.com/repos/{ownerAndRepo}/releases/tags/{Uri.EscapeDataString(tag)}");
            byte[]? json = await http.GetAsync(releaseUrl, CargoHttp.ManifestLimit, true, cancellationToken);
            if (json is null)
                continue;
            CargoRelease release = CargoJson.Deserialize<CargoRelease>(Encoding.UTF8.GetString(json))
                ?? throw new InvalidDataException("The publisher returned an empty release.");
            if (release.draft || release.tag_name != tag)
                throw new InvalidDataException("The publisher release does not match the selected version.");
            var assets = release.assets.Where(asset => MatchesAsset(asset.name, id, version, target, binaries)).ToArray();
            if (assets.Length > 1)
                return Fallback("Several publisher binaries match; refusing to guess.", log);
            if (assets.Length == 0)
                continue;
            var asset = assets[0];
            Uri assetUrl = new(asset.browser_download_url);
            string? hash = ValidHash(asset.digest) ? asset.digest : null;
            if (hash is null)
            {
                foreach (var checksum in release.assets.Where(a =>
                    a.name.Equals(asset.name + ".sha256", StringComparison.OrdinalIgnoreCase)
                    || a.name.Equals(asset.name + ".sha512", StringComparison.OrdinalIgnoreCase)
                    || a.name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase)
                    || a.name.Equals("SHA512SUMS", StringComparison.OrdinalIgnoreCase)
                    || a.name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[]? text = await http.GetAsync(new Uri(checksum.browser_download_url),
                        CargoHttp.ManifestLimit, false, cancellationToken);
                    hash = ParseChecksum(Encoding.UTF8.GetString(text!), asset.name);
                    if (hash is not null)
                        break;
                }
            }
            if (hash is null)
                hash = await ReadSidecarAsync(assetUrl, cancellationToken);
            return hash is null ? Fallback("The publisher binary has no supported checksum.", log)
                : new(id, version, target, binaries, assetUrl, CargoArchive.Format(asset.name)!, hash, binaryPath);
        }
        return Fallback("No compatible publisher binary is available for this version and target.", log);
    }

    private static CargoBinaryPlan? Fallback(string reason, Action<string> log)
    {
        log(reason + " Using Cargo to build from source.");
        return null;
    }

    internal static bool MatchesAsset(string name, string id, string version, string target, string[] binaries)
    {
        string? format = CargoArchive.Format(name);
        if (format is null)
            return false;
        int suffixLength = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? 7 : 4;
        string stem = name[..^suffixLength];
        foreach (string prefix in binaries.Append(id).Distinct(StringComparer.Ordinal))
        {
            foreach (string separator in new[] { "-", "_" })
            {
                foreach (string tag in new[] { version, "v" + version })
                {
                    if (stem == $"{prefix}{separator}{tag}{separator}{target}"
                        || stem == $"{prefix}{separator}{target}{separator}{tag}")
                        return true;
                }
            }
        }
        return false;
    }

    private static bool TryGitHubRepository(string? repository, out string? ownerAndRepo)
    {
        ownerAndRepo = null;
        if (!Uri.TryCreate(repository, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
            return false;
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            return false;
        string repo = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
        ownerAndRepo = parts[0] + "/" + repo;
        return true;
    }

    internal static string? Expand(string template, string id, string version, string target,
        string? repository, string? format, string? binary)
    {
        var variables = new Dictionary<string, string?>
        {
            ["name"] = id,
            ["version"] = version,
            ["target"] = target,
            ["repo"] = repository?.TrimEnd('/'),
            ["bin"] = binary,
            ["binary-ext"] = target.Contains("-windows-") ? ".exe" : "",
            ["archive-suffix"] = format is null ? null : format == "bin"
                ? target.Contains("-windows-", StringComparison.Ordinal) ? ".exe" : ""
                : format == "tgz" ? ".tar.gz" : "." + format,
            ["archive-format"] = format,
            ["format"] = format,
        };
        string result = template;
        foreach (var variable in variables)
        {
            if (variable.Value is null)
                continue;
            result = result.Replace("{ " + variable.Key + " }", variable.Value, StringComparison.Ordinal)
                .Replace("{" + variable.Key + "}", variable.Value, StringComparison.Ordinal);
        }
        return result.Contains('{') || result.Contains('}') ? null : result;
    }

    private static bool SupportedFormat(string? format) => format is "zip" or "tgz" or "tar" or "bin";

    private async Task<string?> ReadSidecarAsync(Uri url, CancellationToken cancellationToken)
    {
        foreach (string suffix in new[] { ".sha256", ".sha512" })
        {
            Uri checksumUrl = new(url.AbsoluteUri + suffix);
            byte[]? bytes = await http.GetAsync(checksumUrl, CargoHttp.ManifestLimit, true, cancellationToken);
            if (bytes is not null)
                return ParseChecksum(Encoding.UTF8.GetString(bytes), Path.GetFileName(url.AbsolutePath))
                    ?? throw new InvalidDataException("The published binary checksum file is invalid or ambiguous.");
        }
        return null;
    }

    internal static string? ParseChecksum(string text, string assetName)
    {
        List<string> matches = [];
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2)
                continue;
            string hash = parts[0];
            if (parts.Length == 2 && parts[1].TrimStart('*') != assetName)
                continue;
            string candidate = (hash.Length == 128 ? "sha512:" : "sha256:") + hash;
            if (ValidHash(candidate))
                matches.Add(candidate);
        }
        return matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? matches[0] : null;
    }

    internal static bool ValidHash(string? hash) =>
        hash is not null
        && ((hash.StartsWith("sha256:", StringComparison.Ordinal) && hash.Length == 71)
            || (hash.StartsWith("sha512:", StringComparison.Ordinal) && hash.Length == 135))
        && hash[7..].All(char.IsAsciiHexDigit);

    internal static void VerifyHash(byte[] bytes, string hash)
    {
        if (!ValidHash(hash))
            throw new InvalidDataException("The publisher checksum is invalid or unsupported.");
        byte[] actual = hash.StartsWith("sha256:", StringComparison.Ordinal)
            ? SHA256.HashData(bytes) : SHA512.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash[7..])))
            throw new InvalidDataException("Cargo download does not match its published checksum.");
    }

    internal static void ValidateBinaryName(string name)
    {
        if (name.Length == 0 || name.Length > 255
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || name is "." or ".." || name.EndsWith('.') || CargoArchive.IsDeviceName(name))
            throw new InvalidDataException($"Invalid Cargo executable name: {name}");
    }
}
