using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace BeanBot.Transport;

/// <summary>
/// Managed DTLS 1.2 client transport powered by BouncyCastle (Org.BouncyCastle.Tls)
/// with TOFU certificate pinning (D3, 5.2, F1). No native dependencies or P/Invoke.
/// </summary>
public sealed class DtlsSession : ITransport
{
    private const int DefaultMtu = 1400;
    private const int ReceiveWaitMs = 50;

    private readonly string _region;
    private readonly TofuCertificatePinStore _pinStore;
    private readonly bool _acceptNewPin;
    private readonly ILogger<DtlsSession>? _logger;

    private Socket? _socket;
    private DtlsTransport? _dtlsTransport;

    public DtlsSession(
        string region,
        TofuCertificatePinStore pinStore,
        bool acceptNewPin = false,
        ILogger<DtlsSession>? logger = null)
    {
        _region = region;
        _pinStore = pinStore;
        _acceptNewPin = acceptNewPin;
        _logger = logger;
    }

    public bool IsConnected => _dtlsTransport is not null;
    public bool UsesDtls => true;
    public IPEndPoint? RemoteEndPoint { get; private set; }

    public Task ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        ct.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            CloseSync();

            var socket = new Socket(endPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(endPoint);
            _socket = socket;
            RemoteEndPoint = endPoint;

            var datagramTransport = new SocketDatagramTransport(socket, DefaultMtu, _logger);
            var verifier = new TofuCertificateVerifier(_region, _pinStore, _acceptNewPin);
            var client = new PinnedDtlsClient(verifier);
            var protocol = new DtlsClientProtocol();

            using var cancelReg = ct.Register(CloseSync);
            try
            {
                _dtlsTransport = protocol.Connect(client, datagramTransport);
                _logger?.LogDebug("Established DTLS 1.2 session to {EndPoint} (region={Region}).", endPoint, _region);
            }
            catch
            {
                CloseSync();
                throw;
            }
        }, ct);
    }

    public Task SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var transport = _dtlsTransport ?? throw new InvalidOperationException("DtlsSession is not connected.");
        var copy = datagram.ToArray();
        transport.Send(copy, 0, copy.Length);
        return Task.CompletedTask;
    }

    public Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var transport = _dtlsTransport ?? throw new InvalidOperationException("DtlsSession is not connected.");
        return Task.Run(() =>
        {
            var temp = new byte[Math.Max(buffer.Length, transport.GetReceiveLimit())];
            while (!ct.IsCancellationRequested && _dtlsTransport is not null)
            {
                var read = transport.Receive(temp, 0, temp.Length, ReceiveWaitMs);
                if (read > 0)
                {
                    if (read > buffer.Length)
                    {
                        throw new InvalidOperationException($"DTLS datagram ({read}B) exceeds caller buffer ({buffer.Length}B).");
                    }

                    temp.AsSpan(0, read).CopyTo(buffer.Span);
                    return read;
                }
            }

            return 0;
        }, ct);
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        CloseSync();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        CloseSync();
        return ValueTask.CompletedTask;
    }

    private void CloseSync()
    {
        if (_dtlsTransport is { } dtls)
        {
            _dtlsTransport = null;
            try { dtls.Close(); } catch { /* ignore teardown */ }
        }

        if (_socket is { } s)
        {
            _socket = null;
            try { s.Close(); } catch { /* ignore teardown */ }
            s.Dispose();
        }
    }

    private sealed class PinnedDtlsClient : DefaultTlsClient
    {
        private readonly ICertificateVerifyer _verifier;

        public PinnedDtlsClient(ICertificateVerifyer verifier)
            : base(new BcTlsCrypto(new SecureRandom()))
        {
            _verifier = verifier;
        }

        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.DTLSv12];

        public override TlsAuthentication GetAuthentication() => new PinnedTlsAuthentication(_verifier);
    }

    private sealed class PinnedTlsAuthentication : ServerOnlyTlsAuthentication
    {
        private readonly ICertificateVerifyer _verifier;

        public PinnedTlsAuthentication(ICertificateVerifyer verifier)
        {
            _verifier = verifier;
        }

        public override void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var certs = serverCertificate.Certificate.GetCertificateList();
            if (certs.Length == 0)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            var x509Chain = new X509CertificateStructure[certs.Length];
            for (var i = 0; i < certs.Length; i++)
            {
                x509Chain[i] = X509CertificateStructure.GetInstance(certs[i].GetEncoded());
            }

            if (!_verifier.IsValid(x509Chain))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }
    }

    private sealed class SocketDatagramTransport : DatagramTransport
    {
        private readonly Socket _socket;
        private readonly int _mtu;
        private readonly ILogger? _logger;

        public SocketDatagramTransport(Socket socket, int mtu, ILogger? logger = null)
        {
            _socket = socket;
            _mtu = mtu;
            _logger = logger;
        }

        public int GetReceiveLimit() => _mtu;
        public int GetSendLimit() => _mtu;

        public int Receive(byte[] buf, int off, int len, int waitMillis) =>
            Receive(buf.AsSpan(off, len), waitMillis);

        public int Receive(Span<byte> buf, int waitMillis)
        {
            _socket.ReceiveTimeout = Math.Max(1, waitMillis);
            try
            {
                var read = _socket.Receive(buf, SocketFlags.None);
                _logger?.LogDebug("DTLS RX {Bytes}B (first=0x{First:X2})", read, read > 0 ? buf[0] : 0);
                return read;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
            {
                _logger?.LogDebug("DTLS RX timeout ({WaitMs}ms)", waitMillis);
                return -1;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted or SocketError.ConnectionReset)
            {
                _logger?.LogDebug("DTLS RX socket {Error}", ex.SocketErrorCode);
                return -1;
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
        }

        public void Send(byte[] buf, int off, int len) =>
            Send(buf.AsSpan(off, len));

        public void Send(ReadOnlySpan<byte> buf)
        {
            _logger?.LogDebug("DTLS TX {Bytes}B (first=0x{First:X2})", buf.Length, buf.Length > 0 ? buf[0] : 0);
            _socket.Send(buf, SocketFlags.None);
        }

        public void Close()
        {
            try { _socket.Close(); } catch { /* ignore */ }
        }
    }
}
