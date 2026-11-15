using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.RootMessages;
using BeanBot.Transport;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using Xunit;

namespace BeanBot.Tests;

public sealed class TransportAndDtlsParityTests
{
    [Fact]
    public void ServerProfile_Presets_SetExpectedFlags()
    {
        var local = ServerProfile.Local();
        Assert.Equal(ServerProfileMode.Local, local.Mode);
        Assert.False(local.UseDtls);
        Assert.False(local.UseDeviceAuth);

        var priv = ServerProfile.Private("https://private.example:22023", useDtls: true, useDeviceAuth: false);
        Assert.Equal(ServerProfileMode.Private, priv.Mode);
        Assert.True(priv.UseDtls);
        Assert.False(priv.UseDeviceAuth);

        var live = ServerProfile.Live("na");
        Assert.Equal(ServerProfileMode.Live, live.Mode);
        Assert.True(live.UseDtls);
        Assert.True(live.UseDeviceAuth);
        Assert.Equal("https://matchmaker.among.us", live.MatchmakerBaseUrl);
    }

    [Fact]
    public void AckTracker_RetransmitsWithBackoff_AndProcessesSelectiveAck()
    {
        long now = 1000;
        var tracker = new AckTracker(initialResendMs: 100, maxResendIntervalMs: 400, disconnectTimeoutMs: 2000, () => now);

        var id1 = tracker.NextSendId();
        var id2 = tracker.NextSendId();
        tracker.TrackOutbound(id1, [0x01, 0x00, 0x01]);
        tracker.TrackOutbound(id2, [0x01, 0x00, 0x02]);
        Assert.Equal(2, tracker.PendingCount);

        now += 500;
        var (resends, timedOut) = tracker.CollectRetransmits();
        Assert.Empty(timedOut);
        Assert.Equal(2, resends.Count);

        // Receiver saw id2 and id1 -> selective ACK on id2 with bit 0 set (id2 - 1 = id1)
        var rx = new AckTracker();
        Assert.True(rx.ProcessInboundReliable(id1).IsNew);
        var (isNew2, ack2) = rx.ProcessInboundReliable(id2);
        Assert.True(isNew2);
        Assert.False(rx.ProcessInboundReliable(id2).IsNew);

        var acked = tracker.ProcessInboundAck(ack2);
        Assert.Equal(2, acked);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void FragmentReassembler_EnforcesCapAndOrphanExpiry()
    {
        long now = 0;
        var reassembler = new FragmentReassembler(
            maxTotalBufferedBytes: 64,
            maxMessageBytes: 64,
            orphanTimeout: TimeSpan.FromSeconds(5),
            () => now);

        var payload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var frags = FragmentReassembler.SplitIntoFragments(streamId: 7, payload, maxChunkSize: 16);
        Assert.Equal(3, frags.Length);

        // Orphan timeout test: add 1 fragment, advance 5.1s, confirm pruned
        Assert.False(reassembler.TryAddFragment(frags[0], out _));
        Assert.Equal(1, reassembler.ActiveStreams);
        now = 5100;
        Assert.Equal(1, reassembler.PruneOrphans());
        Assert.Equal(0, reassembler.ActiveStreams);
        Assert.Equal(0, reassembler.TotalBufferedBytes);

        // Full reassembly out of order
        Assert.False(reassembler.TryAddFragment(frags[2], out _));
        Assert.False(reassembler.TryAddFragment(frags[0], out _));
        Assert.True(reassembler.TryAddFragment(frags[1], out var combined));
        Assert.Equal(payload, combined);

        // Flood cap test (F11)
        var oversized = FragmentReassembler.SplitIntoFragments(streamId: 9, new byte[100], maxChunkSize: 40);
        Assert.False(reassembler.TryAddFragment(oversized[0], out _));
        Assert.Throws<ProtocolException>(() => reassembler.TryAddFragment(oversized[1], out _));
    }

    [Fact]
    public async Task Serializer_RoundTripsIdentically_OverPlainUdpAndDtls12()
    {
        var tempCerts = Path.Combine(Path.GetTempPath(), "beanbot-certs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var messages = ProtocolRoundTripTests.AllRootMessages()
                .Select(arr => (IRootMessage)arr[0])
                .ToArray();

            var udpResults = await RunEchoRoundTripAsync(useDtls: false, tempCerts, messages);
            var dtlsResults = await RunEchoRoundTripAsync(useDtls: true, tempCerts, messages);

            Assert.Equal(messages.Length, udpResults.Count);
            Assert.Equal(messages.Length, dtlsResults.Count);

            for (var i = 0; i < messages.Length; i++)
            {
                Assert.Equal(messages[i], udpResults[i].Decoded);
                Assert.Equal(messages[i], dtlsResults[i].Decoded);
                Assert.Equal(udpResults[i].WirePayload, dtlsResults[i].WirePayload);
            }
        }
        finally
        {
            if (Directory.Exists(tempCerts))
            {
                Directory.Delete(tempCerts, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TofuPinning_PinsOnFirstConnect_AbortsOnRotation_AndRepinsWithFlag()
    {
        var tempCerts = Path.Combine(Path.GetTempPath(), "beanbot-pin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (certA, keyA) = GenerateSelfSignedRsaCert("CN=relay-a.among.us");
            var (certB, keyB) = GenerateSelfSignedRsaCert("CN=relay-b.among.us");

            // 1. First connect pins certA
            await using (var serverA = new InProcessDtlsEchoServer(certA, keyA))
            {
                var profile = ServerProfile.Live("na", certsDirectory: tempCerts, acceptNewPin: false);
                await using var transport = profile.CreateTransport();
                await transport.ConnectAsync(serverA.EndPoint);
                Assert.True(transport.UsesDtls);
            }

            var pinStore = new TofuCertificatePinStore(tempCerts);
            var (derPath, shaPath) = pinStore.GetPinPaths("na");
            Assert.True(File.Exists(derPath));
            Assert.True(File.Exists(shaPath));
            var expectedSha = File.ReadAllText(shaPath).Trim();
            Assert.Equal(TofuCertificatePinStore.ComputeSha256Hex(certA.GetEncoded()), expectedSha);

            // 2. Second connect with certA succeeds
            await using (var serverA2 = new InProcessDtlsEchoServer(certA, keyA))
            {
                var profile = ServerProfile.Live("na", certsDirectory: tempCerts, acceptNewPin: false);
                await using var transport = profile.CreateTransport();
                await transport.ConnectAsync(serverA2.EndPoint);
            }

            // 3. Connect to rotated certB aborts with both hashes & subject
            await using (var serverB = new InProcessDtlsEchoServer(certB, keyB))
            {
                var profile = ServerProfile.Live("na", certsDirectory: tempCerts, acceptNewPin: false);
                await using var transport = profile.CreateTransport();
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(serverB.EndPoint));
                var mismatch = FindInner<CertificatePinMismatchException>(ex);
                Assert.NotNull(mismatch);
                Assert.Equal(expectedSha, mismatch!.ExpectedSha256);
                Assert.Equal(TofuCertificatePinStore.ComputeSha256Hex(certB.GetEncoded()), mismatch.PresentedSha256);
                Assert.Contains("relay-b.among.us", mismatch.PresentedSubject);
            }

            // 4. Connect with --pin-accept-new re-pins to certB
            await using (var serverB2 = new InProcessDtlsEchoServer(certB, keyB))
            {
                var profile = ServerProfile.Live("na", certsDirectory: tempCerts, acceptNewPin: true);
                await using var transport = profile.CreateTransport();
                await transport.ConnectAsync(serverB2.EndPoint);
                Assert.Equal(TofuCertificatePinStore.ComputeSha256Hex(certB.GetEncoded()), File.ReadAllText(shaPath).Trim());
            }
        }
        finally
        {
            if (Directory.Exists(tempCerts))
            {
                Directory.Delete(tempCerts, recursive: true);
            }
        }
    }

    [Fact]
    public void HazelDtlsAuth_FramingAndPrfAndNonceExtraction_MatchLiveProtocol()
    {
        var gameEp = new IPEndPoint(IPAddress.Parse("170.23.113.161"), 22423);
        Assert.Equal(22425, HazelDtlsAuthClient.GetAuthEndPoint(gameEp).Port);

        var hello = HazelDtlsAuthClient.BuildAuthHelloPayload(50663650, Platforms.Android, "tok", "");
        // [08 00 00 00] | int32(50663650=0x030510E2) | byte(7) | str("tok") | str("")
        Assert.Equal(new byte[] { 0x08, 0x00, 0x00, 0x00, 0xE2, 0x10, 0x05, 0x03, 0x07, 0x03, (byte)'t', (byte)'o', (byte)'k', 0x00 }, hello);

        byte[] noncePkt = [0x01, 0x00, 0x00, 0x04, 0x00, 0x01, 0x06, 0xEB, 0xF5, 0x03];
        Assert.True(HazelDtlsAuthClient.TryExtractAuthNonce(noncePkt, out var seq, out var nonce));
        Assert.Equal(((byte)0, (byte)0), seq);
        Assert.Equal(0x03F5EB06u, nonce);
        Assert.False(HazelDtlsAuthClient.TryExtractAuthNonce([0x09, 0x01, 0x01, 0x00, 0x00, 0x0B], out _, out _));

        // Verify AES-128-GCM 0-wire-nonce sealing/opening and Hazel PRF determinism
        var prf1 = HazelDtlsAuthClient.HazelPrfSha256(new byte[32], "master secert", new byte[64], 48);
        var prf2 = HazelDtlsAuthClient.HazelPrfSha256(new byte[32], "master secert", new byte[64], 48);
        Assert.Equal(prf1, prf2);
        var sealedCt = HazelDtlsAuthClient.SealAesGcm(prf1[0..16], prf1[32..36], 0x17, 1, 2, hello);
        Assert.Equal(hello.Length + 16, sealedCt.Length);
        var opened = HazelDtlsAuthClient.OpenAesGcm(prf1[0..16], prf1[32..36], 0x17, 1, 2, sealedCt);
        Assert.Equal(hello, opened);

        // Verify PlatformRecord.WriteHandshake serializes authNonce in lastNonceReceived slot
        using var w = MessageWriter.Get(MessageType.Unreliable);
        PlatformRecord.WriteHandshake(w, "Bean", "dev-1", lastNonceReceived: 0x03F5EB06u);
        var handshakeBytes = w.ToByteArray(includeHeader: false);
        // 4B ClientVersion + (1 + 4)B DisplayName -> offset 9 is 4B uint32 nonce LE
        Assert.Equal(0x03F5EB06u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(handshakeBytes.AsSpan(9, 4)));
    }

    private static async Task<List<(IRootMessage Decoded, byte[] WirePayload)>> RunEchoRoundTripAsync(
        bool useDtls,
        string certsDir,
        IRootMessage[] messages)
    {
        var results = new List<(IRootMessage, byte[])>(messages.Length);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        if (!useDtls)
        {
            using var server = new InProcessUdpEchoServer();
            var profile = ServerProfile.Local("127.0.0.1", server.EndPoint.Port);
            await using var transport = profile.CreateTransport();
            await transport.ConnectAsync(server.EndPoint, cts.Token);
            var framer = new HazelFramer(transport);

            foreach (var msg in messages)
            {
                await framer.SendRootAsync(msg, MessageType.Reliable, cts.Token);
                var frame = await ReceiveNextAppFrameAsync(framer, cts.Token);
                Assert.Single(frame.Messages);
                results.Add((frame.Messages[0], frame.RawPayload));
            }

            return results;
        }
        else
        {
            var (cert, key) = GenerateSelfSignedRsaCert("CN=test-dtls.local");
            await using var server = new InProcessDtlsEchoServer(cert, key);
            var profile = ServerProfile.Private("https://127.0.0.1", useDtls: true, region: "test", certsDirectory: certsDir, acceptNewPin: true);
            await using var transport = profile.CreateTransport();
            await transport.ConnectAsync(server.EndPoint, cts.Token);
            var framer = new HazelFramer(transport);

            foreach (var msg in messages)
            {
                await framer.SendRootAsync(msg, MessageType.Reliable, cts.Token);
                var frame = await ReceiveNextAppFrameAsync(framer, cts.Token);
                Assert.Single(frame.Messages);
                results.Add((frame.Messages[0], frame.RawPayload));
            }

            return results;
        }
    }

    private static async Task<InboundFrame> ReceiveNextAppFrameAsync(HazelFramer framer, CancellationToken ct)
    {
        while (true)
        {
            var frame = await framer.ReceiveFrameAsync(ct);
            Assert.NotNull(frame);
            if (frame!.SendOption is UdpSendOption.Acknowledgement or UdpSendOption.Ping)
            {
                continue;
            }

            return frame;
        }
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException!)
        {
            if (cur is T typed) return typed;
        }

        return null;
    }

    private static (X509Certificate Cert, AsymmetricKeyParameter Key) GenerateSelfSignedRsaCert(string dn)
    {
        var random = new SecureRandom();
        var keyGen = new RsaKeyPairGenerator();
        keyGen.Init(new KeyGenerationParameters(random, 2048));
        var pair = keyGen.GenerateKeyPair();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ProbablePrime(120, random));
        var name = new X509Name(dn);
        gen.SetIssuerDN(name);
        gen.SetSubjectDN(name);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(1));
        gen.SetPublicKey(pair.Public);

        var signer = new Asn1SignatureFactory("SHA256WITHRSA", pair.Private, random);
        var cert = gen.Generate(signer);
        return (cert, pair.Private);
    }

    private sealed class InProcessUdpEchoServer : IDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public InProcessUdpEchoServer()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loop = Task.Run(RunAsync);
        }

        public IPEndPoint EndPoint { get; }

        private async Task RunAsync()
        {
            var buf = new byte[65536];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await _socket.ReceiveFromAsync(buf, SocketFlags.None, remote, _cts.Token);
                    var data = buf.AsMemory(0, res.ReceivedBytes);
                    if (data.Length > 0 && data.Span[0] == (byte)UdpSendOption.Acknowledgement)
                    {
                        continue;
                    }

                    await _socket.SendToAsync(data, SocketFlags.None, res.RemoteEndPoint, _cts.Token);
                }
                catch
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _socket.Close();
            _socket.Dispose();
        }
    }

    private sealed class InProcessDtlsEchoServer : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public InProcessDtlsEchoServer(X509Certificate cert, AsymmetricKeyParameter key)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loop = Task.Run(() => Run(cert, key));
        }

        public IPEndPoint EndPoint { get; }

        private void Run(X509Certificate cert, AsymmetricKeyParameter key)
        {
            var datagram = new ServerSocketDatagramTransport(_socket);
            var server = new TestDtlsServer(cert, key);
            var protocol = new DtlsServerProtocol();
            try
            {
                var transport = protocol.Accept(server, datagram);
                var buf = new byte[transport.GetReceiveLimit()];
                while (!_cts.IsCancellationRequested)
                {
                    var n = transport.Receive(buf, 0, buf.Length, 50);
                    if (n > 0)
                    {
                        if (buf[0] == (byte)UdpSendOption.Acknowledgement)
                        {
                            continue;
                        }

                        transport.Send(buf, 0, n);
                    }
                }
            }
            catch
            {
                // expected on teardown or failed handshake test
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _socket.Close(); } catch { /* ignore */ }
            _socket.Dispose();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        }

        private sealed class TestDtlsServer : DefaultTlsServer
        {
            private readonly X509Certificate _cert;
            private readonly AsymmetricKeyParameter _key;

            public TestDtlsServer(X509Certificate cert, AsymmetricKeyParameter key)
                : base(new BcTlsCrypto(new SecureRandom()))
            {
                _cert = cert;
                _key = key;
            }

            protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions() => [Org.BouncyCastle.Tls.ProtocolVersion.DTLSv12];

            protected override TlsCredentialedSigner GetRsaSignerCredentials()
            {
                var crypto = (BcTlsCrypto)Crypto;
                var tlsCert = crypto.CreateCertificate(_cert.GetEncoded());
                var chain = new Certificate([tlsCert]);
                return new BcDefaultTlsCredentialedSigner(
                    new TlsCryptoParameters(m_context),
                    crypto,
                    _key,
                    chain,
                    SignatureAndHashAlgorithm.rsa_pss_rsae_sha256);
            }
        }

        private sealed class ServerSocketDatagramTransport : DatagramTransport
        {
            private readonly Socket _socket;
            private EndPoint? _peer;

            public ServerSocketDatagramTransport(Socket socket) => _socket = socket;

            public int GetReceiveLimit() => 1400;
            public int GetSendLimit() => 1400;

            public int Receive(byte[] buf, int off, int len, int waitMillis) =>
                Receive(buf.AsSpan(off, len), waitMillis);

            public int Receive(Span<byte> buf, int waitMillis)
            {
                _socket.ReceiveTimeout = Math.Max(1, waitMillis);
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    var bytes = new byte[buf.Length];
                    var read = _socket.ReceiveFrom(bytes, ref remote);
                    _peer ??= remote;
                    bytes.AsSpan(0, read).CopyTo(buf);
                    return read;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock)
                {
                    return -1;
                }
                catch
                {
                    return -1;
                }
            }

            public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));

            public void Send(ReadOnlySpan<byte> buf)
            {
                if (_peer is not null)
                {
                    _socket.SendTo(buf.ToArray(), _peer);
                }
            }

            public void Close()
            {
                try { _socket.Close(); } catch { /* ignore */ }
            }
        }
    }
}
