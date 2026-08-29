using System.Buffers.Binary;
using System.Text.Json;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>Why a frame could not be exchanged.</summary>
public enum PolicyElevationFrameError
{
    /// <summary>The peer closed the connection before a complete frame arrived.</summary>
    EndOfStream,

    /// <summary>The announced body length exceeds the negotiated budget.</summary>
    Oversized,

    /// <summary>The frame header or body could not be interpreted.</summary>
    Malformed,
}

public sealed class PolicyElevationFrameException : IOException
{
    public PolicyElevationFrameException(PolicyElevationFrameError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public PolicyElevationFrameError Error { get; }
}

/// <summary>
/// Length-prefixed UTF-8 JSON framing used by the elevated policy-write channel.
/// Layout: 4-byte big-endian body length, then exactly that many UTF-8 bytes.
/// </summary>
public static class PolicyElevationFrame
{
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> body,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (body.Length is 0)
        {
            throw new PolicyElevationFrameException(
                PolicyElevationFrameError.Malformed,
                "Refusing to write an empty policy elevation frame.");
        }

        if (body.Length > maxBodyBytes)
        {
            throw new PolicyElevationFrameException(
                PolicyElevationFrameError.Oversized,
                $"Policy elevation frame of {body.Length} bytes exceeds the {maxBodyBytes} byte budget.");
        }

        byte[] header = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(
        Stream stream,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[PolicyElevationProtocol.FrameLengthPrefixBytes];
        await ReadExactlyAsync(stream, header, allowCleanEndOfStream: true, cancellationToken).ConfigureAwait(false);

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (declaredLength is 0)
        {
            throw new PolicyElevationFrameException(
                PolicyElevationFrameError.Malformed,
                "Policy elevation frame declared a zero-length body.");
        }

        if (declaredLength > (uint)maxBodyBytes)
        {
            throw new PolicyElevationFrameException(
                PolicyElevationFrameError.Oversized,
                $"Policy elevation frame declared {declaredLength} bytes, above the {maxBodyBytes} byte budget.");
        }

        byte[] body = new byte[declaredLength];
        await ReadExactlyAsync(stream, body, allowCleanEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return body;
    }

    public static async Task WriteRequestAsync(
        Stream stream,
        PolicyElevationRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        byte[] body = Serialize(
            request,
            PolicyElevationJsonContext.Default.PolicyElevationRequestMessage);

        await WriteAsync(stream, body, PolicyElevationProtocol.MaxRequestFrameBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<PolicyElevationRequestMessage> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] body = await ReadAsync(stream, PolicyElevationProtocol.MaxRequestFrameBytes, cancellationToken)
            .ConfigureAwait(false);

        PolicyElevationRequestMessage request = Deserialize(
            body,
            PolicyElevationJsonContext.Default.PolicyElevationRequestMessage);

        ValidateRequest(request);
        return request;
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        PolicyElevationResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ValidateResponse(response);

        byte[] body = Serialize(
            response,
            PolicyElevationJsonContext.Default.PolicyElevationResponseMessage);

        await WriteAsync(stream, body, PolicyElevationProtocol.MaxResponseFrameBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<PolicyElevationResponseMessage> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] body = await ReadAsync(stream, PolicyElevationProtocol.MaxResponseFrameBytes, cancellationToken)
            .ConfigureAwait(false);

        PolicyElevationResponseMessage response = Deserialize(
            body,
            PolicyElevationJsonContext.Default.PolicyElevationResponseMessage);

        ValidateResponse(response);
        return response;
    }

    public static void ValidateRequest(PolicyElevationRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireProtocolVersion(request.ProtocolVersion);
        RequireRequestId(request.RequestId);

        if (!Enum.IsDefined(request.Operation) || !Enum.IsDefined(request.ConflictHandling))
        {
            throw Malformed("Policy elevation request carried an undefined enumeration value.");
        }

        RequireRequiredSafeAscii(
            request.ExpectedStoreToken,
            PolicyElevationProtocol.MaxStoreTokenCharacters,
            "expectedStoreToken");

        RequireRequiredSafeAscii(
            request.ValidationReceipt,
            PolicyElevationProtocol.MaxValidationReceiptCharacters,
            "validationReceipt");

        if (request.Draft.ValueKind is JsonValueKind.Undefined)
        {
            throw Malformed("Policy elevation request carried no draft.");
        }
    }

    public static void ValidateResponse(PolicyElevationResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        RequireProtocolVersion(response.ProtocolVersion);
        RequireRequestId(response.RequestId);

        if (!Enum.IsDefined(response.Outcome))
        {
            throw Malformed("Policy elevation response carried an undefined outcome.");
        }

        RequireOptionalLength(
            response.BrokerErrorCode,
            PolicyElevationProtocol.MaxBrokerErrorCodeCharacters,
            "brokerErrorCode");

        RequireOptionalLength(
            response.Message,
            PolicyElevationProtocol.MaxMessageCharacters,
            "message");

        if (response.Payload is { ValueKind: JsonValueKind.Undefined })
        {
            throw Malformed("Policy elevation response carried an undefined payload.");
        }
    }

    private static byte[] Serialize<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T Deserialize<T>(
        byte[] body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo)
                ?? throw Malformed("Policy elevation frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new PolicyElevationFrameException(
                PolicyElevationFrameError.Malformed,
                "Policy elevation frame was not valid JSON for its contract.",
                ex);
        }
    }

    private static void RequireProtocolVersion(string? value)
    {
        if (value is null
            || value.Length > PolicyElevationProtocol.MaxProtocolVersionCharacters
            || !string.Equals(value, PolicyElevationProtocol.Version, StringComparison.Ordinal))
        {
            throw Malformed("Policy elevation frame carried an unsupported protocol version.");
        }
    }

    private static void RequireRequestId(string? value)
    {
        if (value is null || value.Length != PolicyElevationProtocol.RequestIdCharacters)
        {
            throw Malformed("Policy elevation frame carried a malformed request identifier.");
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                throw Malformed("Policy elevation frame carried a malformed request identifier.");
            }
        }
    }

    private static void RequireOptionalLength(string? value, int maxCharacters, string field)
    {
        if (value is not null && value.Length > maxCharacters)
        {
            throw Malformed($"Policy elevation frame field '{field}' exceeded {maxCharacters} characters.");
        }
    }

    /// <summary>
    /// Mirrors the shared policy store-token / validation-receipt grammar exactly: one or more
    /// characters, every one of them printable ASCII, and the first one an ASCII alphanumeric.
    /// </summary>
    /// <remarks>
    /// The shared converters that enforce this on the broker side
    /// (<c>PolicyStoreTokenJsonConverter</c> and <c>PolicyValidationReceiptJsonConverter</c>) are
    /// internal to the policy API package and cannot be referenced from here, so the rule is
    /// mirrored rather than reused. It is verified against the real converters by the round-trip
    /// tests, which reject anything this method accepts but the broker would not.
    /// </remarks>
    private static void RequireRequiredSafeAscii(
        string? value,
        int maxCharacters,
        string field)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxCharacters)
            throw Malformed($"Policy elevation frame carried an invalid {field}.");

        if (!char.IsAsciiLetterOrDigit(value[0]))
            throw Malformed($"Policy elevation frame carried an invalid {field}.");

        foreach (char character in value)
        {
            if (character is < (char)0x21 or > (char)0x7e)
                throw Malformed($"Policy elevation frame carried an invalid {field}.");
        }
    }

    private static PolicyElevationFrameException Malformed(string message)
        => new(PolicyElevationFrameError.Malformed, message);

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        bool allowCleanEndOfStream,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read is 0)
            {
                if (offset is 0 && allowCleanEndOfStream)
                {
                    throw new PolicyElevationFrameException(
                        PolicyElevationFrameError.EndOfStream,
                        "The policy elevation peer closed the connection without sending a frame.");
                }

                throw new PolicyElevationFrameException(
                    PolicyElevationFrameError.Malformed,
                    $"The policy elevation peer closed the connection after {offset} of {destination.Length} bytes.");
            }

            offset += read;
        }
    }
}
