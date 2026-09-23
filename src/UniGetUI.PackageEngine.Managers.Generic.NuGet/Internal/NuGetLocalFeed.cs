using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml.Linq;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.Generic.NuGet.Internal
{
    internal readonly record struct LocalNuGetDependency(string Id, string Range);

    internal sealed class LocalNuGetPackage
    {
        public required string Id { get; init; }
        public required string Version { get; init; }
        public required string FilePath { get; init; }
        public required long Size { get; init; }
        public required DateTime LastWriteTimeUtc { get; init; }
        public required bool IsPreRelease { get; init; }
        public string? Title { get; init; }
        public string? Description { get; init; }
        public string? Summary { get; init; }
        public string? Authors { get; init; }
        public string? Owners { get; init; }
        public string? ProjectUrl { get; init; }
        public string? LicenseUrl { get; init; }
        public string? License { get; init; }
        public string? IconUrl { get; init; }
        public string? ReleaseNotes { get; init; }
        public string? Tags { get; init; }
        public IReadOnlyList<LocalNuGetDependency> Dependencies { get; init; } = [];
    }

    internal static class NuGetLocalFeed
    {
        private const int MaxRecursionDepth = 3;

        private readonly record struct CacheEntry(
            long Size,
            DateTime LastWriteTimeUtc,
            LocalNuGetPackage Package
        );

        private static readonly ConcurrentDictionary<string, CacheEntry> ParsedPackages =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool IsLocalSource(IManagerSource? source) => TryGetDirectory(source, out _);

        public static bool TryGetDirectory(IManagerSource? source, out string directory)
        {
            directory = string.Empty;

            if (source?.Url is not { IsAbsoluteUri: true, IsFile: true } url)
                return false;

            directory = url.LocalPath;
            return directory.Length > 0;
        }

        internal static void ClearCache() => ParsedPackages.Clear();

        public static IReadOnlyList<LocalNuGetPackage> Enumerate(string directory)
        {
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaxRecursionDepth,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            List<LocalNuGetPackage> packages = [];
            foreach (string file in Directory.EnumerateFiles(directory, "*.nupkg", options))
            {
                if (!Path.GetExtension(file).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Load(file) is { } package)
                    packages.Add(package);
            }

            return packages;
        }

        public static LocalNuGetPackage? Find(string directory, string packageId, string version)
        {
            LocalNuGetPackage? equivalent = null;
            SemanticVersion.TryParse(
                version,
                SemVerLabels.CaseInsensitive,
                out SemanticVersion wanted
            );

            foreach (LocalNuGetPackage package in Enumerate(directory))
            {
                if (!package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (package.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
                    return package;

                if (
                    equivalent is null
                    && wanted.IsValid
                    && SemanticVersion.TryParse(
                        package.Version,
                        SemVerLabels.CaseInsensitive,
                        out SemanticVersion parsed
                    )
                    && parsed == wanted
                )
                    equivalent = package;
            }

            return equivalent;
        }

        public static IReadOnlyList<string> GetVersionsDescending(
            string directory,
            string packageId
        )
        {
            List<(SemanticVersion Parsed, string Raw)> versions = [];
            HashSet<string> alreadyAdded = new(StringComparer.OrdinalIgnoreCase);

            foreach (LocalNuGetPackage package in Enumerate(directory))
            {
                if (!package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!alreadyAdded.Add(package.Version))
                    continue;

                versions.Add(
                    SemanticVersion.TryParse(
                        package.Version,
                        SemVerLabels.CaseInsensitive,
                        out SemanticVersion parsed
                    )
                        ? (parsed, package.Version)
                        : (SemanticVersion.Invalid(package.Version), package.Version)
                );
            }

            versions.Sort((left, right) => right.Parsed.CompareTo(left.Parsed));
            return versions.Select(entry => entry.Raw).ToArray();
        }

        public static bool MatchesQuery(LocalNuGetPackage package, string query, bool idOnly)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            string term = query.Trim();
            if (Contains(package.Id, term))
                return true;

            if (idOnly)
                return false;

            return Contains(package.Title, term)
                || Contains(package.Tags, term)
                || Contains(package.Summary, term)
                || Contains(package.Description, term)
                || Contains(package.Authors, term);
        }

        private static bool Contains(string? value, string term) =>
            value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

        private static LocalNuGetPackage? Load(string file)
        {
            long size;
            DateTime lastWriteTimeUtc;

            try
            {
                FileInfo info = new(file);
                size = info.Length;
                lastWriteTimeUtc = info.LastWriteTimeUtc;
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read the NuGet package file at {file}");
                Logger.Warn(e);
                return null;
            }

            if (
                ParsedPackages.TryGetValue(file, out CacheEntry cached)
                && cached.Size == size
                && cached.LastWriteTimeUtc == lastWriteTimeUtc
            )
                return cached.Package;

            try
            {
                using FileStream stream = File.OpenRead(file);
                using ZipArchive archive = new(stream, ZipArchiveMode.Read);

                ZipArchiveEntry? nuspec = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
                        continue;

                    if (!entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                        continue;

                    nuspec = entry;
                    break;
                }

                if (nuspec is null)
                {
                    Logger.Warn($"The NuGet package at {file} carries no .nuspec manifest");
                    return null;
                }

                using Stream manifest = nuspec.Open();
                LocalNuGetPackage? package = ParseNuspec(manifest, file, size, lastWriteTimeUtc);

                if (package is null)
                {
                    Logger.Warn(
                        $"The .nuspec manifest of the NuGet package at {file} declares no id or version"
                    );
                    return null;
                }

                ParsedPackages[file] = new CacheEntry(size, lastWriteTimeUtc, package);
                return package;
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read the NuGet package at {file}");
                Logger.Warn(e);
                return null;
            }
        }

        internal static LocalNuGetPackage? ParseNuspec(
            Stream manifest,
            string file,
            long size,
            DateTime lastWriteTimeUtc
        )
        {
            XElement? metadata = XDocument
                .Load(manifest)
                .Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName is "metadata");

            if (metadata is null)
                return null;

            string? id = Value(metadata, "id");
            string? version = Value(metadata, "version");

            if (id is null || version is null)
                return null;

            return new LocalNuGetPackage
            {
                Id = id,
                Version = version,
                FilePath = file,
                Size = size,
                LastWriteTimeUtc = lastWriteTimeUtc,
                IsPreRelease =
                    SemanticVersion.TryParse(
                        version,
                        SemVerLabels.CaseInsensitive,
                        out SemanticVersion parsed
                    ) && parsed.IsPreRelease,
                Title = Value(metadata, "title"),
                Description = Value(metadata, "description"),
                Summary = Value(metadata, "summary"),
                Authors = Value(metadata, "authors"),
                Owners = Value(metadata, "owners"),
                ProjectUrl = Value(metadata, "projectUrl"),
                LicenseUrl = Value(metadata, "licenseUrl"),
                License = ReadLicenseExpression(metadata),
                IconUrl = Value(metadata, "iconUrl"),
                ReleaseNotes = Value(metadata, "releaseNotes"),
                Tags = Value(metadata, "tags"),
                Dependencies = ReadDependencies(metadata),
            };
        }

        private static string? ReadLicenseExpression(XElement metadata)
        {
            XElement? license = metadata
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName is "license");

            if (license is null)
                return null;

            string? type = license.Attribute("type")?.Value;
            if (type is not null && !type.Equals("expression", StringComparison.OrdinalIgnoreCase))
                return null;

            string value = license.Value.Trim();
            return value.Length is 0 ? null : value;
        }

        private static IReadOnlyList<LocalNuGetDependency> ReadDependencies(XElement metadata)
        {
            XElement? dependencies = metadata
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName is "dependencies");

            if (dependencies is null)
                return [];

            List<LocalNuGetDependency> parsed = [];
            HashSet<string> alreadyAdded = new(StringComparer.OrdinalIgnoreCase);

            foreach (XElement dependency in dependencies.Descendants())
            {
                if (dependency.Name.LocalName is not "dependency")
                    continue;

                string? dependencyId = dependency.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(dependencyId) || !alreadyAdded.Add(dependencyId))
                    continue;

                parsed.Add(
                    new LocalNuGetDependency(
                        dependencyId,
                        dependency.Attribute("version")?.Value ?? string.Empty
                    )
                );
            }

            return parsed;
        }

        private static string? Value(XElement metadata, string name)
        {
            string? value = metadata
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == name)
                ?.Value.Trim();

            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
