using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeanBot.Protocol;

namespace BeanBot.Observability;

public sealed record ReplayEntry(
    long Sequence,
    long MonotonicMs,
    DateTimeOffset UtcTimestamp,
    string BotId,
    string Action,
    byte? CausePacketId,
    string CausePacketName,
    string StateHash,
    string? Detail = null);

/// <summary>
/// Appends every outbound action as newline-delimited JSON with monotonic timestamp,
/// cause packet id, and resulting state hash (G4, Section 13, P0.3).
/// </summary>
public sealed class ReplayLogger : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();
    private readonly ProtocolRegistry _registry;
    private readonly Counters? _counters;
    private long _sequence;

    public ReplayLogger(string filePath, ProtocolRegistry? registry = null, Counters? counters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _registry = registry ?? ProtocolRegistry.Default;
        _counters = counters;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(
            new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    public string FilePath { get; }

    public ReplayEntry LogAction(
        string botId,
        string action,
        byte? causePacketId,
        string stateHash,
        string? detail = null,
        string? causePacketNameOverride = null)
    {
        var packetName = causePacketNameOverride ??
                         (causePacketId.HasValue ? _registry.GetRootName(causePacketId.Value) : "None");

        lock (_gate)
        {
            var entry = new ReplayEntry(
                Sequence: ++_sequence,
                MonotonicMs: _monotonic.ElapsedMilliseconds,
                UtcTimestamp: DateTimeOffset.UtcNow,
                BotId: botId,
                Action: action,
                CausePacketId: causePacketId,
                CausePacketName: packetName,
                StateHash: stateHash,
                Detail: detail);

            var line = JsonSerializer.Serialize(entry, JsonOptions);
            _writer.WriteLine(line);
            _counters?.IncrementReplayWritten();
            return entry;
        }
    }

    public static string ComputeStateHash(string canonicalState)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalState);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
