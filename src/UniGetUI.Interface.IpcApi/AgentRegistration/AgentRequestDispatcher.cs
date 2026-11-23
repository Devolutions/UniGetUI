using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Devolutions.Now.Policy.Api;
using UniGetUI.PackageEngine.AgentBroker;

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
        "No completed package scan covers every enabled package manager.";
    private const string RescanFailedMessage =
        "The package rescan did not complete for every enabled package manager.";
    private const string ScopeNotAllowedMessage =
        "This manager cannot run the operation in the requested scope; nothing was started.";
    private const string ManagerDisabledMessage =
        "This package manager is disabled in UniGetUI on this machine; nothing was started.";
    private const string RowNotIdentifiedMessage =
        "The installation could not be identified in the installed package list; nothing was started.";
    private const string NotSentMessage =
        "The operation could not be submitted to the Devolutions Agent; nothing was started.";

    private readonly IAgentRegistrationBackend _backend;

    public AgentRequestDispatcher(IAgentRegistrationBackend backend)
    {
        _backend = backend;
    }

    public static bool IsLowercaseUuid(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out Guid guid)
        && string.Equals(guid.ToString("D"), value, StringComparison.Ordinal);

    public async Task<byte[]> HandleAsync(
        string id,
        string? command,
        JsonElement arguments,
        CancellationToken cancellationToken = default
    )
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
                "GetInventorySnapshot" => GetInventorySnapshot(id),
                "ExecutePackageOperation" => await ExecutePackageOperationAsync(
                        id,
                        arguments,
                        cancellationToken
                    )
                    .ConfigureAwait(false),
                "RescanInventory" => await RescanInventoryAsync(id, cancellationToken)
                    .ConfigureAwait(false),
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

    /// <summary>Starts a listing-only reload of the installed packages unless a load is already running.</summary>
    public void RefreshInstalledInventory()
    {
        try
        {
            _backend.RefreshInstalledInventory();
        }
        catch (Exception) { }
    }

    private async Task<byte[]> RescanInventoryAsync(string id, CancellationToken cancellationToken)
    {
        bool rescanned = await _backend.RescanInventoryAsync(cancellationToken).ConfigureAwait(false);
        return rescanned
            ? Ok(
                id,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
            )
            : Error(id, "Failed", RescanFailedMessage);
    }

    private byte[] GetInventorySnapshot(string id)
    {
        RemoteInventorySnapshot? snapshot = _backend.TryGetInventorySnapshot();
        if (snapshot is null)
        {
            return Error(id, "Failed", InventoryUnavailableMessage);
        }

        return Ok(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("completedAt", FormatUtc(snapshot.CompletedUtc));
                if (snapshot.UpdateCompletedUtc is DateTime updateCompleted)
                {
                    writer.WriteString("updateCompletedAt", FormatUtc(updateCompleted));
                }
                else
                {
                    writer.WriteNull("updateCompletedAt");
                }

                writer.WritePropertyName("managers");
                JsonSerializer.Serialize(
                    writer,
                    snapshot.Managers,
                    IpcJson.GetTypeInfo<IReadOnlyList<IpcRemoteManagerInfo>>()
                );
                writer.WritePropertyName("sources");
                JsonSerializer.Serialize(
                    writer,
                    snapshot.Sources,
                    IpcJson.GetTypeInfo<IReadOnlyList<IpcSourceInfo>>()
                );
                WritePackages(writer, "installedPackages", snapshot.InstalledPackages);
                WritePackages(
                    writer,
                    "updates",
                    snapshot.UpdateCompletedUtc is null ? [] : snapshot.Updates
                );
                writer.WriteEndObject();
            }
        );
    }

    internal static string FormatUtc(DateTime value) =>
        DateTime
            .SpecifyKind(
                value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
                DateTimeKind.Utc
            )
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    // systemRestartPending is left out: remote requests never read the pending-reboot store.
    private static void WritePackages(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyList<IpcPackageInfo> packages
    )
    {
        writer.WriteStartArray(name);
        foreach (IpcPackageInfo package in packages)
        {
            writer.WriteStartObject();
            writer.WriteString("name", package.Name.Length == 0 ? package.Id : package.Name);
            writer.WriteString("id", package.Id);
            writer.WriteString("version", package.Version);
            writer.WriteString("newVersion", package.NewVersion);
            writer.WriteString("source", package.Source);
            // A package's loaded source is assigned rather than observed, so its canonical name is unproven.
            writer.WriteString("sourceName", "");
            writer.WriteString("manager", package.Manager);
            if (package.ReportedScope is null)
            {
                writer.WriteNull("reportedScope");
            }
            else
            {
                writer.WriteString("reportedScope", package.ReportedScope);
            }

            writer.WriteBoolean("isUpgradable", package.IsUpgradable);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private async Task<byte[]> ExecutePackageOperationAsync(
        string id,
        JsonElement args,
        CancellationToken cancellationToken
    )
    {
        if (
            !HasOnlyOperationArguments(args)
            || !TryGetString(args, "operationId", out string operationId)
            || !IsLowercaseUuid(operationId)
            || !TryGetString(args, "brokerRequestId", out string brokerRequestId)
            || !IsLowercaseUuid(brokerRequestId)
            || !TryGetString(args, "operation", out string operationText)
            || !TryParseOperation(operationText, out Operation operation)
            || !TryGetString(args, "manager", out string managerText)
            || !TryGetString(args, "sourceName", out string sourceName)
            || !TryGetString(args, "packageId", out string packageId)
            || packageId.Length == 0
            || (operation is Operation.Install && sourceName.Length == 0)
            || (operation is Operation.Uninstall && sourceName.Length != 0)
            || !TryGetString(args, "version", out string version, required: false)
            || !TryGetString(args, "scope", out string scopeText)
            || !TryParseScope(scopeText, out Scope scope)
            || !TryGetReportedScope(args, operation, out string? reportedScope)
            || !TryGetString(args, "grant", out string grant)
            || !IsValidGrant(grant)
        )
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        // Chocolatey and unknown managers are refused, as by the Agent.
        if (!RemoteGrantRequestBuilder.TryParseManager(managerText, out ManagerName manager))
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        string? managerId = _backend.FindManagerId(manager);
        if (managerId is null)
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        // The user turned this manager off on this machine; nothing is started for it.
        if (!_backend.IsManagerEnabled(managerId))
        {
            return OperationResult(id, "Failed", ManagerDisabledMessage);
        }

        if (
            sourceName.Length != 0
            && _backend.ResolveSource(managerId, sourceName) is not RemoteSourceResolution.Resolved
        )
        {
            return Error(id, "InvalidArguments", InvalidMessage);
        }

        RemoteGrantPlan? plan = RemoteGrantRequestBuilder.Plan(
            new RemoteGrantOperation(
                brokerRequestId,
                operation,
                manager,
                sourceName,
                packageId,
                version,
                scope,
                reportedScope
            )
        );
        if (plan is null)
        {
            return OperationResult(id, "Failed", ScopeNotAllowedMessage);
        }

        // Checked last, so the list the decision rests on is as fresh as it can be.
        if (
            plan.RequiresUniqueInstalledRow
            && !_backend.IsOnlyInstalledRow(managerId, packageId, reportedScope ?? "")
        )
        {
            return OperationResult(id, "Failed", RowNotIdentifiedMessage);
        }

        RemoteGrantDelivery delivery = await _backend
            .SubmitGrantAsync(plan, grant, cancellationToken)
            .ConfigureAwait(false);

        // Once the grant may have left, nothing UniGetUI holds proves the outcome.
        return delivery is RemoteGrantDelivery.NotSent
            ? OperationResult(id, "Failed", NotSentMessage)
            : OperationResult(id, "Unknown", "");
    }

    private static byte[] OperationResult(string id, string status, string error) =>
        Ok(
            id,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("status", status);
                writer.WriteString("error", error);
                writer.WriteEndObject();
            }
        );

    private static bool TryParseOperation(string text, out Operation operation)
    {
        operation = text switch
        {
            "Install" => Operation.Install,
            "Update" => Operation.Update,
            "Uninstall" => Operation.Uninstall,
            _ => (Operation)(-1),
        };
        return operation is Operation.Install or Operation.Update or Operation.Uninstall;
    }

    private static bool TryParseScope(string text, out Scope scope)
    {
        scope = text switch
        {
            "User" => Scope.User,
            "Machine" => Scope.Machine,
            _ => (Scope)(-1),
        };
        return scope is Scope.User or Scope.Machine;
    }

    /// <summary>Required (possibly empty) for update and uninstall, and absent for install.</summary>
    private static bool TryGetReportedScope(
        JsonElement args,
        Operation operation,
        out string? reportedScope
    )
    {
        reportedScope = null;
        bool present = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("reportedScope", out _);
        if (operation is Operation.Install)
        {
            return !present;
        }

        if (!present || !TryGetString(args, "reportedScope", out string value))
        {
            return false;
        }

        reportedScope = value;
        return true;
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

    private static bool HasOnlyOperationArguments(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
        {
            if (property.Name is not (
                "operationId" or "brokerRequestId" or "operation" or "manager" or "sourceName"
                or "packageId" or "version" or "scope" or "reportedScope" or "grant")
                || !seen.Add(property.Name))
                return false;
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
