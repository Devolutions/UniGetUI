using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.Managers.Generic.NuGet;

namespace UniGetUI.PackageEngine.Managers.DotNetManager;

internal sealed record NuGetConfiguredSource(string Key, Uri Url);

internal sealed class DotNetNuGetConfiguration
{
    private readonly IReadOnlyList<(string Path, DateTime? LastWriteTimeUtc)> _files;
    private readonly DateTime _loadedAtUtc;
    private DateTime _filesCheckedAtUtc;

    public DotNetNuGetConfiguration(
        IReadOnlyList<IManagerSource> sources,
        IReadOnlyList<NuGetConfiguredSource> configuredSources,
        NuGetSourceMapping mapping,
        IReadOnlyList<string> files,
        bool canBeReused
    )
    {
        Sources = sources;
        ConfiguredSources = configuredSources;
        Mapping = mapping;
        CanBeReused = canBeReused;
        _files = [.. files.Select(path => (path, LastWriteTime(path)))];
        _loadedAtUtc = DateTime.UtcNow;
        _filesCheckedAtUtc = _loadedAtUtc;
    }

    public IReadOnlyList<IManagerSource> Sources { get; }
    public IReadOnlyList<NuGetConfiguredSource> ConfiguredSources { get; }
    public NuGetSourceMapping Mapping { get; }
    public bool CanBeReused { get; }

    public bool IsCurrent(TimeSpan maxAge, TimeSpan fileCheckInterval)
    {
        DateTime now = DateTime.UtcNow;
        if (!CanBeReused || now - _loadedAtUtc >= maxAge)
            return false;

        if (now - _filesCheckedAtUtc < fileCheckInterval)
            return true;

        if (_files.Any(file => LastWriteTime(file.Path) != file.LastWriteTimeUtc))
            return false;

        _filesCheckedAtUtc = now;
        return true;
    }

    public bool Allows(string packageId, Uri sourceUrl)
    {
        if (!Mapping.IsEnabled)
            return true;

        IReadOnlySet<string> allowed = Mapping.SourcesFor(packageId);
        return ConfiguredSources.Any(source =>
            source.Url == sourceUrl && allowed.Contains(source.Key)
        );
    }

    private static DateTime? LastWriteTime(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
}

internal sealed partial class DotNetSourceHelper : ReadOnlySourceHelper
{
    private static readonly TimeSpan MaxConfigurationAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ConfigurationFileCheckInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _configurationLock = new();
    private DotNetNuGetConfiguration? _configuration;

    public DotNetSourceHelper(DotNet manager)
        : base(manager, "NuGet sources are read from the NuGet configuration.") { }

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() =>
        GetConfiguration().Sources;

    internal DotNetNuGetConfiguration GetConfiguration()
    {
        lock (_configurationLock)
        {
            if (_configuration is { } current && current.IsCurrent(MaxConfigurationAge, ConfigurationFileCheckInterval))
                return current;

            _configuration = LoadConfiguration();
            return _configuration;
        }
    }

    private DotNetNuGetConfiguration LoadConfiguration()
    {
        try
        {
            var (sourcesExitCode, sourceLines) = RunNuGet("list source --format Detailed");
            IReadOnlyList<NuGetConfiguredSource>? configured =
                sourcesExitCode is 0 ? ParseSources(sourceLines) : null;
            if (configured is null)
            {
                Logger.Warn(
                    $"Could not read the NuGet sources of {Manager.Name}, falling back to {Manager.Properties.DefaultSource.Name}"
                );
                return Fallback();
            }

            var (pathsExitCode, pathLines) = RunNuGet("config paths");
            IReadOnlyList<string> files =
                pathsExitCode is 0
                    ? [.. pathLines.Select(line => line.Trim()).Where(File.Exists)]
                    : [];
            NuGetConfigSettings settings = NuGetConfigFiles.Read(files);

            NuGetFeedCredentials.Replace(BuildCredentials(configured, settings));

            return new DotNetNuGetConfiguration(
                ToManagerSources(configured),
                configured,
                settings.Mapping,
                files,
                canBeReused: true
            );
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read the NuGet configuration of {Manager.Name}");
            Logger.Warn(ex);
            return Fallback();
        }
    }

    private DotNetNuGetConfiguration Fallback() =>
        new(
            [Manager.Properties.DefaultSource],
            [new NuGetConfiguredSource(Manager.Properties.DefaultSource.Name, Manager.Properties.DefaultSource.Url)],
            NuGetSourceMapping.None,
            [],
            canBeReused: false
        );

    internal IReadOnlyList<IManagerSource> ToManagerSources(
        IReadOnlyList<NuGetConfiguredSource> configured
    )
    {
        IManagerSource defaultSource = Manager.Properties.DefaultSource;
        List<IManagerSource> sources = [];
        foreach (NuGetConfiguredSource source in configured)
        {
            if (source.Url == defaultSource.Url)
            {
                if (!sources.Contains(defaultSource))
                    sources.Add(defaultSource);
            }
            else
            {
                sources.Add(new ManagerSource(Manager, source.Key, source.Url));
            }
        }

        return sources;
    }

    internal static IEnumerable<(Uri Source, AuthenticationHeaderValue Header)> BuildCredentials(
        IReadOnlyList<NuGetConfiguredSource> configured,
        NuGetConfigSettings settings
    )
    {
        foreach (NuGetConfiguredSource source in configured)
        {
            if (
                source.Url.Scheme is not ("http" or "https")
                || !settings.Credentials.TryGetValue(source.Key, out NuGetCredential? credential)
            )
                continue;

            string token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credential.Username}:{credential.Password}")
            );
            yield return (source.Url, new AuthenticationHeaderValue("Basic", token));
        }
    }

    private (int ExitCode, List<string> Lines) RunNuGet(string arguments)
    {
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Manager.Status.ExecutablePath,
                Arguments = "nuget " + arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                StandardOutputEncoding = Encoding.UTF8,
            },
        };
        p.StartInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.ListSources, p);
        p.Start();

        Task<string> stdErr = p.StandardError.ReadToEndAsync();
        List<string> lines = [];
        string? line;
        while ((line = p.StandardOutput.ReadLine()) is not null)
        {
            logger.AddToStdOut(line);
            lines.Add(line);
        }

        logger.AddToStdErr(stdErr.GetAwaiter().GetResult());
        p.WaitForExit();
        logger.Close(p.ExitCode);
        return (p.ExitCode, lines);
    }

    internal static IReadOnlyList<NuGetConfiguredSource>? ParseSources(IEnumerable<string> lines)
    {
        List<NuGetConfiguredSource> sources = [];
        string? pendingName = null;

        foreach (string line in lines)
        {
            Match header = SourceHeader().Match(line);
            if (header.Success)
            {
                pendingName = header.Groups["state"].Value == "Enabled"
                    ? header.Groups["name"].Value
                    : null;
                continue;
            }

            if (NumberedEntry().IsMatch(line))
                return null;

            if (pendingName is null || line.Trim().Length is 0)
                continue;

            if (Uri.TryCreate(line.Trim(), UriKind.Absolute, out Uri? url))
                sources.Add(new NuGetConfiguredSource(pendingName, url));

            pendingName = null;
        }

        return sources;
    }

    [GeneratedRegex(@"^\s*\d+\.\s+(?<name>.*\S)\s+\[(?<state>Enabled|Disabled)\]\s*$")]
    private static partial Regex SourceHeader();

    [GeneratedRegex(@"^\s*\d+\.\s+\S")]
    private static partial Regex NumberedEntry();
}
