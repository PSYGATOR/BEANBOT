using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.RootMessages;
using Microsoft.Extensions.Logging;

namespace BeanBot.Transport;

public sealed record InboundFrame(
    UdpSendOption SendOption,
    ushort? ReliableId,
    bool IsDuplicate,
    IReadOnlyList<IRootMessage> Messages,
    DisconnectMessage? Disconnect,
    byte[] RawPayload);

/// <summary>
/// Frames and parses Hazel packets (Normal, Reliable, Hello, Disconnect, Acknowledgement, Fragment, Ping)
/// uniformly over any ITransport (plain UDP or DTLS 1.2).
/// </summary>
public sealed class HazelFramer
{
    private const byte HazelProtocolVersion = 0;

    private readonly ITransport _transport;
    private readonly ProtocolRegistry _registry;
    private readonly AckTracker _ackTracker;
    private readonly FragmentReassembler _fragments;
    private readonly ILogger<HazelFramer>? _logger;

    public HazelFramer(
        ITransport transport,
        ProtocolRegistry? registry = null,
        AckTracker? ackTracker = null,
        FragmentReassembler? fragments = null,
        ILogger<HazelFramer>? logger = null)
    {
        _transport = transport;
        _registry = registry ?? ProtocolRegistry.Default;
        _ackTracker = ackTracker ?? new AckTracker();
        _fragments = fragments ?? new FragmentReassembler();
        _logger = logger;
    }

    public AckTracker AckTracker => _ackTracker;
    public FragmentReassembler Fragments => _fragments;
    public bool IgnoreUnknownRootMessages { get; set; }

    public async Task<ushort> SendHelloAsync(
        string displayName,
        string deviceId,
        Language language = Language.English,
        QuickChatModes chatMode = QuickChatModes.QuickChatOnly,
        CrossplayFlags crossplayFlags = CrossplayFlags.All,
        uint authNonce = 0u,
        CancellationToken ct = default)
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        writer.Write(HazelProtocolVersion);
        PlatformRecord.WriteHandshake(writer, displayName, deviceId, language, chatMode, crossplayFlags, authNonce);
        var handshakePayload = writer.ToByteArray(includeHeader: false);
        return await SendReliableFramedAsync(UdpSendOption.Hello, handshakePayload, ct).ConfigureAwait(false);
    }

    public Task<ushort?> SendReliableRootAsync(IRootMessage message, CancellationToken ct = default)
        => SendRootAsync(message, MessageType.Reliable, ct);

    public Task<ushort?> SendUnreliableRootAsync(IRootMessage message, CancellationToken ct = default)
        => SendRootAsync(message, MessageType.Unreliable, ct);

    public async Task<ushort?> SendRootAsync(IRootMessage message, MessageType delivery = MessageType.Reliable, CancellationToken ct = default)
    {
        var payload = _registry.EncodeRoot(message, delivery);
        if (delivery == MessageType.Reliable)
        {
            return await SendReliableFramedAsync(UdpSendOption.Reliable, payload, ct).ConfigureAwait(false);
        }

        var packet = new byte[1 + payload.Length];
        packet[0] = (byte)UdpSendOption.Normal;
        Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
        await _transport.SendDatagramAsync(packet, ct).ConfigureAwait(false);
        return null;
    }

    public async Task<ushort> SendPingAsync(CancellationToken ct = default)
    {
        return await SendReliableFramedAsync(UdpSendOption.Ping, [], ct).ConfigureAwait(false);
    }

    public async Task SendDisconnectAsync(DisconnectMessage? reason = null, CancellationToken ct = default)
    {
        if (reason is null)
        {
            await _transport.SendDatagramAsync(new byte[] { (byte)UdpSendOption.Disconnect }, ct).ConfigureAwait(false);
            return;
        }

        using var writer = MessageWriter.Get(MessageType.Unreliable);
        reason.Serialize(writer);
        var payload = writer.ToByteArray(includeHeader: false);
        var packet = new byte[1 + payload.Length];
        packet[0] = (byte)UdpSendOption.Disconnect;
        Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
        await _transport.SendDatagramAsync(packet, ct).ConfigureAwait(false);
    }

    public async Task<int> PumpRetransmitsAsync(CancellationToken ct = default)
    {
        var (toResend, timedOut) = _ackTracker.CollectRetransmits();
        if (timedOut.Count > 0)
        {
            throw new TimeoutException($"Reliable packet(s) timed out without ACK: {string.Join(", ", timedOut)}.");
        }

        foreach (var pkt in toResend)
        {
            await _transport.SendDatagramAsync(pkt, ct).ConfigureAwait(false);
        }

        return toResend.Count;
    }

    public async Task<InboundFrame?> ReceiveFrameAsync(CancellationToken ct = default)
    {
        var buffer = new byte[65536];
        var read = await _transport.ReceiveDatagramAsync(buffer, ct).ConfigureAwait(false);
        if (read <= 0)
        {
            return null;
        }

        var datagram = buffer.AsSpan(0, read).ToArray();
        return await ProcessDatagramAsync(datagram, ct).ConfigureAwait(false);
    }

    public async Task<InboundFrame> ProcessDatagramAsync(byte[] datagram, CancellationToken ct = default)
    {
        if (datagram.Length == 0)
        {
            throw new ProtocolException(null, datagram, "Empty Hazel datagram.");
        }

        var option = (UdpSendOption)datagram[0];
        try
        {
            switch (option)
            {
                case UdpSendOption.Normal:
                {
                    var payload = datagram.AsSpan(1).ToArray();
                    var messages = DecodeRootMessages(payload);
                    return new InboundFrame(option, null, false, messages, null, payload);
                }

                case UdpSendOption.Reliable:
                {
                    if (datagram.Length < 3)
                    {
                        throw new ProtocolException(datagram[0], datagram, "Truncated Reliable Hazel header.");
                    }

                    var id = (ushort)((datagram[1] << 8) | datagram[2]);
                    var (isNew, ack) = _ackTracker.ProcessInboundReliable(id);
                    await _transport.SendDatagramAsync(ack, ct).ConfigureAwait(false);

                    var payload = datagram.AsSpan(3).ToArray();
                    var messages = isNew ? DecodeRootMessages(payload) : Array.Empty<IRootMessage>();
                    return new InboundFrame(option, id, !isNew, messages, null, payload);
                }

                case UdpSendOption.Hello:
                case UdpSendOption.Ping:
                {
                    if (datagram.Length < 3)
                    {
                        throw new ProtocolException(datagram[0], datagram, $"Truncated {option} Hazel header.");
                    }

                    var id = (ushort)((datagram[1] << 8) | datagram[2]);
                    var (isNew, ack) = _ackTracker.ProcessInboundReliable(id);
                    await _transport.SendDatagramAsync(ack, ct).ConfigureAwait(false);
                    var payload = datagram.AsSpan(3).ToArray();
                    return new InboundFrame(option, id, !isNew, Array.Empty<IRootMessage>(), null, payload);
                }

                case UdpSendOption.Acknowledgement:
                {
                    _ackTracker.ProcessInboundAck(datagram);
                    return new InboundFrame(option, null, false, Array.Empty<IRootMessage>(), null, datagram);
                }

                case UdpSendOption.Disconnect:
                {
                    DisconnectMessage? reason = null;
                    if (datagram.Length > 1)
                    {
                        using var reader = MessageReader.Get(datagram, 1, datagram.Length - 1);
                        reason = DisconnectMessage.Deserialize(reader);
                    }

                    return new InboundFrame(option, null, false, Array.Empty<IRootMessage>(), reason, datagram.AsSpan(1).ToArray());
                }

                case UdpSendOption.Fragment:
                {
                    if (_fragments.TryAddFragment(datagram, out var reassembled) && reassembled is not null)
                    {
                        var messages = DecodeRootMessages(reassembled);
                        return new InboundFrame(option, null, false, messages, null, reassembled);
                    }

                    return new InboundFrame(option, null, false, Array.Empty<IRootMessage>(), null, Array.Empty<byte>());
                }

                default:
                    throw new ProtocolException(datagram[0], datagram, $"Unknown Hazel send option 0x{datagram[0]:x2}.");
            }
        }
        catch (ProtocolException ex)
        {
            _logger?.LogError(ex, "Protocol parse error: PacketId={PacketId}, Hex={HexDump}", ex.PacketId, ex.HexDump);
            throw;
        }
        catch (Exception ex)
        {
            var wrapped = new ProtocolException(datagram[0], datagram, $"Unhandled error parsing Hazel frame {option}: {ex.Message}", ex);
            _logger?.LogError(wrapped, "Protocol error: PacketId={PacketId}, Hex={HexDump}", wrapped.PacketId, wrapped.HexDump);
            throw wrapped;
        }
    }

    private async Task<ushort> SendReliableFramedAsync(UdpSendOption option, byte[] payload, CancellationToken ct)
    {
        var id = _ackTracker.NextSendId();
        var packet = new byte[3 + payload.Length];
        packet[0] = (byte)option;
        packet[1] = (byte)(id >> 8);
        packet[2] = (byte)id;
        Buffer.BlockCopy(payload, 0, packet, 3, payload.Length);

        _ackTracker.TrackOutbound(id, packet);
        await _transport.SendDatagramAsync(packet, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Waits for an inbound Hazel acknowledgement while pumping retransmits and throwing if the server disconnects.
    /// </summary>
    public async Task WaitForAcknowledgementAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                await PumpRetransmitsAsync(timeoutCts.Token).ConfigureAwait(false);
                using var sliceCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                sliceCts.CancelAfter(TimeSpan.FromMilliseconds(250));

                InboundFrame? frame;
                try
                {
                    frame = await ReceiveFrameAsync(sliceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
                {
                    continue;
                }

                if (frame is null)
                {
                    continue;
                }

                if (frame.Disconnect is { } dc)
                {
                    throw new IOException($"Server disconnected while awaiting acknowledgement: {dc.Reason} ({dc.CustomMessage})");
                }

                if (frame.SendOption == UdpSendOption.Acknowledgement)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fall through to timeout exception below.
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for Hazel acknowledgement.");
    }

    private IReadOnlyList<IRootMessage> DecodeRootMessages(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return Array.Empty<IRootMessage>();
        }

        using var outer = MessageReader.Get(payload);
        var list = new List<IRootMessage>();
        while (outer.BytesRemaining > 0)
        {
            using var msg = outer.ReadMessage();
            if (IgnoreUnknownRootMessages && !_registry.RootMessages.ContainsKey(msg.Tag))
            {
                _logger?.LogDebug("Skipping unrecognized root message 0x{Tag:x2} ({Bytes} bytes).", msg.Tag, msg.BytesRemaining);
                continue;
            }

            list.Add(_registry.DecodeRootMessage(msg));
        }

        return list;
    }
}
