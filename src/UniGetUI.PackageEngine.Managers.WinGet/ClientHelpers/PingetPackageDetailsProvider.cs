using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Devolutions.Pinget.Core;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal interface IPingetPackageDetailsProvider
{
    bool LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger);
}

internal sealed class PingetCliPackageDetailsProvider(string cliExecutablePath)
    : IPingetPackageDetailsProvider
{
    public bool LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger)
    {
        try
        {
            PackageQuery query = PingetPackageDetailsProvider.CreateQuery(details.Package);
            logger.Log("Loading WinGet installer metadata with bundled Pinget CLI");
            logger.Log($" Query: {PingetPackageDetailsProvider.FormatQueryForLog(query)}");

            string output = RunShow(query, logger);
            ApplyManifestJson(details, output);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(
                "Could not load WinGet installer metadata with bundled Pinget CLI for package "
                    + $"{details.Package.Id}: {ex.Message}"
            );
            logger.Error(ex);
            return false;
        }
    }

    private string RunShow(PackageQuery query, INativeTaskLogger logger)
    {
        List<string?> sources = [query.Source];
        if (string.IsNullOrWhiteSpace(query.Source))
        {
            sources.AddRange(GetSourceNames(logger));
        }

        Exception? lastException = null;
        foreach (string? source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                logger.Log(
                    string.IsNullOrWhiteSpace(source)
                        ? " Running source-less Pinget CLI show query"
                        : $" Running Pinget CLI show query with source {source}"
                );
                return RunPinget(BuildShowArguments(query, source), logger);
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.Log(
                    string.IsNullOrWhiteSpace(source)
                        ? " Source-less Pinget CLI show query failed: " + ex.Message
                        : $" Pinget CLI show query failed for source {source}: {ex.Message}"
                );

                if (!string.IsNullOrWhiteSpace(query.Source))
                {
                    break;
                }
            }
        }

        throw lastException ?? new InvalidOperationException("Pinget CLI did not return package details.");
    }

    private IReadOnlyList<string> GetSourceNames(INativeTaskLogger logger)
    {
        try
        {
            string output = RunPinget(["source", "list", "--output", "json"], logger);
            using JsonDocument document = JsonDocument.Parse(output);
            if (
                !document.RootElement.TryGetProperty("sources", out JsonElement sources)
                || sources.ValueKind != JsonValueKind.Array
            )
            {
                return [];
            }

            return sources
                .EnumerateArray()
                .Select(source =>
                    source.TryGetProperty("name", out JsonElement name)
                        ? name.GetString()
                        : null
                )
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray()!;
        }
        catch (Exception ex)
        {
            logger.Log(" Could not list bundled Pinget CLI sources for retry: " + ex.Message);
            return [];
        }
    }

    private static IReadOnlyList<string> BuildShowArguments(PackageQuery query, string? source)
    {
        List<string> arguments = ["show"];
        if (!string.IsNullOrWhiteSpace(query.Id))
        {
            arguments.Add("--id");
            arguments.Add(query.Id);
        }
        else if (!string.IsNullOrWhiteSpace(query.Name))
        {
            arguments.Add("--name");
            arguments.Add(query.Name);
        }

        if (query.Exact)
        {
            arguments.Add("--exact");
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            arguments.Add("--source");
            arguments.Add(source);
        }

        arguments.Add("--accept-source-agreements");
        arguments.Add("--output");
        arguments.Add("json");
        return arguments;
    }

    private string RunPinget(IReadOnlyList<string> arguments, INativeTaskLogger logger)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (CoreTools.IsAdministrator())
        {
            string winGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
            logger.Log(
                $"[WARN] Redirecting %TEMP% folder to {winGetTemp}, since UniGetUI was run as admin"
            );
            process.StartInfo.Environment["TEMP"] = winGetTemp;
            process.StartInfo.Environment["TMP"] = winGetTemp;
        }

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string detail = string.Join(
                Environment.NewLine,
                new[] { error, output }.Where(text => !string.IsNullOrWhiteSpace(text))
            );
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"Pinget exited with code {process.ExitCode}."
                    : detail.Trim()
            );
        }

        return output;
    }

    private static void ApplyManifestJson(IPackageDetails details, string output)
    {
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement manifest = document.RootElement;
        JsonElement installer = default;
        bool hasInstaller =
            manifest.TryGetProperty("Installers", out JsonElement installers)
            && installers.ValueKind == JsonValueKind.Array
            && installers.GetArrayLength() > 0
            && (installer = installers[0]).ValueKind == JsonValueKind.Object;

        SetIfMissing(value => details.Author = value, details.Author, GetString(manifest, "Author"));
        SetIfMissing(
            value => details.Description = value,
            details.Description,
            GetString(manifest, "Description") ?? GetString(manifest, "ShortDescription")
        );
        SetIfMissing(value => details.License = value, details.License, GetString(manifest, "License"));
        SetIfMissing(
            value => details.Publisher = value,
            details.Publisher,
            GetString(manifest, "Publisher")
        );
        SetIfMissing(
            value => details.ReleaseNotes = value,
            details.ReleaseNotes,
            GetString(manifest, "ReleaseNotes")
        );

        SetUriIfMissing(uri => details.HomepageUrl = uri, details.HomepageUrl, GetString(manifest, "PackageUrl"));
        SetUriIfMissing(uri => details.LicenseUrl = uri, details.LicenseUrl, GetString(manifest, "LicenseUrl"));
        SetUriIfMissing(
            uri => details.ReleaseNotesUrl = uri,
            details.ReleaseNotesUrl,
            GetString(manifest, "ReleaseNotesUrl")
        );

        if (
            details.Tags.Length == 0
            && manifest.TryGetProperty("Tags", out JsonElement tags)
            && tags.ValueKind == JsonValueKind.Array
        )
        {
            details.Tags = tags
                .EnumerateArray()
                .Select(tag => tag.GetString())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .ToArray()!;
        }

        if (hasInstaller)
        {
            SetIfPresent(value => details.InstallerHash = value, GetString(installer, "InstallerSha256"));
            SetIfPresent(value => details.InstallerType = value, GetString(installer, "InstallerType"));
            SetIfPresent(value => details.UpdateDate = value, GetString(installer, "ReleaseDate"));

            if (TryCreateUri(GetString(installer, "InstallerUrl"), out Uri? installerUri))
            {
                details.InstallerUrl = installerUri;
                details.InstallerSize = CoreTools.GetFileSizeAsLong(installerUri);
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void SetIfMissing(
        Action<string> setValue,
        string? currentValue,
        string? newValue
    )
    {
        if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(newValue))
        {
            setValue(newValue);
        }
    }

    private static void SetIfPresent(Action<string> setValue, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            setValue(value);
        }
    }

    private static void SetUriIfMissing(
        Action<Uri> setValue,
        Uri? currentValue,
        string? newValue
    )
    {
        if (currentValue is null && TryCreateUri(newValue, out Uri? uri) && uri is not null)
        {
            setValue(uri);
        }
    }

    private static bool TryCreateUri([NotNullWhen(true)] string? value, out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri);
    }
}

internal sealed class PingetPackageDetailsProvider : IPingetPackageDetailsProvider
{
    private readonly Func<PackageQuery, ShowResult> _showPackage;
    private readonly Func<Uri, long> _installerSizeResolver;

    public PingetPackageDetailsProvider()
        : this(ShowWithRepository) { }

    internal PingetPackageDetailsProvider(
        Func<PackageQuery, ShowResult> showPackage,
        Func<Uri, long>? installerSizeResolver = null
    )
    {
        _showPackage = showPackage;
        _installerSizeResolver = installerSizeResolver ?? CoreTools.GetFileSizeAsLong;
    }

    public bool LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger)
    {
        try
        {
            PackageQuery query = CreateQuery(details.Package);
            logger.Log("Loading WinGet installer metadata with Pinget");
            logger.Log($" Query: {FormatQueryForLog(query)}");
            Logger.Info($"Loading WinGet installer metadata with Pinget. Query: {FormatQueryForLog(query)}");

            ShowResult result = _showPackage(query);

            foreach (string warning in result.Warnings)
            {
                logger.Log(" Pinget warning: " + warning);
            }

            ApplyShowResult(details, result, _installerSizeResolver);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(
                "Could not load WinGet installer metadata with Pinget for package "
                    + $"{details.Package.Id}: {ex.Message}"
            );
            logger.Error(ex);
            return false;
        }
    }

    internal static PackageQuery CreateQuery(IPackage package)
    {
        string id = package.Id.TrimEnd('…');
        string name = package.Name.TrimEnd('…');

        PackageQuery query;
        if (!string.IsNullOrWhiteSpace(id))
        {
            query = new PackageQuery
            {
                Id = id,
                Exact = !package.Id.EndsWith('…'),
            };
        }
        else
        {
            query = new PackageQuery
            {
                Name = name,
                Exact = !package.Name.EndsWith('…'),
            };
        }

        if (
            !package.Source.IsVirtualManager
            && !string.IsNullOrWhiteSpace(package.Source.Name)
            && !IsEllipsized(package.Source.Name)
        )
        {
            query = query with { Source = package.Source.Name };
        }

        return query;
    }

    internal static void ApplyShowResult(
        IPackageDetails details,
        ShowResult result,
        Func<Uri, long>? installerSizeResolver = null
    )
    {
        installerSizeResolver ??= CoreTools.GetFileSizeAsLong;
        Manifest manifest = result.Manifest;
        Installer? installer = result.SelectedInstaller ?? manifest.Installers.FirstOrDefault();

        SetIfMissing(value => details.Author = value, details.Author, manifest.Author);
        SetIfMissing(
            value => details.Description = value,
            details.Description,
            manifest.Description ?? manifest.ShortDescription
        );
        SetIfMissing(value => details.License = value, details.License, manifest.License);
        SetIfMissing(value => details.Publisher = value, details.Publisher, manifest.Publisher);
        SetIfMissing(value => details.ReleaseNotes = value, details.ReleaseNotes, manifest.ReleaseNotes);

        SetUriIfMissing(uri => details.HomepageUrl = uri, details.HomepageUrl, manifest.PackageUrl);
        SetUriIfMissing(uri => details.LicenseUrl = uri, details.LicenseUrl, manifest.LicenseUrl);
        SetUriIfMissing(
            uri => details.ReleaseNotesUrl = uri,
            details.ReleaseNotesUrl,
            manifest.ReleaseNotesUrl
        );

        if (details.Tags.Length == 0 && manifest.Tags.Count > 0)
        {
            details.Tags = manifest.Tags.ToArray();
        }

        if (installer is not null)
        {
            SetIfPresent(value => details.InstallerHash = value, installer.Sha256);
            SetIfPresent(value => details.InstallerType = value, installer.InstallerType);
            SetIfPresent(value => details.UpdateDate = value, installer.ReleaseDate);

            if (TryCreateUri(installer.Url, out Uri? installerUri))
            {
                details.InstallerUrl = installerUri;
                details.InstallerSize = installerSizeResolver(installerUri);
            }
        }

        details.Dependencies.Clear();
        foreach (string dependency in manifest.PackageDependencies.Concat(
                     installer?.PackageDependencies ?? []
                 ).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryCreateDependency(dependency, out IPackageDetails.Dependency parsedDependency))
            {
                details.Dependencies.Add(parsedDependency);
            }
        }
    }

    private static ShowResult ShowWithRepository(PackageQuery query)
    {
        using Repository repository = OpenRepository();
        return repository.ShowFirstMatchAcrossSources(query);
    }

    private static Repository OpenRepository()
    {
        return Repository.Open(CreateRepositoryOptions());
    }

    internal static RepositoryOptions CreateRepositoryOptions()
    {
        return new RepositoryOptions
        {
            UserAgent = CoreData.UserAgentString,
        };
    }

    internal static string FormatQueryForLog(PackageQuery query)
    {
        return string.Join(
            ", ",
            new[]
            {
                $"Id={query.Id}",
                $"Name={query.Name}",
                $"Source={query.Source}",
                $"Exact={query.Exact}",
            }.Where(part => !part.EndsWith("="))
        );
    }

    private static bool IsEllipsized(string value) => value.Contains('…');

    private static void SetIfMissing(
        Action<string> setValue,
        string? currentValue,
        string? newValue
    )
    {
        if (string.IsNullOrWhiteSpace(currentValue) && !string.IsNullOrWhiteSpace(newValue))
        {
            setValue(newValue);
        }
    }

    private static void SetIfPresent(Action<string> setValue, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            setValue(value);
        }
    }

    private static void SetUriIfMissing(Action<Uri> setValue, Uri? currentValue, string? value)
    {
        if (currentValue is null && TryCreateUri(value, out Uri? uri))
        {
            setValue(uri);
        }
    }

    private static bool TryCreateUri(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri);
    }

    private static bool TryCreateDependency(
        string value,
        out IPackageDetails.Dependency dependency
    )
    {
        dependency = default;
        string trimmedValue = value.Trim();
        if (trimmedValue == "")
            return false;

        string name = trimmedValue;
        string version = "";
        int versionStart = trimmedValue.IndexOf('[', StringComparison.Ordinal);
        if (versionStart >= 0)
        {
            name = trimmedValue[..versionStart].Trim();
            version = trimmedValue[(versionStart + 1)..].TrimEnd(']').Trim();
        }

        if (name.Contains(' '))
        {
            name = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        if (name == "")
            return false;

        dependency = new IPackageDetails.Dependency
        {
            Name = name,
            Version = version,
            Mandatory = true,
        };
        return true;
    }
}
