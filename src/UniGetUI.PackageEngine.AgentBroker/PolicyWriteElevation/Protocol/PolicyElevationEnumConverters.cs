using System.Text.Json;
using System.Text.Json.Serialization;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

public sealed class PolicyElevationOperationJsonConverter
    : JsonConverter<PolicyElevationOperation>
{
    public override PolicyElevationOperation Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "Update" => PolicyElevationOperation.Update,
                "ReplaceIdentity" => PolicyElevationOperation.ReplaceIdentity,
                "Create" => PolicyElevationOperation.Create,
                "Repair" => PolicyElevationOperation.Repair,
                _ => throw new JsonException("Unknown policy replacement operation."),
            }
            : throw new JsonException("Policy replacement operation must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        PolicyElevationOperation value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PolicyElevationOperation.Update => "Update",
            PolicyElevationOperation.ReplaceIdentity => "ReplaceIdentity",
            PolicyElevationOperation.Create => "Create",
            PolicyElevationOperation.Repair => "Repair",
            _ => throw new JsonException("Unknown policy replacement operation."),
        });
}

public sealed class PolicyElevationConflictHandlingJsonConverter
    : JsonConverter<PolicyElevationConflictHandling>
{
    public override PolicyElevationConflictHandling Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "Reject" => PolicyElevationConflictHandling.Reject,
                "ConfirmOverwrite" => PolicyElevationConflictHandling.ConfirmOverwrite,
                _ => throw new JsonException("Unknown policy conflict handling."),
            }
            : throw new JsonException("Policy conflict handling must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        PolicyElevationConflictHandling value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PolicyElevationConflictHandling.Reject => "Reject",
            PolicyElevationConflictHandling.ConfirmOverwrite => "ConfirmOverwrite",
            _ => throw new JsonException("Unknown policy conflict handling."),
        });
}

public sealed class PolicyElevationDispositionJsonConverter
    : JsonConverter<PolicyElevationDisposition>
{
    public override PolicyElevationDisposition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "Committed" => PolicyElevationDisposition.Committed,
                "Rejected" => PolicyElevationDisposition.Rejected,
                "Unknown" => PolicyElevationDisposition.Unknown,
                _ => throw new JsonException("Unknown policy elevation disposition."),
            }
            : throw new JsonException("Policy elevation disposition must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        PolicyElevationDisposition value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PolicyElevationDisposition.Committed => "Committed",
            PolicyElevationDisposition.Rejected => "Rejected",
            PolicyElevationDisposition.Unknown => "Unknown",
            _ => throw new JsonException("Unknown policy elevation disposition."),
        });
}

public sealed class PolicyElevationManagementStateJsonConverter
    : JsonConverter<PolicyElevationManagementState>
{
    public override PolicyElevationManagementState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "Active" => PolicyElevationManagementState.Active,
                "Missing" => PolicyElevationManagementState.Missing,
                "Invalid" => PolicyElevationManagementState.Invalid,
                _ => throw new JsonException("Unknown policy management state."),
            }
            : throw new JsonException("Policy management state must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        PolicyElevationManagementState value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PolicyElevationManagementState.Active => "Active",
            PolicyElevationManagementState.Missing => "Missing",
            PolicyElevationManagementState.Invalid => "Invalid",
            _ => throw new JsonException("Unknown policy management state."),
        });
}
