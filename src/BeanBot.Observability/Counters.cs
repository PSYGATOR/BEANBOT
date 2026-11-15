using System.Collections.Concurrent;

namespace BeanBot.Observability;

/// <summary>
/// Fleet and per-session observability counters (Section 13).
/// </summary>
public sealed class Counters
{
    private long _joinAttempts;
    private long _joinSuccess;
    private long _kicks;
    private long _seatsHeld;
    private long _taskCompletions;
    private long _kills;
    private long _reports;
    private long _votes;
    private long _replaysWritten;
    private long _puidCollisions;
    private readonly ConcurrentDictionary<string, long> _disconnectsByReason = new(StringComparer.OrdinalIgnoreCase);

    public long JoinAttempts => Interlocked.Read(ref _joinAttempts);
    public long JoinSuccess => Interlocked.Read(ref _joinSuccess);
    public long Kicks => Interlocked.Read(ref _kicks);
    public long SeatsHeld => Interlocked.Read(ref _seatsHeld);
    public long TaskCompletions => Interlocked.Read(ref _taskCompletions);
    public long Kills => Interlocked.Read(ref _kills);
    public long Reports => Interlocked.Read(ref _reports);
    public long Votes => Interlocked.Read(ref _votes);
    public long ReplaysWritten => Interlocked.Read(ref _replaysWritten);
    public long PuidCollisions => Interlocked.Read(ref _puidCollisions);
    public IReadOnlyDictionary<string, long> DisconnectsByReason => _disconnectsByReason;

    public void IncrementJoinAttempt() => Interlocked.Increment(ref _joinAttempts);
    public void IncrementJoinSuccess() => Interlocked.Increment(ref _joinSuccess);
    public void IncrementKick() => Interlocked.Increment(ref _kicks);
    public void SetSeatsHeld(long count) => Interlocked.Exchange(ref _seatsHeld, count);
    public void IncrementTaskCompletion() => Interlocked.Increment(ref _taskCompletions);
    public void IncrementKill() => Interlocked.Increment(ref _kills);
    public void IncrementReport() => Interlocked.Increment(ref _reports);
    public void IncrementVote() => Interlocked.Increment(ref _votes);
    public void IncrementReplayWritten() => Interlocked.Increment(ref _replaysWritten);
    public void IncrementPuidCollision() => Interlocked.Increment(ref _puidCollisions);

    public void IncrementDisconnect(string reason)
    {
        var key = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
        _disconnectsByReason.AddOrUpdate(key, 1, static (_, cur) => cur + 1);
    }

    public object Snapshot() => new
    {
        JoinAttempts,
        JoinSuccess,
        Kicks,
        DisconnectsByReason = _disconnectsByReason.ToDictionary(k => k.Key, v => v.Value),
        SeatsHeld,
        TaskCompletions,
        Kills,
        Reports,
        Votes,
        ReplaysWritten,
        PuidCollisions,
    };
}
