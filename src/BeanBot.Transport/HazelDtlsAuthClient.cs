using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.RootMessages;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;

namespace BeanBot.Transport;

/// <summary>
/// Implements Innersloth's custom Hazel DTLS authentication client connecting to
/// &lt;relayIp&gt;:&lt;relayPort + 2&gt; to exchange a MatchmakerToken for a 4-byte UDP session nonce.
///
/// Protocol quirks required by live Among Us relays (verified against willardf/Hazel-Networking):
/// 1. Record header uses ProtocolVersion.UDP (0x0000) and sequence numbers start at 1 per epoch.
/// 2. ClientHello #2 after HelloVerifyRequest retains message_seq = 0; ClientKeyExchange uses 5; Finished uses 6.
/// 3. PRF label for master secret is literally "master secert", and ExpandSecret mimics .NET
///    HMACSHA256.OutputBlockSize (1), overwriting bytes [0..31] of (1 + seed.Length) buffer each round.
/// 4. AES-128-GCM writes 0 explicit nonce bytes on the wire (12B nonce = iv4 || epoch2 || seq6).
/// 5. ServerKeyExchange signs only the 36-byte curve+pubkey params (without clientRandom || serverRandom).
/// </summary>
public sealed class HazelDtlsAuthClient
{
    private const ushort ProtocolVersionUdp = 0x0000;
    private const ushort CipherSuiteEcdheRsaAes128GcmSha256 = 0xC02F;
    private const ushort NamedCurveX25519 = 0x001D;

    private const byte ContentTypeChangeCipherSpec = 0x14;
    private const byte ContentTypeAlert = 0x15;
    private const byte ContentTypeHandshake = 0x16;
    private const byte ContentTypeApplicationData = 0x17;

    private const byte HandshakeClientHello = 0x01;
    private const byte HandshakeServerHello = 0x02;
    private const byte HandshakeHelloVerifyRequest = 0x03;
    private const byte HandshakeCertificate = 0x0B;
    private const byte HandshakeServerKeyExchange = 0x0C;
    private const byte HandshakeServerHelloDone = 0x0E;
    private const byte HandshakeClientKeyExchange = 0x10;
    private const byte HandshakeFinished = 0x14;

    private readonly string _region;
    private readonly TofuCertificatePinStore? _pinStore;
    private readonly bool _acceptNewPin;
    private readonly ILogger<HazelDtlsAuthClient>? _logger;

    public HazelDtlsAuthClient(
        string region = "na",
        TofuCertificatePinStore? pinStore = null,
        bool acceptNewPin = false,
        ILogger<HazelDtlsAuthClient>? logger = null)
    {
        _region = region;
        _pinStore = pinStore;
        _acceptNewPin = acceptNewPin;
        _logger = logger;
    }

    public static IPEndPoint GetAuthEndPoint(IPEndPoint gameRelayEndPoint)
    {
        ArgumentNullException.ThrowIfNull(gameRelayEndPoint);
        return new IPEndPoint(gameRelayEndPoint.Address, gameRelayEndPoint.Port + 2);
    }

    /// <summary>
    /// Completes Hazel DTLS handshake + AuthHello against (relayIp, relayPort + 2)
    /// and returns the uint32 session nonce for the subsequent plain UDP Hello on relayPort.
    /// </summary>
    public async Task<uint> AuthenticateAsync(
        IPEndPoint gameRelayEndPoint,
        int clientVersion,
        Platforms platform,
        string matchmakerToken,
        string friendCode = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gameRelayEndPoint);
        if (string.IsNullOrWhiteSpace(matchmakerToken))
        {
            throw new ArgumentException("Matchmaker token is required for Hazel DTLS auth.", nameof(matchmakerToken));
        }

        var authEndPoint = GetAuthEndPoint(gameRelayEndPoint);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(authEndPoint);

        var session = await PerformHandshakeAsync(udp, ct).ConfigureAwait(false);
        var helloPt = BuildAuthHelloPayload(clientVersion, platform, matchmakerToken, friendCode);
        var encHello = SealAesGcm(session.ClientKey, session.ClientIv, ContentTypeApplicationData, 1, 2, helloPt);
        await udp.SendAsync(BuildDtlsRecord(ContentTypeApplicationData, 1, 2, encHello), ct).ConfigureAwait(false);

        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(6));

        while (!authCts.IsCancellationRequested)
        {
            UdpReceiveResult rx;
            try
            {
                rx = await udp.ReceiveAsync(authCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out in Stage 4 (AuthNonce) waiting for response from {authEndPoint}.");
            }

            foreach (var rec in ParseDtlsRecords(rx.Buffer))
            {
                if (rec.ContentType == ContentTypeAlert && rec.Epoch == 1 && rec.Payload.Length > 16)
                {
                    var alertPt = OpenAesGcm(session.ServerKey, session.ServerIv, ContentTypeAlert, rec.Epoch, rec.Sequence, rec.Payload);
                    throw new IOException($"Hazel DTLS AuthServer sent encrypted alert {Convert.ToHexString(alertPt)}.");
                }

                if (rec.ContentType != ContentTypeApplicationData)
                {
                    continue;
                }

                var pt = OpenAesGcm(session.ServerKey, session.ServerIv, ContentTypeApplicationData, rec.Epoch, rec.Sequence, rec.Payload);
                if (pt.Length == 0)
                {
                    continue;
                }

                if (pt[0] == (byte)UdpSendOption.Disconnect)
                {
                    var reasonText = "Unknown";
                    if (pt.Length > 1)
                    {
                        using var dcReader = MessageReader.Get(pt[1..]);
                        var dc = DisconnectMessage.Deserialize(dcReader);
                        reasonText = dc.Reason == DisconnectReason.Custom
                            ? $"{dc.Reason} ({dc.CustomMessage})"
                            : (dc.Reason?.ToString() ?? "None");
                    }

                    throw new InvalidOperationException($"DTLS AuthServer rejected connection: {reasonText}.");
                }

                if (TryExtractAuthNonce(pt, out var seqBytes, out var nonce))
                {
                    byte[] ack = [(byte)UdpSendOption.Acknowledgement, seqBytes.SeqHi, seqBytes.SeqLo, 0xFF];
                    var encAck = SealAesGcm(session.ClientKey, session.ClientIv, ContentTypeApplicationData, 1, 3, ack);
                    await udp.SendAsync(BuildDtlsRecord(ContentTypeApplicationData, 1, 3, encAck), ct).ConfigureAwait(false);

                    byte[] dcPkt = [(byte)UdpSendOption.Disconnect];
                    var encDc = SealAesGcm(session.ClientKey, session.ClientIv, ContentTypeApplicationData, 1, 4, dcPkt);
                    await udp.SendAsync(BuildDtlsRecord(ContentTypeApplicationData, 1, 4, encDc), ct).ConfigureAwait(false);

                    _logger?.LogDebug("DTLS AuthServer {EndPoint} issued nonce 0x{Nonce:X8}.", authEndPoint, nonce);
                    return nonce;
                }

                if (pt[0] == 0x0C && pt.Length >= 3) // Ping
                {
                    byte[] ack = [(byte)UdpSendOption.Acknowledgement, pt[1], pt[2], 0xFF];
                    var encAck = SealAesGcm(session.ClientKey, session.ClientIv, ContentTypeApplicationData, 1, 3, ack);
                    await udp.SendAsync(BuildDtlsRecord(ContentTypeApplicationData, 1, 3, encAck), ct).ConfigureAwait(false);
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out in Stage 4 (AuthNonce) waiting for response from {authEndPoint}.");
    }

    /// <summary>
    /// Completes just the Hazel DTLS handshake against an Auth endpoint to verify or pin its certificate.
    /// </summary>
    public async Task VerifyOrPinHandshakeAsync(IPEndPoint dtlsAuthEndPoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dtlsAuthEndPoint);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(dtlsAuthEndPoint);
        var session = await PerformHandshakeAsync(udp, ct).ConfigureAwait(false);
        byte[] dcPkt = [(byte)UdpSendOption.Disconnect];
        var encDc = SealAesGcm(session.ClientKey, session.ClientIv, ContentTypeApplicationData, 1, 2, dcPkt);
        await udp.SendAsync(BuildDtlsRecord(ContentTypeApplicationData, 1, 2, encDc), ct).ConfigureAwait(false);
    }

    private async Task<DtlsKeys> PerformHandshakeAsync(UdpClient udp, CancellationToken ct)
    {
        var clientRandom = new byte[32];
        RandomNumberGenerator.Fill(clientRandom);
        var ch1Body = BuildClientHelloBody(clientRandom, ReadOnlySpan<byte>.Empty);
        var ch1Record = BuildDtlsRecord(ContentTypeHandshake, 0, 1, BuildHandshakeFragment(HandshakeClientHello, 0, ch1Body));
        await udp.SendAsync(ch1Record, ct).ConfigureAwait(false);

        // Stage 1: ClientHello #1 <-> HelloVerifyRequest (with UDP retransmit)
        byte[]? cookie = null;
        using (var hvrCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            hvrCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (cookie is null)
                {
                    using var slice = CancellationTokenSource.CreateLinkedTokenSource(hvrCts.Token);
                    slice.CancelAfter(TimeSpan.FromMilliseconds(1200));
                    UdpReceiveResult rx;
                    try
                    {
                        rx = await udp.ReceiveAsync(slice.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!hvrCts.IsCancellationRequested)
                    {
                        await udp.SendAsync(ch1Record, ct).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var rec in ParseDtlsRecords(rx.Buffer))
                    {
                        if (rec.ContentType != ContentTypeHandshake)
                        {
                            continue;
                        }

                        var hsBuf = rec.Payload;
                        for (int hpos = 0; hpos + 12 <= hsBuf.Length;)
                        {
                            byte hsType = hsBuf[hpos];
                            int fragLen = ReadUInt24BigEndian(hsBuf, hpos + 9);
                            if (hpos + 12 + fragLen > hsBuf.Length)
                            {
                                break;
                            }

                            if (hsType == HandshakeHelloVerifyRequest && fragLen >= 3)
                            {
                                var cookieLen = hsBuf[hpos + 14];
                                if (hpos + 15 + cookieLen <= hsBuf.Length)
                                {
                                    cookie = hsBuf.AsSpan(hpos + 15, cookieLen).ToArray();
                                    break;
                                }
                            }

                            hpos += 12 + fragLen;
                        }
                        if (cookie is not null)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out in Stage 1 waiting for HelloVerifyRequest.");
            }
        }

        // Stage 2: ClientHello #2 (msg_seq=0) <-> ServerHello + Certificate + ServerKeyExchange + ServerHelloDone
        var ch2Hs = BuildHandshakeFragment(HandshakeClientHello, 0, BuildClientHelloBody(clientRandom, cookie));
        var ch2Record = BuildDtlsRecord(ContentTypeHandshake, 0, 2, ch2Hs);
        var transcript = new List<byte>(2048);
        transcript.AddRange(ch2Hs);
        await udp.SendAsync(ch2Record, ct).ConfigureAwait(false);

        byte[]? serverRandom = null;
        byte[]? serverX25519Pub = null;
        byte[]? skeRawBody = null;
        byte[]? fullCertMsg = null;
        var certFragments = new Dictionary<int, byte[]>();
        int? certTotalLen = null;
        ushort certSeq = 0;

        byte[]? shBody = null;
        ushort shSeq = 0;
        byte[]? skeBody = null;
        ushort skeSeq = 0;
        byte[]? shdBody = null;
        ushort shdSeq = 0;

        using (var serverFlightCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            serverFlightCts.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                while (shdBody is null || fullCertMsg is null)
                {
                    using var slice = CancellationTokenSource.CreateLinkedTokenSource(serverFlightCts.Token);
                    slice.CancelAfter(TimeSpan.FromMilliseconds(1200));
                    UdpReceiveResult rx;
                    try
                    {
                        rx = await udp.ReceiveAsync(slice.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!serverFlightCts.IsCancellationRequested)
                    {
                        await udp.SendAsync(ch2Record, ct).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var rec in ParseDtlsRecords(rx.Buffer))
                    {
                        if (rec.ContentType != ContentTypeHandshake)
                        {
                            continue;
                        }

                        var hs = rec.Payload;
                        for (int hpos = 0; hpos + 12 <= hs.Length;)
                        {
                            byte hsType = hs[hpos];
                            int totalLen = ReadUInt24BigEndian(hs, hpos + 1);
                            ushort msgSeq = BinaryPrimitives.ReadUInt16BigEndian(hs.AsSpan(hpos + 4));
                            int fragOff = ReadUInt24BigEndian(hs, hpos + 6);
                            int fragLen = ReadUInt24BigEndian(hs, hpos + 9);
                            if (hpos + 12 + fragLen > hs.Length)
                            {
                                break;
                            }

                            var fragBody = hs.AsSpan(hpos + 12, fragLen).ToArray();
                            hpos += 12 + fragLen;

                            switch (hsType)
                            {
                                case HandshakeServerHello when shBody is null:
                                    serverRandom = fragBody.AsSpan(2, 32).ToArray();
                                    shSeq = msgSeq;
                                    shBody = fragBody;
                                    break;

                                case HandshakeCertificate when fullCertMsg is null:
                                    certSeq = msgSeq;
                                    certTotalLen ??= totalLen;
                                    certFragments[fragOff] = fragBody;
                                    if (certFragments.Values.Sum(f => f.Length) >= certTotalLen.Value)
                                    {
                                        fullCertMsg = new byte[certTotalLen.Value];
                                        foreach (var kv in certFragments)
                                        {
                                            kv.Value.CopyTo(fullCertMsg, kv.Key);
                                        }
                                    }
                                    break;

                                case HandshakeServerKeyExchange when skeBody is null:
                                    skeSeq = msgSeq;
                                    skeBody = fragBody;
                                    skeRawBody = fragBody;
                                    if (fragBody.Length < 40 || fragBody[0] != 0x03 ||
                                        BinaryPrimitives.ReadUInt16BigEndian(fragBody.AsSpan(1)) != NamedCurveX25519 ||
                                        fragBody[3] != 32)
                                    {
                                        throw new InvalidDataException("Unexpected ServerKeyExchange curve parameters.");
                                    }
                                    serverX25519Pub = fragBody.AsSpan(4, 32).ToArray();
                                    break;

                                case HandshakeServerHelloDone when shdBody is null:
                                    shdSeq = msgSeq;
                                    shdBody = fragBody;
                                    break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out in Stage 2 (ServerHello flight): sh={shBody != null}, certFrags={certFragments.Count}, certFull={fullCertMsg != null}, ske={skeBody != null}, shd={shdBody != null}.");
            }
        }

        if (serverRandom is null || fullCertMsg is null || serverX25519Pub is null || skeRawBody is null)
        {
            throw new InvalidOperationException("Incomplete Hazel DTLS ServerHello flight.");
        }

        // Parse leaf DER cert from Certificate message ([3B listLen][3B cert0Len][cert0Der...])
        var leafCertDer = ExtractLeafCertificateDer(fullCertMsg);
        VerifyCertificateAndSignature(leafCertDer, skeRawBody);

        // Append server flight in canonical handshake order
        transcript.AddRange(BuildHandshakeFragment(HandshakeServerHello, shSeq, shBody!));
        transcript.AddRange(BuildHandshakeFragment(HandshakeCertificate, certSeq, fullCertMsg));
        transcript.AddRange(BuildHandshakeFragment(HandshakeServerKeyExchange, skeSeq, skeBody!));
        transcript.AddRange(BuildHandshakeFragment(HandshakeServerHelloDone, shdSeq, shdBody!));

        // ECDHE X25519 agreement
        var privBytes = new byte[32];
        RandomNumberGenerator.Fill(privBytes);
        var clientPriv = new X25519PrivateKeyParameters(privBytes, 0);
        var clientPub = clientPriv.GeneratePublicKey().GetEncoded();
        var serverPub = new X25519PublicKeyParameters(serverX25519Pub, 0);
        var agree = new X25519Agreement();
        agree.Init(clientPriv);
        var premaster = new byte[agree.AgreementSize];
        agree.CalculateAgreement(serverPub, premaster, 0);

        var crsr = new byte[64];
        clientRandom.CopyTo(crsr, 0);
        serverRandom.CopyTo(crsr, 32);
        var srcr = new byte[64];
        serverRandom.CopyTo(srcr, 0);
        clientRandom.CopyTo(srcr, 32);

        var masterSecret = HazelPrfSha256(premaster, "master secert", crsr, 48);
        var keyBlock = HazelPrfSha256(masterSecret, "key expansion", srcr, 40);
        var clientKey = keyBlock[0..16];
        var serverKey = keyBlock[16..32];
        var clientIv = keyBlock[32..36];
        var serverIv = keyBlock[36..40];

        // ClientKeyExchange requires msg_seq = 5, Finished requires msg_seq = 6.
        var ckeBody = new byte[33];
        ckeBody[0] = 32;
        clientPub.CopyTo(ckeBody, 1);
        var ckeHs = BuildHandshakeFragment(HandshakeClientKeyExchange, 5, ckeBody);
        transcript.AddRange(ckeHs);

        var handshakeHash = SHA256.HashData(transcript.ToArray());
        var clientVerify = HazelPrfSha256(masterSecret, "client finished", handshakeHash, 12);
        var expectedServerVerify = HazelPrfSha256(masterSecret, "server finished", handshakeHash, 12);

        var finishedHs = BuildHandshakeFragment(HandshakeFinished, 6, clientVerify);
        var encFinished = SealAesGcm(clientKey, clientIv, ContentTypeHandshake, 1, 1, finishedHs);

        var r1 = BuildDtlsRecord(ContentTypeHandshake, 0, 3, ckeHs);
        var r2 = BuildDtlsRecord(ContentTypeChangeCipherSpec, 0, 4, [0x01]);
        var r3 = BuildDtlsRecord(ContentTypeHandshake, 1, 1, encFinished);
        var flight = new byte[r1.Length + r2.Length + r3.Length];
        r1.CopyTo(flight, 0);
        r2.CopyTo(flight, r1.Length);
        r3.CopyTo(flight, r1.Length + r2.Length);
        await udp.SendAsync(flight, ct).ConfigureAwait(false);

        bool gotServerCcs = false;
        bool verifiedServerFinished = false;
        using (var finCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            finCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (!verifiedServerFinished)
                {
                    var rx = await udp.ReceiveAsync(finCts.Token).ConfigureAwait(false);
                    foreach (var rec in ParseDtlsRecords(rx.Buffer))
                    {
                        if (rec.ContentType == ContentTypeChangeCipherSpec)
                        {
                            gotServerCcs = true;
                        }
                        else if (gotServerCcs && rec.ContentType == ContentTypeHandshake && rec.Epoch == 1)
                        {
                            var serverFinPt = OpenAesGcm(serverKey, serverIv, ContentTypeHandshake, rec.Epoch, rec.Sequence, rec.Payload);
                            if (serverFinPt.Length < 24 || serverFinPt[0] != HandshakeFinished ||
                                !CryptographicOperations.FixedTimeEquals(serverFinPt.AsSpan(12, 12), expectedServerVerify))
                            {
                                throw new CryptographicException("Server DTLS Finished verify_data mismatch.");
                            }
                            verifiedServerFinished = true;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out in Stage 3 (Server Finished): gotServerCcs={gotServerCcs}.");
            }
        }

        return new DtlsKeys(clientKey, clientIv, serverKey, serverIv);
    }

    private void VerifyCertificateAndSignature(byte[] leafCertDer, byte[] skeRawBody)
    {
        using var cert = new X509Certificate2(leafCertDer);
        _pinStore?.VerifyOrPin(_region, leafCertDer, cert.Subject, _acceptNewPin);

        // SKE layout: [3B curve header][1B pubLen=32][32B pubKey][2B sigAlg (04 01)][2B sigLen BE][signature]
        if (skeRawBody.Length < 40)
        {
            throw new InvalidDataException("ServerKeyExchange too short for signature.");
        }

        ushort sigLen = BinaryPrimitives.ReadUInt16BigEndian(skeRawBody.AsSpan(38));
        if (40 + sigLen > skeRawBody.Length)
        {
            throw new InvalidDataException("ServerKeyExchange signature truncated.");
        }

        var paramsSigned = skeRawBody.AsSpan(0, 36);
        var signature = skeRawBody.AsSpan(40, sigLen);
        using var rsa = cert.GetRSAPublicKey()
            ?? throw new CryptographicException("Server certificate does not contain an RSA public key.");
        if (!rsa.VerifyData(paramsSigned, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            throw new CryptographicException("ServerKeyExchange RSA-SHA256 signature verification failed.");
        }
    }

    internal static byte[] ExtractLeafCertificateDer(byte[] certificateMessageBody)
    {
        if (certificateMessageBody.Length < 6)
        {
            throw new InvalidDataException("Certificate message body too short.");
        }

        int leafLen = ReadUInt24BigEndian(certificateMessageBody, 3);
        if (6 + leafLen > certificateMessageBody.Length)
        {
            throw new InvalidDataException("Leaf certificate truncated inside Certificate message.");
        }

        return certificateMessageBody.AsSpan(6, leafLen).ToArray();
    }

    /// <summary>
    /// Builds the plaintext Hazel AuthHello payload sent inside DTLS ApplicationData to (relayIp, relayPort + 2).
    /// Layout: [0x08, 0x00, 0x00, 0x00] | ClientVersion (int32 LE) | Platform (byte) | MatchmakerToken (string) | FriendCode (string).
    /// </summary>
    public static byte[] BuildAuthHelloPayload(
        int clientVersion,
        Platforms platform,
        string matchmakerToken,
        string friendCode = "")
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        writer.Write(clientVersion);
        writer.Write((byte)platform);
        writer.Write(matchmakerToken);
        writer.Write(friendCode);
        var body = writer.ToByteArray(includeHeader: false);
        var packet = new byte[4 + body.Length];
        packet[0] = (byte)UdpSendOption.Hello;
        packet[1] = 0x00;
        packet[2] = 0x00;
        packet[3] = 0x00; // Hazel version 0
        body.CopyTo(packet, 4);
        return packet;
    }

    /// <summary>
    /// Parses the 10-byte reliable AuthServer nonce response: [0x01][seqHi][seqLo][0x04][0x00][0x01][uint32 LE nonce].
    /// </summary>
    public static bool TryExtractAuthNonce(ReadOnlySpan<byte> plaintext, out (byte SeqHi, byte SeqLo) seqBytes, out uint nonce)
    {
        if (plaintext.Length == 10 &&
            plaintext[0] == (byte)UdpSendOption.Reliable &&
            plaintext[3] == 0x04 &&
            plaintext[4] == 0x00 &&
            plaintext[5] == 0x01)
        {
            seqBytes = (plaintext[1], plaintext[2]);
            nonce = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.Slice(6, 4));
            return true;
        }

        seqBytes = default;
        nonce = 0;
        return false;
    }

    /// <summary>
    /// Reproduces Hazel's PrfSha256.ExpandSecret (including OutputBlockSize=1 clobbering of seed bytes [0..31]).
    /// </summary>
    internal static byte[] HazelPrfSha256(byte[] secret, string label, byte[] seed, int outputLength)
    {
        var labelBytes = Encoding.ASCII.GetBytes(label);
        var roundSeed = new byte[labelBytes.Length + seed.Length];
        labelBytes.CopyTo(roundSeed, 0);
        seed.CopyTo(roundSeed, labelBytes.Length);

        const int hashLen = 32;
        using var hmac = new HMACSHA256(secret);
        var result = new byte[outputLength];

        var input = new byte[1 + roundSeed.Length];
        roundSeed.CopyTo(input, 1);

        byte[] hashA = roundSeed;
        for (var offset = 0; offset < outputLength; offset += hashLen)
        {
            hashA = hmac.ComputeHash(hashA);
            hashA.CopyTo(input, 0);
            var block = hmac.ComputeHash(input);
            var copyLen = Math.Min(hashLen, outputLength - offset);
            block.AsSpan(0, copyLen).CopyTo(result.AsSpan(offset, copyLen));
        }

        return result;
    }

    internal static byte[] SealAesGcm(byte[] key, byte[] iv4, byte contentType, ushort epoch, ulong sequence, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        WriteGcmNonce(iv4, epoch, sequence, nonce);

        Span<byte> aad = stackalloc byte[13];
        WriteRecordHeader(aad, contentType, epoch, sequence, (ushort)plaintext.Length);

        var output = new byte[plaintext.Length + 16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length, 16), aad);
        return output;
    }

    internal static byte[] OpenAesGcm(byte[] key, byte[] iv4, byte contentType, ushort epoch, ulong sequence, ReadOnlySpan<byte> ciphertextWithTag)
    {
        if (ciphertextWithTag.Length < 16)
        {
            throw new CryptographicException("DTLS ciphertext shorter than 16-byte GCM tag.");
        }

        var ptLen = ciphertextWithTag.Length - 16;
        Span<byte> nonce = stackalloc byte[12];
        WriteGcmNonce(iv4, epoch, sequence, nonce);

        Span<byte> aad = stackalloc byte[13];
        WriteRecordHeader(aad, contentType, epoch, sequence, (ushort)ptLen);

        var plaintext = new byte[ptLen];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, ciphertextWithTag[..ptLen], ciphertextWithTag[ptLen..], plaintext, aad);
        return plaintext;
    }

    internal static byte[] BuildDtlsRecord(byte contentType, ushort epoch, ulong sequence, ReadOnlySpan<byte> payload)
    {
        var record = new byte[13 + payload.Length];
        WriteRecordHeader(record, contentType, epoch, sequence, (ushort)payload.Length);
        payload.CopyTo(record.AsSpan(13));
        return record;
    }

    private static void WriteRecordHeader(Span<byte> dest, byte contentType, ushort epoch, ulong sequence, ushort length)
    {
        dest[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(dest[1..], ProtocolVersionUdp);
        BinaryPrimitives.WriteUInt16BigEndian(dest[3..], epoch);
        Span<byte> seq8 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seq8, sequence);
        seq8[2..8].CopyTo(dest[5..]);
        BinaryPrimitives.WriteUInt16BigEndian(dest[11..], length);
    }

    private static void WriteGcmNonce(ReadOnlySpan<byte> iv4, ushort epoch, ulong sequence, Span<byte> nonce)
    {
        iv4.CopyTo(nonce[..4]);
        BinaryPrimitives.WriteUInt16BigEndian(nonce[4..], epoch);
        Span<byte> seq8 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seq8, sequence);
        seq8[2..8].CopyTo(nonce[6..]);
    }

    private static byte[] BuildHandshakeFragment(byte handshakeType, ushort msgSeq, ReadOnlySpan<byte> body)
    {
        var buf = new byte[12 + body.Length];
        buf[0] = handshakeType;
        WriteUInt24BigEndian(buf, 1, body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), msgSeq);
        WriteUInt24BigEndian(buf, 6, 0);
        WriteUInt24BigEndian(buf, 9, body.Length);
        body.CopyTo(buf.AsSpan(12));
        return buf;
    }

    private static byte[] BuildClientHelloBody(ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> cookie)
    {
        using var ms = new MemoryStream(128);
        ms.WriteByte(0x00);
        ms.WriteByte(0x00); // ProtocolVersion.UDP
        ms.Write(clientRandom);
        ms.WriteByte(0x01); // 1-byte SessionId length
        ms.WriteByte(0x01);
        ms.WriteByte((byte)cookie.Length);
        ms.Write(cookie);
        ms.Write([0x00, 0x02, (byte)(CipherSuiteEcdheRsaAes128GcmSha256 >> 8), (byte)(CipherSuiteEcdheRsaAes128GcmSha256 & 0xFF)]);
        ms.Write([0x01, 0x00]); // Compression: null
        ms.Write([0x00, 0x08, 0x00, 0x0A, 0x00, 0x04, 0x00, 0x02, (byte)(NamedCurveX25519 >> 8), (byte)NamedCurveX25519]);
        return ms.ToArray();
    }

    private static List<(byte ContentType, ushort Epoch, ulong Sequence, byte[] Payload)> ParseDtlsRecords(byte[] datagram)
    {
        var list = new List<(byte, ushort, ulong, byte[])>(4);
        var pos = 0;
        Span<byte> seq8 = stackalloc byte[8];
        while (pos + 13 <= datagram.Length)
        {
            byte type = datagram[pos];
            ushort epoch = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(pos + 3));
            seq8.Clear();
            datagram.AsSpan(pos + 5, 6).CopyTo(seq8[2..]);
            ulong seq = BinaryPrimitives.ReadUInt64BigEndian(seq8);
            ushort len = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(pos + 11));
            if (pos + 13 + len > datagram.Length)
            {
                break;
            }

            list.Add((type, epoch, seq, datagram.AsSpan(pos + 13, len).ToArray()));
            pos += 13 + len;
        }

        return list;
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> src, int offset) =>
        (src[offset] << 16) | (src[offset + 1] << 8) | src[offset + 2];

    private static void WriteUInt24BigEndian(Span<byte> dest, int offset, int value)
    {
        dest[offset] = (byte)(value >> 16);
        dest[offset + 1] = (byte)(value >> 8);
        dest[offset + 2] = (byte)value;
    }

    private sealed record DtlsKeys(byte[] ClientKey, byte[] ClientIv, byte[] ServerKey, byte[] ServerIv);
}
