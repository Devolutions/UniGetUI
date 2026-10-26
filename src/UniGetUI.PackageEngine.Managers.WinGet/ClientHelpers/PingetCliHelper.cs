using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.Pinget.Core;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal sealed partial class PingetCliHelper : IWinGetManagerHelper
{
    private static readonly JsonSerializerOptions SerializationOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly PingetCliJsonContext SerializationContext = new(SerializationOptions);

    private const string MsResourcePrefix = "ms-resource:";
    private const string MsixIdPrefix = "MSIX\\";
    private static readonly ConcurrentDictionary<(string Identity, string Name), string> ResolvedDisplayNames = new();

    private readonly WinGet Manager;
    private readonly string _cliExecutablePath;
    private readonly IPingetPackageDetailsProvider _packageDetailsProvider;

    public PingetCliHelper(WinGet manager, string cliExecutablePath)
        : this(manager, cliExecutablePath, new PingetPackageDetailsProvider()) { }

    internal PingetCliHelper(
        WinGet manager,
        string cliExecutablePath,
        IPingetPackageDetailsProvider packageDetailsProvider
    )
    {
        Manager = manager;
        _cliExecutablePath = cliExecutablePath;
        _packageDetailsProvider = packageDetailsProvider;
    }

    public IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        ListResponse result = RunJson<ListResponse>(
            LoggableTaskType.ListUpdates,
            "upgrade --include-unknown --output json"
        );

        return BuildUpdatePackages(result);
    }

    internal IReadOnlyList<Package> BuildUpdatePackages(ListResponse result)
    {
        List<Package> packages = [];
        foreach (ListMatch match in result.Matches.Where(match => match.AvailableVersion is not null))
        {
            // Restore the version we last upgraded to when WinGet reports it as unknown (#5158).
            bool versionUnknown = WinGetPkgOperationHelper.IsUnknownVersion(match.InstalledVersion);
            var package = new Package(
                ResolveDisplayName(match),
                match.Id,
                versionUnknown
                    ? WinGetPkgOperationHelper.GetLastInstalledVersion(match.Id)
                    : match.InstalledVersion,
                match.AvailableVersion!,
                GetSource(match),
                Manager
            )
            {
                InstalledVersionIsUnverified = versionUnknown,
            };

            // Skip one-shot suppression for unknown versions so the restored mark isn't cleared.
            if (versionUnknown || !WinGetPkgOperationHelper.ConsumeAlreadyUpgradedSuppression(package))
            {
                packages.Add(package);
            }
            else
            {
                Logger.Warn(
                    $"WinGet package {package.Id} not being shown as an updated as this version has already been marked as installed"
                );
            }
        }

        return packages;
    }

    public IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        ListResponse result = RunJson<ListResponse>(
            LoggableTaskType.ListInstalledPackages,
            "list --output json"
        );

        return BuildInstalledPackages(result);
    }

    internal IReadOnlyList<Package> BuildInstalledPackages(ListResponse result)
    {
        return result
            .Matches.Select(match =>
                new Package(
                    ResolveDisplayName(match),
                    match.Id,
                    WinGetPkgOperationHelper.ResolveReportedInstalledVersion(match.Id, match.InstalledVersion),
                    GetSource(match),
                    Manager
                )
                {
                    InstalledVersionIsUnverified = WinGetPkgOperationHelper.IsUnknownVersion(
                        match.InstalledVersion
                    ),
                }
            )
            .ToArray();
    }

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        if (query.Length == 0)
        {
            return Manager
                .SourcesHelper.GetSources()
                .SelectMany(SearchSourceForBrowse)
                .ToArray();
        }

        return SearchPackages($"search {Quote(query)} --output json");
    }

    private Package[] SearchSourceForBrowse(IManagerSource source)
    {
        try
        {
            return SearchPackages(
                $"search {Quote(string.Empty)} --source {Quote(source.Name)} --count {SearchLimits.MaxEmptyQueryResultsPerSource} --output json"
            );
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not list the packages of the WinGet source {source.Name}: {ex.Message}");
            return [];
        }
    }

    private Package[] SearchPackages(string arguments)
    {
        SearchResponse result = RunJson<SearchResponse>(LoggableTaskType.FindPackages, arguments);

        return result
            .Matches.Select(match =>
                new Package(
                    match.Name,
                    match.Id,
                    match.Version ?? "Unknown",
                    GetSource(match.SourceName, match.Id),
                    Manager
                )
            )
            .ToArray();
    }

    public IReadOnlyList<IManagerSource> GetSources_UnSafe()
    {
        // pinget 0.4.1 dropped JSON support for `source list`; `source export` is the
        // equivalent JSON-emitting command (PascalCase keys, but case-insensitive matching
        // in SerializationOptions binds them to our records).
        PingetSourcesResponse result = RunJson<PingetSourcesResponse>(
            LoggableTaskType.ListSources,
            "source export --output json"
        );

        return result
            .Sources.Where(source => Uri.TryCreate(source.Arg, UriKind.Absolute, out _))
            .Select(source =>
                (IManagerSource)
                    new ManagerSource(Manager, source.Name, new Uri(source.Arg, UriKind.Absolute))
            )
            .ToArray();
    }

    public IReadOnlyList<string> GetInstallableVersions_Unsafe(IPackage package)
    {
        VersionsResult result = RunJson<VersionsResult>(
            LoggableTaskType.LoadPackageVersions,
            $"show {WinGetPkgOperationHelper.GetIdNamePiece(package)} --versions --output json"
        );

        return result
            .Versions.Select(version =>
                string.IsNullOrWhiteSpace(version.Channel)
                    ? version.Version
                    : $"{version.Version} [{version.Channel}]"
            )
            .ToArray();
    }

    public void GetPackageDetails_UnSafe(IPackageDetails details)
    {
        if (details.Package.Source.Name == "winget")
        {
            details.ManifestUrl = new Uri(
                "https://github.com/microsoft/winget-pkgs/tree/master/manifests/"
                    + details.Package.Id[0].ToString().ToLower()
                    + "/"
                    + details.Package.Id.Split('.')[0]
                    + "/"
                    + string.Join(
                        "/",
                        details.Package.Id.Contains('.')
                            ? details.Package.Id.Split('.')[1..]
                            : details.Package.Id.Split('.')
                    )
            );
        }
        else if (details.Package.Source.Name == "msstore")
        {
            details.ManifestUrl = new Uri("https://apps.microsoft.com/detail/" + details.Package.Id);
        }

        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageDetails);
        bool metadataLoaded = _packageDetailsProvider.LoadPackageDetails(details, logger);
        logger.Close(metadataLoaded ? 0 : 1);
    }

    private T RunJson<T>(LoggableTaskType taskType, string arguments)
    {
        using var _cliLock = WinGet.AcquireCliLock();
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _cliExecutablePath,
                Arguments = Manager.Status.ExecutableCallArgs + " " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(taskType, process);

        if (CoreTools.IsAdministrator())
        {
            string winGetTemp = AppPaths.RedirectTempToElevatedDirectory(process.StartInfo);
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {winGetTemp}, since UniGetUI was run as admin"
            );
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = stdoutTask.GetAwaiter().GetResult();
        string error = stderrTask.GetAwaiter().GetResult();

        logger.AddToStdOut(output.Split(Environment.NewLine));
        logger.AddToStdErr(error.Split(Environment.NewLine));
        logger.Close(process.ExitCode);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Pinget exited with code {process.ExitCode}: {error.Trim()}"
            );
        }

        return DeserializeJson<T>(output);
    }

    internal static T DeserializeJson<T>(string output)
    {
        return JsonSerializer.Deserialize(output, typeof(T), SerializationContext) is T result
            ? result
            : throw new InvalidOperationException("Pinget returned empty JSON output.");
    }

    internal static string? InferSourceName(ListMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.SourceName))
        {
            return match.SourceName;
        }

        if (match.Id.Contains("_Microsoft.Winget.Source_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
        {
            return "winget";
        }

        if (!string.IsNullOrWhiteSpace(match.InstallLocation)
            && match.InstallLocation.Contains("\\Microsoft\\WinGet\\Packages\\", StringComparison.OrdinalIgnoreCase))
        {
            return "winget";
        }

        return null;
    }

    internal static string ResolveDisplayName(ListMatch match) =>
        ResolveDisplayName(match, LoadIndirectString, ResolvedDisplayNames);

    internal static string ResolveDisplayName(
        ListMatch match,
        Func<string, string?> loadIndirectString,
        ConcurrentDictionary<(string Identity, string Name), string> cache
    )
    {
        if (!match.Name.StartsWith(MsResourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return match.Name;
        }

        string? packageFullName = GetMsixPackageFullName(match.LocalId) ?? GetMsixPackageFullName(match.Id);
        if (packageFullName is null)
        {
            return cache.GetOrAdd((match.Id, match.Name), static key => GetNonMsixFallbackName(key.Identity, key.Name));
        }

        return cache.GetOrAdd(
            (packageFullName, match.Name),
            static (key, loader) => ResolveMsixDisplayName(key.Identity, key.Name, loader),
            loadIndirectString
        );
    }

    private static string ResolveMsixDisplayName(
        string packageFullName,
        string name,
        Func<string, string?> loadIndirectString
    )
    {
        string packageName = packageFullName.Split('_')[0];
        foreach (string reference in GetResourceReferences(name, packageName))
        {
            string? resolved = loadIndirectString($"@{{{packageFullName}?{reference}}}");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        Logger.Warn(
            $"Could not resolve the display name {name} of the MSIX package {packageFullName}; showing {packageName} instead"
        );
        return packageName;
    }

    private static string GetNonMsixFallbackName(string id, string name)
    {
        if (id.Contains('\\'))
        {
            Logger.Warn($"Could not resolve the display name {name} of {id}, which is not an MSIX package");
            return name;
        }

        Logger.Warn($"Could not resolve the display name {name} of {id}, which is not an MSIX package; showing its id instead");
        return id;
    }

    private static string? GetMsixPackageFullName(string? id)
    {
        if (id is null || !id.StartsWith(MsixIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string packageFullName = id[MsixIdPrefix.Length..];
        return packageFullName.Contains('_') ? packageFullName : null;
    }

    private static IEnumerable<string> GetResourceReferences(string name, string packageName)
    {
        string reference = name[MsResourcePrefix.Length..];
        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            yield return name;
        }
        else if (reference.StartsWith('/'))
        {
            yield return $"{MsResourcePrefix}//{packageName}{reference}";
        }
        else
        {
            yield return $"{MsResourcePrefix}//{packageName}/Resources/{reference}";
            yield return $"{MsResourcePrefix}//{packageName}/{reference}";
        }
    }

    private static string? LoadIndirectString(string source)
    {
        Span<char> buffer = stackalloc char[1024];
        int result = SHLoadIndirectString(source, ref buffer[0], (uint)buffer.Length, 0);
        if (result != 0)
        {
            return null;
        }

        int end = buffer.IndexOf('\0');
        return end > 0 ? new string(buffer[..end]) : null;
    }

    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, ref char output, uint outputLength, nint reserved);

    private IManagerSource GetSource(ListMatch match)
    {
        return GetSource(InferSourceName(match), match.Id);
    }

    private IManagerSource GetSource(string? sourceName, string packageId)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return Manager.GetLocalSource(packageId);
        }

        return Manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed record PingetSourcesResponse(List<PingetSourceRecord> Sources);

    private sealed record PingetSourceRecord(string Name, string Arg);

    [JsonSerializable(typeof(ListResponse))]
    [JsonSerializable(typeof(SearchResponse))]
    [JsonSerializable(typeof(VersionsResult))]
    [JsonSerializable(typeof(PingetSourcesResponse))]
    private sealed partial class PingetCliJsonContext : JsonSerializerContext { }
}
