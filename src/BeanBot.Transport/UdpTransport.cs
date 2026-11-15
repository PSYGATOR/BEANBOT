using System.Net;
using System.Net.Sockets;

namespace BeanBot.Transport;

/// <summary>
/// Plain UDP datagram transport used when ServerProfile.UseDtls is false (e.g., Local Impostor CI).
/// </summary>
public sealed class UdpTransport : ITransport
{
    private Socket? _socket;

    public bool IsConnected => _socket is { Connected: true };
    public bool UsesDtls => false;
    public IPEndPoint? RemoteEndPoint { get; private set; }

    public Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ct.ThrowIfCancellationRequested();

        _socket?.Dispose();
        var socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(endPoint);
        _socket = socket;
        RemoteEndPoint = endPoint;
        return Task.CompletedTask;
    }

    public async Task SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("UdpTransport is not connected.");
        await socket.SendAsync(datagram, SocketFlags.None, ct).ConfigureAwait(false);
    }

    public async Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("UdpTransport is not connected.");
        try
        {
            return await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        if (_socket is { } s)
        {
            _socket = null;
            try { s.Close(); } catch { /* closing socket */ }
            s.Dispose();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        CloseAsync().GetAwaiter().GetResult();
        return ValueTask.CompletedTask;
    }
}
