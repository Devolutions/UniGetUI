using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.PackageEngine.Enums;

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

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";
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

[JsonSerializable(typeof(PkgHelperRequest))]
[JsonSerializable(typeof(PkgHelperMessage))]
internal partial class PkgHelperJsonContext : JsonSerializerContext;

internal static class PkgHelperClient
{
    internal const int MaxLineLength = 64 * 1024;

    /// <summary>Where the helper listens. Replaceable for tests.</summary>
    internal static string SocketPath { get; set; } = PkgHelperCommand.SocketPath;

    public static bool IsAvailable => File.Exists(SocketPath);

    /// <summary>The helper's verdict, or null when it cannot be reached, so the caller can fall back.</summary>
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
            JsonSerializer.Serialize(request, PkgHelperJsonContext.Default.PkgHelperRequest) + "\n"));
        log("Installing through the UniGetUI package helper, which needs no administrator password...");

        // Once sent, the install is not canceled: the helper runs it as root to the end, as a prompt would.
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > MaxLineLength)
                break;
            var message = JsonSerializer.Deserialize(line, PkgHelperJsonContext.Default.PkgHelperMessage);
            if (message?.Log is { } text)
                log(text);
            if (message?.Result is not { } result)
                continue;
            if (message.Message is { Length: > 0 } explanation)
                log(explanation);
            return result == PkgHelperMessage.Success ? OperationVeredict.Success : OperationVeredict.Failure;
        }
        log("The UniGetUI package helper ended the connection without a result.");
        return OperationVeredict.Failure;
    }
}
