namespace BeanBot.Protocol.Hazel;

/// <summary>
/// Hazel message delivery mode (5.3).
/// Source: Impostor.Hazel.Abstractions.MessageType.
/// </summary>
[Flags]
public enum MessageType : byte
{
    /// <summary>0x00 Normal / unreliable delivery (movement and pings).</summary>
    Unreliable = 0x00,

    /// <summary>0x01 Reliable delivery with send-id and acknowledgement tracking.</summary>
    Reliable = 0x01,
}

/// <summary>
/// Hazel UDP framing send options (5.3).
/// Source: Impostor.Hazel.Udp.UdpSendOption + Among Us wire spec 5.3.
/// </summary>
public enum UdpSendOption : byte
{
    Normal = 0x00,
    Reliable = 0x01,
    Hello = 0x08,
    Disconnect = 0x09,
    Acknowledgement = 0x0a,
    Fragment = 0x0b,
    Ping = 0x0c,
}
