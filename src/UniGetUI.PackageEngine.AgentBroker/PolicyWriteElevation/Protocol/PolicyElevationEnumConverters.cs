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

public sealed class PolicyElevationResponseStatusJsonConverter
    : JsonConverter<PolicyElevationResponseStatus>
{
    public override PolicyElevationResponseStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { } value
            ? value switch
            {
                "Replaced" => PolicyElevationResponseStatus.Replaced,
                "BrokerRejected" => PolicyElevationResponseStatus.BrokerRejected,
                "BrokerUnavailable" => PolicyElevationResponseStatus.BrokerUnavailable,
                "BrokerInvalidResponse" => PolicyElevationResponseStatus.BrokerInvalidResponse,
                "HelperRejected" => PolicyElevationResponseStatus.HelperRejected,
                _ => throw new JsonException("Unknown policy elevation response status."),
            }
            : throw new JsonException("Policy elevation response status must be a string.");

    public override void Write(
        Utf8JsonWriter writer,
        PolicyElevationResponseStatus value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            PolicyElevationResponseStatus.Replaced => "Replaced",
            PolicyElevationResponseStatus.BrokerRejected => "BrokerRejected",
            PolicyElevationResponseStatus.BrokerUnavailable => "BrokerUnavailable",
            PolicyElevationResponseStatus.BrokerInvalidResponse => "BrokerInvalidResponse",
            PolicyElevationResponseStatus.HelperRejected => "HelperRejected",
            _ => throw new JsonException("Unknown policy elevation response status."),
        });
}
