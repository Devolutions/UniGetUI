using System.Diagnostics;
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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly PingetCliJsonContext SerializationContext = new(SerializationOptions);

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

        List<Package> packages = [];
        foreach (ListMatch match in result.Matches.Where(match => match.AvailableVersion is not null))
        {
            var package = new Package(
                match.Name,
                match.Id,
                match.InstalledVersion,
                match.AvailableVersion!,
                GetSource(match.SourceName, match.Id),
                Manager
            );

            if (!WinGetPkgOperationHelper.UpdateAlreadyInstalled(package))
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

        return result
            .Matches.Select(match =>
                new Package(
                    match.Name,
                    match.Id,
                    match.InstalledVersion,
                    GetSource(match.SourceName, match.Id),
                    Manager
                )
            )
            .ToArray();
    }

    public IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        SearchResponse result = RunJson<SearchResponse>(
            LoggableTaskType.FindPackages,
            $"search {Quote(query)} --output json"
        );

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
            string winGetTemp = Path.Join(Path.GetTempPath(), "UniGetUI", "ElevatedWinGetTemp");
            logger.AddToStdErr(
                $"[WARN] Redirecting %TEMP% folder to {winGetTemp}, since UniGetUI was run as admin"
            );
            process.StartInfo.Environment["TEMP"] = winGetTemp;
            process.StartInfo.Environment["TMP"] = winGetTemp;
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
