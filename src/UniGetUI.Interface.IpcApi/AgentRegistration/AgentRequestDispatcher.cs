using System.Buffers;
using System.Text.Json;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// Answers registration requests. Every answer is built from fixed strings and the backend's
/// read-only inventory, so nothing the Agent sent (source names, grants, package ids) or any
/// exception text can reach a response, and nothing is ever logged here.
/// </summary>
internal sealed class AgentRequestDispatcher
{
    private const string UnsupportedMessage = "The command is not supported.";
    private const string InvalidMessage = "The request arguments are invalid.";
    private const string BusyMessage = "UniGetUI is busy; try again later.";
    private const string FailedMessage = "The request could not be completed.";
    private const string InventoryUnavailableMessage =
        "Remote package inventory is not available in this UniGetUI build.";
    private const string VersionsUnavailableMessage =
        "Remote version lookup is not available in this UniGetUI build.";
    private const string OperationUnavailableMessage =
        "Remote package operations are not available in this build; nothing was started.";

    private readonly IAgentRegistrationBackend _backend;

    public AgentRequestDispatcher(IAgentRegistrationBackend backend)
    {
        _backend = backend;
    }

    public static bool IsLowercaseUuid(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out Guid guid)
        && string.Equals(guid.ToString("D"), value, StringComparison.Ordinal);

    public byte[] Handle(string id, string? command, JsonElement arguments)
    {
        try
        {
            if (
                arguments.ValueKind
                is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)
            )
            {
                return Error(id, "InvalidArguments", InvalidMessage);
            }

            byte[] response = command switch
            {
                // Held back until the source representation and an isolated, complete snapshot
                // exist; failing closed beats an empty list that looks like a successful scan.
                "ListInstalledPackages" or "ListPackageUpdates" or "ListSources" => Error(
                    id,
                    "Failed",
                    InventoryUnavailableMessage
                ),
                "ListManagers" => Ok(
                    id,
                    writer => JsonSerializer.Serialize(
                        writer,
                        _backend.ListManagers(),
                        IpcJson.GetTypeInfo<IReadOnlyList<IpcRemoteManagerInfo>>()
                    )
                ),
                "GetPackageVersions" => GetPackageVersions(id, arguments),
                "ExecutePackageOperation" => ExecutePackageOperation(id, arguments),
                _ => Error(id, "UnsupportedCommand", UnsupportedMessage),
            };

            return response.Length <= AgentFrameCodec.MaxFrameLength
                ? response
                : Error(id, "Failed", FailedMessage);
        }
        catch (Exception)
        {
            return Error(id, "Failed", FailedMessage);
        }
    }

    public static byte[] Busy(string id) => Error(id, "Busy", BusyMessage);

    public static byte[] InvalidArguments(string id) =>
        Error(id, "InvalidArguments", InvalidMessage);

    private static byte[] GetPackageVersions(string id, JsonElement args)
    {
        if (
            !TryGetString(args, "manager", out string manager)
            || !TryGetString(args, "sourceName", out string sourceName)
            || !TryGetString(args, "packageId", out string packageId)
            || manager.Length == 0
            || sourceName.Length == 0
            || packageId.Length == 0
        )
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        return Error(id, "Failed", VersionsUnavailableMessage);
    }

    private static byte[] ExecutePackageOperation(string id, JsonElement args)
    {
        if (
            !TryGetString(args, "operationId", out string operationId)
            || !IsLowercaseUuid(operationId)
            || !TryGetString(args, "brokerRequestId", out string brokerRequestId)
            || !IsLowercaseUuid(brokerRequestId)
            || !TryGetString(args, "operation", out string operation)
            || operation is not ("Install" or "Update" or "Uninstall")
            || !TryGetString(args, "manager", out string manager)
            || !TryGetString(args, "sourceName", out string sourceName)
            || !TryGetString(args, "packageId", out string packageId)
            || manager.Length == 0
            || (operation == "Install" && sourceName.Length == 0)
            || (operation == "Uninstall" && sourceName.Length != 0)
            || packageId.Length == 0
            || !TryGetString(args, "version", out _, required: false)
            || !TryGetString(args, "scope", out string scope, required: false)
            || !(
                scope.Length == 0
                || scope.Equals("User", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)
            )
            || !TryGetString(args, "grant", out string grant)
            || !IsValidGrant(grant)
        )
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        // The installed SDK has no grant-execute API, and remote work must never fall back to
        // the legacy broker route or local execution, so a valid request is reported unstarted.
        return Ok(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", "Failed");
                writer.WriteString("error", OperationUnavailableMessage);
                writer.WriteEndObject();
            }
        );
    }

    private static bool IsValidGrant(string grant)
    {
        if (grant.Length is < 1 or > 2048 || !char.IsAsciiLetterOrDigit(grant[0]))
        {
            return false;
        }

        foreach (char c in grant)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '~' or ':' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(
        JsonElement args,
        string name,
        out string value,
        bool required = true
    )
    {
        value = "";
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement e))
        {
            return !required;
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = e.GetString() ?? "";
        return true;
    }

    private static byte[] Ok(string id, Action<Utf8JsonWriter> writeResult) =>
        Build(id, true, writer =>
        {
            writer.WritePropertyName("result");
            writeResult(writer);
        });

    private static byte[] Error(string id, string code, string message) =>
        Build(id, false, writer =>
        {
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

    private static byte[] Build(string id, bool ok, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "Response");
            writer.WriteString("id", id);
            writer.WriteBoolean("ok", ok);
            writeBody(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
