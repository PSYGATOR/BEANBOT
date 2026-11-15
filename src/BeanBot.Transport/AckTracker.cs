using System.Collections.Concurrent;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;

namespace BeanBot.Transport;

/// <summary>
/// Tracks outbound reliable packets for retransmission and inbound reliable packets
/// for deduplication and 8-bit selective acknowledgement (5.3, Hazel UdpConnection.Reliable).
/// </summary>
public sealed class AckTracker
{
    private readonly ConcurrentDictionary<ushort, PendingPacket> _pendingOutbound = new();
    private readonly HashSet<ushort> _missingInbound = new();
    private readonly Func<long> _clockMs;

    private int _lastAllocatedId = -1;
    private ushort _receiveLast = ushort.MaxValue;
    private float _smoothedPingMs = 200f;

    public AckTracker(
        int initialResendMs = 250,
        int maxResendIntervalMs = 1000,
        int disconnectTimeoutMs = 5000,
        Func<long>? clockMs = null)
    {
        InitialResendMs = initialResendMs;
        MaxResendIntervalMs = maxResendIntervalMs;
        DisconnectTimeoutMs = disconnectTimeoutMs;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public int InitialResendMs { get; }
    public int MaxResendIntervalMs { get; }
    public int DisconnectTimeoutMs { get; }
    public float SmoothedPingMs => _smoothedPingMs;
    public int PendingCount => _pendingOutbound.Count;

    public ushort NextSendId() => (ushort)Interlocked.Increment(ref _lastAllocatedId);

    public void TrackOutbound(ushort id, byte[] wirePacket)
    {
        var now = _clockMs();
        var interval = Math.Clamp((int)(_smoothedPingMs * 2f), InitialResendMs, MaxResendIntervalMs);
        var entry = new PendingPacket(id, wirePacket, now, now + interval, interval);
        if (!_pendingOutbound.TryAdd(id, entry))
        {
            throw new InvalidOperationException($"Duplicate outbound reliable send-id {id}.");
        }
    }

    public int ProcessInboundAck(ReadOnlySpan<byte> ackPacket)
    {
        if (ackPacket.Length < 3 || ackPacket[0] != (byte)UdpSendOption.Acknowledgement)
        {
            throw new ProtocolException(ackPacket.Length > 0 ? ackPacket[0] : null, ackPacket, "Invalid ACK packet.");
        }

        var acked = 0;
        var id = (ushort)((ackPacket[1] << 8) | ackPacket[2]);
        if (AcknowledgeId(id))
        {
            acked++;
        }

        if (ackPacket.Length >= 4)
        {
            var mask = ackPacket[3];
            for (var i = 1; i <= 8; i++)
            {
                if ((mask & 1) != 0 && AcknowledgeId((ushort)(id - i)))
                {
                    acked++;
                }

                mask >>= 1;
            }
        }

        return acked;
    }

    public (bool IsNew, byte[] AckPacket) ProcessInboundReliable(ushort id)
    {
        var isNew = true;

        lock (_missingInbound)
        {
            var overwritePointer = (ushort)(_receiveLast - 32768);
            bool ahead = overwritePointer < _receiveLast
                ? (id > _receiveLast || id <= overwritePointer)
                : (id > _receiveLast && id <= overwritePointer);

            if (ahead)
            {
                if (id > _receiveLast)
                {
                    for (var i = (ushort)(_receiveLast + 1); i != id; i++)
                    {
                        _missingInbound.Add(i);
                    }
                }
                else
                {
                    var count = (ushort.MaxValue - _receiveLast) + id;
                    for (var i = 1; i <= count; i++)
                    {
                        _missingInbound.Add((ushort)(_receiveLast + i));
                    }
                }

                _receiveLast = id;
            }
            else if (!_missingInbound.Remove(id))
            {
                isNew = false;
            }
        }

        return (isNew, BuildAckPacket(id));
    }

    public byte[] BuildAckPacket(ushort id)
    {
        byte recentMask = 0;
        lock (_missingInbound)
        {
            for (var i = 1; i <= 8; i++)
            {
                if (!_missingInbound.Contains((ushort)(id - i)))
                {
                    recentMask |= (byte)(1 << (i - 1));
                }
            }
        }

        return
        [
            (byte)UdpSendOption.Acknowledgement,
            (byte)(id >> 8),
            (byte)id,
            recentMask,
        ];
    }

    public (IReadOnlyList<byte[]> PacketsToResend, IReadOnlyList<ushort> TimedOutIds) CollectRetransmits()
    {
        var now = _clockMs();
        var toResend = new List<byte[]>();
        var timedOut = new List<ushort>();

        foreach (var (id, pkt) in _pendingOutbound)
        {
            var lifetime = now - pkt.CreatedAtMs;
            if (lifetime >= DisconnectTimeoutMs)
            {
                if (_pendingOutbound.TryRemove(id, out _))
                {
                    timedOut.Add(id);
                }

                continue;
            }

            if (now >= pkt.NextRetryAtMs)
            {
                pkt.Retries++;
                pkt.CurrentIntervalMs = Math.Min(pkt.CurrentIntervalMs * 2, MaxResendIntervalMs);
                pkt.NextRetryAtMs = now + pkt.CurrentIntervalMs;
                toResend.Add(pkt.WirePacket);
            }
        }

        return (toResend, timedOut);
    }

    private bool AcknowledgeId(ushort id)
    {
        if (!_pendingOutbound.TryRemove(id, out var pkt))
        {
            return false;
        }

        var rtt = Math.Max(1f, _clockMs() - pkt.CreatedAtMs);
        _smoothedPingMs = Math.Max(50f, (_smoothedPingMs * 0.7f) + (rtt * 0.3f));
        return true;
    }

    private sealed class PendingPacket
    {
        public PendingPacket(ushort id, byte[] wirePacket, long createdAtMs, long nextRetryAtMs, int currentIntervalMs)
        {
            Id = id;
            WirePacket = wirePacket;
            CreatedAtMs = createdAtMs;
            NextRetryAtMs = nextRetryAtMs;
            CurrentIntervalMs = currentIntervalMs;
        }

        public ushort Id { get; }
        public byte[] WirePacket { get; }
        public long CreatedAtMs { get; }
        public long NextRetryAtMs { get; set; }
        public int CurrentIntervalMs { get; set; }
        public int Retries { get; set; }
    }
}

/// <summary>
/// Reassembles Hazel Fragment (0x0b) packets with strict total size cap and 5s orphan expiry (5.3, F11).
/// Wire format per fragment: [0x0b][streamId:u16 BE][fragmentIndex:u16 BE][totalFragments:u16 BE][chunk...]
/// </summary>
public sealed class FragmentReassembler
{
    public const int DefaultMaxTotalBufferedBytes = 256 * 1024;
    public const int DefaultMaxMessageBytes = 64 * 1024;
    public static readonly TimeSpan DefaultOrphanTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<ushort, FragmentBuffer> _inFlight = new();
    private readonly Func<long> _clockMs;
    private int _totalBufferedBytes;

    public FragmentReassembler(
        int maxTotalBufferedBytes = DefaultMaxTotalBufferedBytes,
        int maxMessageBytes = DefaultMaxMessageBytes,
        TimeSpan? orphanTimeout = null,
        Func<long>? clockMs = null)
    {
        MaxTotalBufferedBytes = maxTotalBufferedBytes;
        MaxMessageBytes = maxMessageBytes;
        OrphanTimeoutMs = (long)(orphanTimeout ?? DefaultOrphanTimeout).TotalMilliseconds;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public int MaxTotalBufferedBytes { get; }
    public int MaxMessageBytes { get; }
    public long OrphanTimeoutMs { get; }
    public int ActiveStreams => _inFlight.Count;
    public int TotalBufferedBytes => _totalBufferedBytes;

    public static byte[][] SplitIntoFragments(ushort streamId, ReadOnlySpan<byte> payload, int maxChunkSize = 1024)
    {
        if (maxChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkSize));
        }

        var totalFragments = Math.Max(1, (payload.Length + maxChunkSize - 1) / maxChunkSize);
        if (totalFragments > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload too large to fragment.");
        }

        var packets = new byte[totalFragments][];
        for (ushort i = 0; i < totalFragments; i++)
        {
            var offset = i * maxChunkSize;
            var len = Math.Min(maxChunkSize, payload.Length - offset);
            var pkt = new byte[7 + len];
            pkt[0] = (byte)UdpSendOption.Fragment;
            pkt[1] = (byte)(streamId >> 8);
            pkt[2] = (byte)streamId;
            pkt[3] = (byte)(i >> 8);
            pkt[4] = (byte)i;
            pkt[5] = (byte)(totalFragments >> 8);
            pkt[6] = (byte)totalFragments;
            payload.Slice(offset, len).CopyTo(pkt.AsSpan(7));
            packets[i] = pkt;
        }

        return packets;
    }

    public bool TryAddFragment(ReadOnlySpan<byte> packet, out byte[]? reassembled)
    {
        reassembled = null;
        PruneOrphans();

        if (packet.Length < 7 || packet[0] != (byte)UdpSendOption.Fragment)
        {
            throw new ProtocolException((byte)UdpSendOption.Fragment, packet, "Truncated or invalid Fragment header.");
        }

        var streamId = (ushort)((packet[1] << 8) | packet[2]);
        var index = (ushort)((packet[3] << 8) | packet[4]);
        var total = (ushort)((packet[5] << 8) | packet[6]);
        var chunk = packet[7..];

        if (total == 0 || index >= total)
        {
            throw new ProtocolException((byte)UdpSendOption.Fragment, packet,
                $"Invalid fragment indices: index={index}, total={total}.");
        }

        if (!_inFlight.TryGetValue(streamId, out var buffer))
        {
            if (_totalBufferedBytes + chunk.Length > MaxTotalBufferedBytes)
            {
                throw new ProtocolException((byte)UdpSendOption.Fragment, packet,
                    $"Fragment reassembly pool cap exceeded ({_totalBufferedBytes + chunk.Length} > {MaxTotalBufferedBytes}).");
            }

            buffer = new FragmentBuffer(total, _clockMs());
            _inFlight[streamId] = buffer;
        }
        else if (buffer.Total != total)
        {
            throw new ProtocolException((byte)UdpSendOption.Fragment, packet,
                $"Fragment total mismatch for stream {streamId}: expected {buffer.Total}, got {total}.");
        }

        if (buffer.Chunks[index] is null)
        {
            if (buffer.AccumulatedBytes + chunk.Length > MaxMessageBytes)
            {
                RemoveStream(streamId, buffer);
                throw new ProtocolException((byte)UdpSendOption.Fragment, packet,
                    $"Single fragmented message exceeded MaxMessageBytes ({MaxMessageBytes}).");
            }

            if (_totalBufferedBytes + chunk.Length > MaxTotalBufferedBytes)
            {
                RemoveStream(streamId, buffer);
                throw new ProtocolException((byte)UdpSendOption.Fragment, packet,
                    $"Fragment reassembly total buffer exceeded ({MaxTotalBufferedBytes}).");
            }

            var copy = chunk.ToArray();
            buffer.Chunks[index] = copy;
            buffer.AccumulatedBytes += copy.Length;
            buffer.ReceivedCount++;
            buffer.LastUpdatedMs = _clockMs();
            _totalBufferedBytes += copy.Length;
        }

        if (buffer.ReceivedCount < buffer.Total)
        {
            return false;
        }

        reassembled = new byte[buffer.AccumulatedBytes];
        var pos = 0;
        foreach (var part in buffer.Chunks)
        {
            if (part is not null)
            {
                part.CopyTo(reassembled, pos);
                pos += part.Length;
            }
        }

        RemoveStream(streamId, buffer);
        return true;
    }

    public int PruneOrphans()
    {
        var now = _clockMs();
        List<ushort>? expired = null;
        foreach (var (streamId, buffer) in _inFlight)
        {
            if (now - buffer.LastUpdatedMs >= OrphanTimeoutMs)
            {
                expired ??= new List<ushort>();
                expired.Add(streamId);
            }
        }

        if (expired is null)
        {
            return 0;
        }

        foreach (var id in expired)
        {
            if (_inFlight.TryGetValue(id, out var buf))
            {
                RemoveStream(id, buf);
            }
        }

        return expired.Count;
    }

    private void RemoveStream(ushort streamId, FragmentBuffer buffer)
    {
        _inFlight.Remove(streamId);
        _totalBufferedBytes = Math.Max(0, _totalBufferedBytes - buffer.AccumulatedBytes);
    }

    private sealed class FragmentBuffer
    {
        public FragmentBuffer(int total, long createdAtMs)
        {
            Total = total;
            Chunks = new byte[total][];
            LastUpdatedMs = createdAtMs;
        }

        public int Total { get; }
        public byte[]?[] Chunks { get; }
        public int ReceivedCount { get; set; }
        public int AccumulatedBytes { get; set; }
        public long LastUpdatedMs { get; set; }
    }
}
