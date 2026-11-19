using System.Buffers.Binary;
using System.Text.Json;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>A violation of the registration framing or message rules; the connection must be closed.</summary>
internal sealed class AgentProtocolException : Exception
{
    public AgentProtocolException()
        : base("Registration protocol violation.") { }
}

/// <summary>Length-prefixed JSON framing: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.</summary>
internal static class AgentFrameCodec
{
    public const int MaxFrameLength = 1_048_576;

    /// <summary>Reads one frame, or returns null when the stream ended cleanly between frames.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        int read = await stream.ReadAsync(header.AsMemory(), ct).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            await stream.ReadExactlyAsync(header.AsMemory(read), ct).ConfigureAwait(false);
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 1 || length > MaxFrameLength)
        {
            throw new AgentProtocolException();
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        return payload;
    }

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 1 || payload.Length > MaxFrameLength)
        {
            throw new AgentProtocolException();
        }

        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Parses a frame into a detached JSON object and its string <c>kind</c>.</summary>
    public static JsonElement Parse(byte[] payload, out string kind)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("kind", out JsonElement kindElement)
                || kindElement.ValueKind != JsonValueKind.String
            )
            {
                throw new AgentProtocolException();
            }

            kind = kindElement.GetString() ?? "";
            return root.Clone();
        }
        catch (JsonException)
        {
            throw new AgentProtocolException();
        }
    }
}
