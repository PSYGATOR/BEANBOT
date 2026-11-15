using System.Net;

namespace BeanBot.Transport;

/// <summary>
/// Datagram transport abstraction allowing Local (plain UDP) and Live/Private (DTLS 1.2)
/// to share the exact same Hazel framing and protocol serialization.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }
    bool UsesDtls { get; }
    IPEndPoint? RemoteEndPoint { get; }

    Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default);
    Task SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default);
    Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
}
