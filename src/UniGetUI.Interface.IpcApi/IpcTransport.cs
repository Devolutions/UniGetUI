using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Interface;

public enum IpcTransportKind
{
    Tcp,
    NamedPipe,
}

public sealed record IpcTransportOptions(
    IpcTransportKind TransportKind,
    int TcpPort,
    string NamedPipeName
)
{
    public const string GuiSessionKind = "gui";
    public const string HeadlessSessionKind = "headless";
    public const int DefaultTcpPort = 7058;
    public const string DefaultNamedPipeName = "UniGetUI.IPC";
    public const string DefaultUnixSocketDirectory = "/tmp";
    internal const int MaxUnixSocketPathLength = 104;
    internal const UnixFileMode SameUserUnixSocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public const string TransportArgument = "--ipc-api-transport";
    public const string TcpPortArgument = "--ipc-api-port";
    public const string NamedPipeArgument = "--ipc-api-pipe-name";
    public const string CliTransportArgument = "--transport";
    public const string CliTcpPortArgument = "--tcp-port";
    public const string CliNamedPipeArgument = "--pipe-name";

    public const string TransportEnvironmentVariable = "UNIGETUI_IPC_API_TRANSPORT";
    public const string TcpPortEnvironmentVariable = "UNIGETUI_IPC_API_PORT";
    public const string NamedPipeEnvironmentVariable = "UNIGETUI_IPC_API_PIPE_NAME";

    private const string EndpointMetadataDirectoryName = "IpcApiEndpoints";

    public Uri BaseAddress =>
        TransportKind == IpcTransportKind.NamedPipe
            ? new Uri("http://localhost/")
            : new Uri($"http://localhost:{TcpPort}/");

    public string BaseAddressString => BaseAddress.ToString().TrimEnd('/');
    public string? NamedPipePath =>
        TransportKind == IpcTransportKind.NamedPipe && !OperatingSystem.IsWindows()
            ? ResolveUnixSocketPath(NamedPipeName)
            : null;
    public string NamedPipeDisplayName =>
        TransportKind != IpcTransportKind.NamedPipe
            ? BaseAddressString
            : OperatingSystem.IsWindows()
                ? NamedPipeName
                : NamedPipePath ?? NamedPipeName;

    public static IpcTransportOptions Default { get; } = new(
        IpcTransportKind.NamedPipe,
        DefaultTcpPort,
        DefaultNamedPipeName
    );

    public static string EndpointMetadataDirectoryPath =>
        Path.Join(CoreData.UniGetUIUserConfigurationDirectory, EndpointMetadataDirectoryName);

    public static IpcTransportOptions LoadForServer(IReadOnlyList<string>? args = null)
    {
        args ??= Environment.GetCommandLineArgs();
        return Parse(
            args,
            includeCliAliases: false,
            fallback: Default
        );
    }

    public static IpcTransportOptions LoadForClient(IReadOnlyList<string>? args = null)
    {
        args ??= Environment.GetCommandLineArgs();

        if (HasExplicitClientOverride(args))
        {
            return Parse(
                args,
                includeCliAliases: true,
                fallback: TryLoadPersisted()?.ToTransportOptions() ?? Default
            );
        }

        return TryLoadPersisted()?.ToTransportOptions() ?? Default;
    }

    public void Persist(string sessionId, string token, string sessionKind, int processId)
    {
        Directory.CreateDirectory(EndpointMetadataDirectoryPath);

        var metadata = new IpcEndpointRegistration
        {
            SessionId = sessionId,
            SessionKind = sessionKind,
            Token = token,
            ProcessId = processId,
            PersistedAtUtc = DateTimeOffset.UtcNow,
            Transport = TransportKind,
            TcpPort = TcpPort,
            NamedPipeName = NamedPipeName,
        };

        File.WriteAllText(
            GetEndpointMetadataPath(sessionId),
            JsonSerializer.Serialize(
                metadata,
                new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            )
        );
    }

    public static void DeletePersistedMetadata(string? sessionId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                if (Directory.Exists(EndpointMetadataDirectoryPath))
                {
                    Directory.Delete(EndpointMetadataDirectoryPath, recursive: true);
                }
                return;
            }

            string metadataPath = GetEndpointMetadataPath(sessionId);
            if (File.Exists(metadataPath))
            {
                File.Delete(metadataPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not delete IPC API endpoint metadata");
            Logger.Warn(ex);
        }
    }

    internal static IReadOnlyList<IpcEndpointRegistration> LoadPersistedRegistrations()
    {
        List<IpcEndpointRegistration> registrations = [];

        try
        {
            if (Directory.Exists(EndpointMetadataDirectoryPath))
            {
                foreach (string file in Directory.GetFiles(EndpointMetadataDirectoryPath, "*.json"))
                {
                    var registration = JsonSerializer.Deserialize<IpcEndpointRegistration>(
                        File.ReadAllText(file),
                        new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true,
                        }
                    );

                    if (registration is not null)
                    {
                        registrations.Add(registration);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not load persisted IPC API endpoint metadata");
            Logger.Warn(ex);
        }

        return registrations;
    }

    internal static IReadOnlyList<IpcEndpointRegistration> OrderRegistrationsForCliSelection(
        IReadOnlyList<IpcEndpointRegistration> registrations
    )
    {
        return registrations
            .OrderByDescending(registration => registration.SessionKind == HeadlessSessionKind)
            .ThenByDescending(registration => registration.PersistedAtUtc)
            .ThenBy(registration => registration.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IpcEndpointRegistration? FindRegistration(
        IpcTransportOptions options
    )
    {
        return LoadPersistedRegistrations()
            .Where(registration => registration.Matches(options))
            .OrderByDescending(registration => registration.PersistedAtUtc)
            .FirstOrDefault();
    }

    private static IpcEndpointRegistration? TryLoadPersisted()
    {
        return OrderRegistrationsForCliSelection(LoadPersistedRegistrations()).FirstOrDefault();
    }

    internal static bool HasExplicitClientOverride(IReadOnlyList<string> args)
    {
        return args.Contains(CliTransportArgument)
            || args.Contains(CliTcpPortArgument)
            || args.Contains(CliNamedPipeArgument)
            || args.Contains(TransportArgument)
            || args.Contains(TcpPortArgument)
            || args.Contains(NamedPipeArgument)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TransportEnvironmentVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TcpPortEnvironmentVariable))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NamedPipeEnvironmentVariable));
    }

    private static string GetEndpointMetadataPath(string sessionId)
    {
        string safeSessionId = string.Concat(
            sessionId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
        );
        if (string.IsNullOrWhiteSpace(safeSessionId))
        {
            safeSessionId = "session";
        }

        return Path.Join(EndpointMetadataDirectoryPath, safeSessionId + ".json");
    }

    private static IpcTransportOptions Parse(
        IReadOnlyList<string> args,
        bool includeCliAliases,
        IpcTransportOptions fallback
    )
    {
        string? transportValue = GetArgumentValue(
            args,
            includeCliAliases
                ? [CliTransportArgument, TransportArgument]
                : [TransportArgument]
        );
        transportValue ??= Environment.GetEnvironmentVariable(TransportEnvironmentVariable);

        string? portValue = GetArgumentValue(
            args,
            includeCliAliases
                ? [CliTcpPortArgument, TcpPortArgument]
                : [TcpPortArgument]
        );
        portValue ??= Environment.GetEnvironmentVariable(TcpPortEnvironmentVariable);

        string? pipeValue = GetArgumentValue(
            args,
            includeCliAliases
                ? [CliNamedPipeArgument, NamedPipeArgument]
                : [NamedPipeArgument]
        );
        pipeValue ??= Environment.GetEnvironmentVariable(NamedPipeEnvironmentVariable);

        var transport = ParseTransport(transportValue, fallback.TransportKind);
        int tcpPort = ParseTcpPort(portValue, fallback.TcpPort);
        string pipeName = ParseNamedPipeName(pipeValue, fallback.NamedPipeName);

        return new IpcTransportOptions(transport, tcpPort, pipeName);
    }

    private static IpcTransportKind ParseTransport(
        string? value,
        IpcTransportKind fallback
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "tcp" => IpcTransportKind.Tcp,
            "named-pipe" or "namedpipe" or "pipe" => IpcTransportKind.NamedPipe,
            _ =>
                LogInvalidTransport(value, fallback),
        };
    }

    private static IpcTransportKind LogInvalidTransport(
        string value,
        IpcTransportKind fallback
    )
    {
        Logger.Warn(
            $"Invalid IPC API transport \"{value}\". Falling back to {fallback}."
        );
        return fallback;
    }

    private static int ParseTcpPort(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, out int port) && port is > 0 and <= 65535)
        {
            return port;
        }

        Logger.Warn($"Invalid IPC API TCP port \"{value}\". Falling back to {fallback}.");
        return fallback;
    }

    private static string ParseNamedPipeName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string pipeName = value.Trim();
        if (pipeName.Length == 0)
        {
            Logger.Warn(
                $"Invalid IPC API named pipe name \"{value}\". Falling back to {fallback}."
            );
            return fallback;
        }

        if (OperatingSystem.IsWindows() && Path.IsPathRooted(pipeName))
        {
            Logger.Warn(
                $"Absolute IPC API named pipe paths are not supported on Windows. Falling back to {fallback}."
            );
            return fallback;
        }

        if (!OperatingSystem.IsWindows())
        {
            string resolvedPath = ResolveUnixSocketPath(pipeName);
            if (resolvedPath.Length > MaxUnixSocketPathLength)
            {
                Logger.Warn(
                    $"IPC API Unix socket path \"{resolvedPath}\" exceeds the supported length limit. Falling back to {fallback}."
                );
                return fallback;
            }
        }

        return pipeName;
    }

    internal static string ResolveUnixSocketPath(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        string trimmed = pipeName.Trim();
        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return $"{DefaultUnixSocketDirectory.TrimEnd('/')}/{trimmed.TrimStart('/')}";
    }

    private static string? GetArgumentValue(
        IReadOnlyList<string> args,
        IReadOnlyList<string> argumentNames
    )
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (!argumentNames.Contains(args[i]) || i + 1 >= args.Count)
            {
                continue;
            }

            return args[i + 1].Trim('"').Trim('\'');
        }

        return null;
    }
}

public sealed class IpcStatus
{
    public bool Running { get; set; }
    public string Transport { get; set; } = "tcp";
    public int TcpPort { get; set; }
    public string NamedPipeName { get; set; } = IpcTransportOptions.DefaultNamedPipeName;
    public string NamedPipePath { get; set; } = "";
    public string BaseAddress { get; set; } = "http://localhost:7058";
    public string Version { get; set; } = CoreData.VersionName;
    public int BuildNumber { get; set; } = CoreData.BuildNumber;
}

internal sealed class IpcEndpointRegistration
{
    public string SessionId { get; set; } = "";
    public string SessionKind { get; set; } = IpcTransportOptions.GuiSessionKind;
    public string Token { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTimeOffset PersistedAtUtc { get; set; }
    public IpcTransportKind Transport { get; set; } = IpcTransportKind.Tcp;
    public int TcpPort { get; set; } = IpcTransportOptions.DefaultTcpPort;
    public string NamedPipeName { get; set; } = IpcTransportOptions.DefaultNamedPipeName;

    public IpcTransportOptions ToTransportOptions()
    {
        return new IpcTransportOptions(Transport, TcpPort, NamedPipeName);
    }

    public bool Matches(IpcTransportOptions options)
    {
        return Transport == options.TransportKind
            && TcpPort == options.TcpPort
            && string.Equals(NamedPipeName, options.NamedPipeName, StringComparison.Ordinal);
    }
}
