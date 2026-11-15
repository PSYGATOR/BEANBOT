namespace BeanBot.Protocol;

/// <summary>
/// Exception thrown on any malformed, truncated, or unknown protocol packet.
/// Always captures packet id (when known) and hex dump per ground rule G8.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(byte? packetId, ReadOnlySpan<byte> payload, string message, Exception? innerException = null)
        : base(FormatMessage(packetId, payload, message), innerException)
    {
        PacketId = packetId;
        HexDump = ToHexDump(payload);
    }

    public byte? PacketId { get; }
    public string HexDump { get; }

    public static string ToHexDump(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        if (data.IsEmpty)
        {
            return "<empty>";
        }

        var count = Math.Min(data.Length, maxBytes);
        var hex = Convert.ToHexString(data[..count]);
        return data.Length > maxBytes ? $"{hex}...(+{data.Length - maxBytes}B)" : hex;
    }

    private static string FormatMessage(byte? packetId, ReadOnlySpan<byte> payload, string message)
    {
        var idText = packetId.HasValue ? $"0x{packetId.Value:x2}" : "<unknown>";
        return $"[PacketId={idText}] {message} | Hex={ToHexDump(payload)}";
    }
}
