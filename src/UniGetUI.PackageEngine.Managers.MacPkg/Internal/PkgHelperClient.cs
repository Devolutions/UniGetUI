using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>
/// Asks the root helper to install a package. Only names are sent (catalog, package, version): the
/// helper finds, checks and installs the package itself, so a user can only install what IT published.
/// </summary>
internal sealed class PkgHelperRequest
{
    [JsonPropertyName("catalog")]
    public required string Catalog { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // Empty or absent for the newest; the source generator leaves an absent value null, not "".
    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>One line the helper sends back: a line of log, or the final result.</summary>
internal sealed class PkgHelperMessage
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Refused = "refused";

    [JsonPropertyName("log")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Log { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>
/// What a root process started behind the administrator prompt installs: the feed package as the user's
/// UniGetUI found it, staged and checked again as root, where the user's own processes cannot swap it.
/// </summary>
internal sealed class PkgElevatedInstall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("allowUnsigned")]
    public bool AllowUnsigned { get; init; }

    public static PkgElevatedInstall From(PkgFeedItem item, bool allowUnsigned) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Version = item.Version,
        Location = item.Location.AbsoluteUri,
        Sha256 = item.Sha256,
        Size = item.Size,
        AllowUnsigned = allowUnsigned,
    };

    public PkgFeedItem ToItem() => new(Id, Name ?? Id, Version, new Uri(Location), Sha256, Size, [], null);
}

[JsonSerializable(typeof(PkgHelperRequest))]
[JsonSerializable(typeof(PkgHelperMessage))]
[JsonSerializable(typeof(PkgElevatedInstall))]
internal partial class PkgHelperJsonContext : JsonSerializerContext;

internal static class PkgHelperClient
{
    internal const int MaxLineLength = 64 * 1024;

    /// <summary>Where the helper listens. Replaceable for tests.</summary>
    internal static string SocketPath { get; set; } = PkgHelperCommand.SocketPath;

    public static bool IsAvailable => File.Exists(SocketPath);

    /// <summary>
    /// The helper's verdict, or null when it cannot be reached or refuses (a feed it does not trust), so
    /// the caller falls back to the administrator prompt: refused only means "not without a password".
    /// </summary>
    public static async Task<OperationVeredict?> TryInstallAsync(string catalog, PkgFeedItem item, Action<string> log)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath));
        }
        catch (SocketException)
        {
            return null;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = new PkgHelperRequest { Catalog = catalog, Id = item.Id, Version = item.Version };
        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            FeedJson.Serialize(request, PkgHelperJsonContext.Default.PkgHelperRequest) + "\n"));
        log("Installing through the UniGetUI package helper, which needs no administrator password...");

        // Once sent, the install is not canceled: the helper runs it as root to the end, as a prompt would.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > MaxLineLength)
                break;
            if (Interpret(line, log) is not { } result)
                continue;
            return result switch
            {
                PkgHelperMessage.Success => OperationVeredict.Success,
                PkgHelperMessage.Refused => null,
                _ => OperationVeredict.Failure,
            };
        }
        log("The UniGetUI package helper ended the connection without a result.");
        return OperationVeredict.Failure;
    }

    /// <summary>Logs one line the helper sent, and gives its result when it is the final one.</summary>
    internal static string? Interpret(string line, Action<string> log)
    {
        PkgHelperMessage? message = null;
        try
        {
            if (line.StartsWith('{'))
                message = FeedJson.Deserialize(line, PkgHelperJsonContext.Default.PkgHelperMessage);
        }
        catch (JsonException) { }
        if (message is null)
        {
            // Not one of the helper's messages, such as a crash report: shown as is.
            log(line);
            return null;
        }
        if (message.Log is { } text)
            log(text);
        if (message.Result is not null && message.Message is { Length: > 0 } explanation)
            log(explanation);
        return message.Result;
    }
}
