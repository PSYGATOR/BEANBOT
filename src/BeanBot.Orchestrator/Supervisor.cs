using System.Collections.Concurrent;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Transport;
using Microsoft.Extensions.Logging;

namespace BeanBot.Orchestrator;

public enum BotSlotHealth
{
    Idle,
    Joining,
    Connected,
    Quarantined,
    Disconnected,
    Stopped,
}

public sealed record OrchestratorTimingConfig(
    TimeSpan AuthSpacing,
    TimeSpan JoinSpacing,
    TimeSpan RetryMinDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan ReconnectBaseDelay,
    TimeSpan ReconnectMaxDelay,
    TimeSpan HostRebuildDeadline,
    TimeSpan StartGateDwell,
    IReadOnlyList<TimeSpan>? RateLimitRetryDelays = null)
{
    public static OrchestratorTimingConfig LiveDefault => new(
        AuthSpacing: TimeSpan.FromMilliseconds(1500),
        JoinSpacing: TimeSpan.FromMilliseconds(750),
        RetryMinDelay: TimeSpan.FromMilliseconds(500),
        RetryMaxDelay: TimeSpan.FromMilliseconds(1500),
        ReconnectBaseDelay: TimeSpan.FromSeconds(1),
        ReconnectMaxDelay: TimeSpan.FromSeconds(60),
        HostRebuildDeadline: TimeSpan.FromSeconds(55),
        StartGateDwell: TimeSpan.FromSeconds(3));

    public static OrchestratorTimingConfig FastLocal => new(
        AuthSpacing: TimeSpan.Zero,
        JoinSpacing: TimeSpan.Zero,
        RetryMinDelay: TimeSpan.FromMilliseconds(20),
        RetryMaxDelay: TimeSpan.FromMilliseconds(60),
        ReconnectBaseDelay: TimeSpan.FromMilliseconds(30),
        ReconnectMaxDelay: TimeSpan.FromMilliseconds(300),
        HostRebuildDeadline: TimeSpan.FromSeconds(30),
        StartGateDwell: TimeSpan.FromMilliseconds(250),
        RateLimitRetryDelays: new[] { TimeSpan.FromMilliseconds(20) });
}

public sealed class ManagedBotSlot
{
    public ManagedBotSlot(RosterSeat seat, BotClient client)
    {
        Seat = seat;
        Client = client;
    }

    public RosterSeat Seat { get; }
    public BotClient Client { get; }
    public SemaphoreSlim JoinGate { get; } = new(1, 1);
    public BotSlotHealth Health { get; internal set; } = BotSlotHealth.Idle;
    public int ConsecutiveFailures { get; internal set; }
    public bool CircuitOpen { get; internal set; }
    public int RestartCount { get; internal set; }
    public int ConcurrentJoinEntries => _concurrentEntries;
    public string? LastError { get; internal set; }
    public RoomLookupException? NonTransientJoinError { get; internal set; }

    internal int _inFlightFlag;
    internal int _concurrentEntries;
}

/// <summary>
/// P2.2 & P2.5 Orchestrator Supervisor:
/// - Ramps joins in waves (1 -> 4 -> 8 -> 14) with single-flight per seat (F9)
/// - Applies exponential jittered backoff (capped at 60s) and opens circuit breaker after 3 consecutive failures
/// - Isolates per-bot crashes/drops so remaining seats stay connected while dropped bots requeue
/// - Enforces host-first reconnect: pauses joiners on host drop, rebuilds host room < 60s, then reseats joiners (F4)
/// </summary>
public sealed class Supervisor
{
    public const int CircuitBreakerFailureThreshold = 3;
    public static readonly int[] RampWaves = [1, 4, 8, 14];

    private readonly Vault _vault;
    private readonly OrchestratorTimingConfig _timing;
    private readonly Counters _counters;
    private readonly ILogger<Supervisor>? _logger;
    private readonly string _replayDirectory;
    private readonly string _locksDirectory;
    private readonly Func<CancellationToken, Task<string>> _rebuildHostAsync;

    private readonly Dictionary<string, ManagedBotSlot> _slotsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _hostRebuildLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _requeueTasks = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isShuttingDown;
    private volatile bool _hostRebuilding;

    public bool IsHostRebuilding => _hostRebuilding;
    public string ActiveLobbyCode { get; internal set; } = string.Empty;
    public event Func<string, Task>? HostRebuiltAsync;

    public Supervisor(
        Vault vault,
        OrchestratorTimingConfig timing,
        Counters counters,
        string replayDirectory,
        string locksDirectory,
        Func<CancellationToken, Task<string>> rebuildHostAsync,
        ILogger<Supervisor>? logger = null)
    {
        _vault = vault;
        _timing = timing;
        _counters = counters;
        _replayDirectory = replayDirectory;
        _locksDirectory = locksDirectory;
        _rebuildHostAsync = rebuildHostAsync;
        _logger = logger;
    }

    public void RegisterSlot(ManagedBotSlot slot)
    {
        _slotsById[slot.Seat.AccountId] = slot;
        slot.Client.ManageSeatsHeldCounter = false;
        slot.Client.AutoReconnect = false;
        slot.Client.OnConnectionLostAsync = ex => HandleSlotConnectionLostAsync(slot, ex);
    }

    public ManagedBotSlot? GetSlot(string accountId) =>
        _slotsById.TryGetValue(accountId, out var slot) ? slot : null;

    public IReadOnlyCollection<ManagedBotSlot> Slots => _slotsById.Values.ToArray();

    /// <summary>
    /// Ramps joiners through 1 -> 4 -> 8 -> 14 batches with single-flight and jittered spacing.
    /// </summary>
    public async Task SeatJoinersWithRampAsync(IReadOnlyList<ManagedBotSlot> joiners, string lobbyCode, CancellationToken ct)
    {
        ActiveLobbyCode = lobbyCode;
        var index = 0;
        var prevCap = 0;

        foreach (var waveTarget in RampWaves)
        {
            if (index >= joiners.Count)
            {
                break;
            }

            var waveSize = Math.Min(waveTarget - prevCap, joiners.Count - index);
            prevCap = waveTarget;

            var tasks = new List<Task>(waveSize);
            for (var i = 0; i < waveSize && index < joiners.Count; i++, index++)
            {
                var slot = joiners[index];
                if ((i > 0 || waveTarget > 1) && _timing.JoinSpacing > TimeSpan.Zero)
                {
                    var jitter = Random.Shared.Next(0, Math.Max(1, (int)(_timing.JoinSpacing.TotalMilliseconds * 0.25)));
                    await Task.Delay(_timing.JoinSpacing + TimeSpan.FromMilliseconds(jitter), ct).ConfigureAwait(false);
                }

                tasks.Add(TrySeatWithBackoffAsync(slot, lobbyCode, maxAttempts: 4, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (joiners.Any(s => s.NonTransientJoinError is not null))
            {
                return;
            }

            if (waveTarget == 1 && joiners.Count > 1 && joiners[0].Client.IsConnected)
            {
                var resolvedProfile = joiners[0].Client.EffectiveProfile;
                var resolvedRelay = joiners[0].Client.ResolvedRelayEndPoint;
                var sharedLookup = !resolvedProfile.RequiresRelayDtlsAuth && resolvedRelay is not null
                    ? new SharedRoomLookup(joiners[0].Client.CurrentGameCode, resolvedRelay)
                    : null;
                for (var j = 1; j < joiners.Count; j++)
                {
                    joiners[j].Client.PreferredRegionProfile = resolvedProfile;
                    joiners[j].Client.SharedLookup = sharedLookup;
                }
            }
        }

        while (index < joiners.Count)
        {
            var slot = joiners[index++];
            await TrySeatWithBackoffAsync(slot, lobbyCode, maxAttempts: 4, ct).ConfigureAwait(false);
            if (slot.NonTransientJoinError is not null)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Single-flight join per seat (F9). Concurrent callers either serialize cleanly or return false if already in-flight.
    /// </summary>
    public async Task<bool> TrySeatSingleFlightAsync(
        ManagedBotSlot slot,
        string lobbyCode,
        bool rejectIfInFlight = true,
        CancellationToken ct = default)
    {
        if (rejectIfInFlight && Interlocked.CompareExchange(ref slot._inFlightFlag, 1, 0) != 0)
        {
            return false;
        }

        if (!rejectIfInFlight)
        {
            while (Interlocked.CompareExchange(ref slot._inFlightFlag, 1, 0) != 0)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        await slot.JoinGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var concurrency = Interlocked.Increment(ref slot._concurrentEntries);
            if (concurrency > 1)
            {
                throw new InvalidOperationException($"Single-flight violation on seat {slot.Seat.AccountId}: concurrent={concurrency}.");
            }

            if (slot.CircuitOpen)
            {
                var rec = _vault.Get(slot.Seat.AccountId);
                if (rec is not null && !rec.IsCooling(DateTimeOffset.UtcNow))
                {
                    slot.CircuitOpen = false;
                    slot.ConsecutiveFailures = 0;
                    rec.State = AccountState.LoggedIn;
                    rec.ConsecutiveJoinFailures = 0;
                    _vault.SaveRecord(rec);
                }
                else
                {
                    slot.Health = BotSlotHealth.Quarantined;
                    return false;
                }
            }

            slot.Health = BotSlotHealth.Joining;
            slot.NonTransientJoinError = null;
            var target = slot.Seat.IsHost ? "host" : lobbyCode;
            await slot.Client.JoinAsync(
                target,
                slot.Seat.AccountId,
                slot.Seat.DisplayName,
                _replayDirectory,
                _locksDirectory,
                ct).ConfigureAwait(false);

            slot.ConsecutiveFailures = 0;
            slot.CircuitOpen = false;
            slot.LastError = null;
            slot.NonTransientJoinError = null;
            slot.Health = BotSlotHealth.Connected;

            var record = _vault.Get(slot.Seat.AccountId);
            if (record is not null)
            {
                record.ConsecutiveJoinFailures = 0;
                record.CooldownUntil = null;
                record.State = AccountState.InLobby;
                _vault.SaveRecord(record);
            }

            UpdateSeatsHeldCounter();
            return true;
        }
        catch (RoomLookupException lookupEx)
        {
            slot.LastError = lookupEx.Message;
            slot.NonTransientJoinError = lookupEx;
            slot.Health = BotSlotHealth.Disconnected;
            _logger?.LogWarning("Join lookup failed for seat {AccountId}: {Message}", slot.Seat.AccountId, lookupEx.Message);
            UpdateSeatsHeldCounter();
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            slot.LastError = ex.Message;
            slot.ConsecutiveFailures++;
            slot.Health = BotSlotHealth.Disconnected;

            var record = _vault.Get(slot.Seat.AccountId);
            if (record is not null)
            {
                record.ConsecutiveJoinFailures = slot.ConsecutiveFailures;
                if (slot.ConsecutiveFailures >= CircuitBreakerFailureThreshold)
                {
                    slot.CircuitOpen = true;
                    slot.Health = BotSlotHealth.Quarantined;
                    record.State = AccountState.Cooling;
                    record.CooldownUntil = DateTimeOffset.UtcNow + _timing.ReconnectMaxDelay;
                }

                _vault.SaveRecord(record);
            }

            if (slot.ConsecutiveFailures >= CircuitBreakerFailureThreshold)
            {
                slot.CircuitOpen = true;
                slot.Health = BotSlotHealth.Quarantined;
                _logger?.LogError(ex, "Circuit breaker tripped for seat {AccountId} after {Failures} failures.",
                    slot.Seat.AccountId, slot.ConsecutiveFailures);
            }
            else
            {
                _logger?.LogWarning(ex, "Seat {AccountId} failed to join {Target} (failure {Count}/{Threshold}).",
                    slot.Seat.AccountId, slot.Seat.IsHost ? "host" : lobbyCode, slot.ConsecutiveFailures, CircuitBreakerFailureThreshold);
            }

            UpdateSeatsHeldCounter();
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref slot._concurrentEntries);
            slot.JoinGate.Release();
            Interlocked.Exchange(ref slot._inFlightFlag, 0);
        }
    }

    public async Task<bool> TrySeatWithBackoffAsync(
        ManagedBotSlot slot,
        string lobbyCode,
        int maxAttempts,
        CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(
            (int)Math.Max(1, _timing.ReconnectBaseDelay.TotalMilliseconds),
            (int)Math.Max(10, _timing.ReconnectMaxDelay.TotalMilliseconds));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (slot.CircuitOpen)
            {
                return false;
            }

            if (await TrySeatSingleFlightAsync(slot, lobbyCode, rejectIfInFlight: false, ct).ConfigureAwait(false))
            {
                return true;
            }

            if (slot.CircuitOpen || slot.NonTransientJoinError is not null || attempt + 1 >= maxAttempts)
            {
                break;
            }

            var delay = backoff.NextDelay();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Simulates killing one bot mid-lobby (P2.5 gate: killed bot requeues cleanly; other bots stay connected).
    /// </summary>
    public async Task KillBotAndRequeueAsync(string accountId, CancellationToken ct = default)
    {
        var slot = GetSlot(accountId) ?? throw new KeyNotFoundException($"Unknown bot '{accountId}'.");
        await slot.Client.StopAsync().ConfigureAwait(false);
        slot.Health = BotSlotHealth.Disconnected;
        UpdateSeatsHeldCounter();
        await HandleSlotConnectionLostAsync(slot, new IOException($"Simulated kill on {accountId}")).ConfigureAwait(false);
        if (_requeueTasks.TryGetValue(accountId, out var task))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles a host drop: pauses joiners, rebuilds host room in &lt; 60s, then reseats all joiners (F4, P2.5).
    /// </summary>
    public async Task<string> HandleHostLostAndRebuildAsync(CancellationToken ct = default)
    {
        await _hostRebuildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _hostRebuilding = true;
            _logger?.LogWarning("Host connection lost -> initiating host-first rebuild (deadline={Deadline}s).", _timing.HostRebuildDeadline.TotalSeconds);

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadlineCts.CancelAfter(_timing.HostRebuildDeadline);

            // Disconnect joiners cleanly so no joiner sits orphaned on the dead lobby while host rebuilds.
            var joiners = _slotsById.Values.Where(s => !s.Seat.IsHost).ToArray();
            await Task.WhenAll(joiners.Select(async j =>
            {
                try
                {
                    await j.Client.StopAsync().ConfigureAwait(false);
                    j.Health = BotSlotHealth.Disconnected;
                }
                catch { }
            })).ConfigureAwait(false);

            var newLobbyCode = await _rebuildHostAsync(deadlineCts.Token).ConfigureAwait(false);
            ActiveLobbyCode = newLobbyCode;
            _hostRebuilding = false;

            await SeatJoinersWithRampAsync(joiners, newLobbyCode, deadlineCts.Token).ConfigureAwait(false);
            if (HostRebuiltAsync is not null)
            {
                await HostRebuiltAsync(newLobbyCode).ConfigureAwait(false);
            }

            UpdateSeatsHeldCounter();
            return newLobbyCode;
        }
        finally
        {
            _hostRebuilding = false;
            _hostRebuildLock.Release();
        }
    }

    private Task HandleSlotConnectionLostAsync(ManagedBotSlot slot, Exception ex)
    {
        slot.LastError = ex.ToString();
        if (_isShuttingDown)
        {
            return Task.CompletedTask;
        }

        UpdateSeatsHeldCounter();
        if (slot.Seat.IsHost)
        {
            _requeueTasks["__host_rebuild__"] = Task.Run(async () =>
            {
                try
                {
                    await HandleHostLostAndRebuildAsync().ConfigureAwait(false);
                }
                catch (Exception rebuildEx)
                {
                    _logger?.LogWarning(rebuildEx, "Host rebuild failed.");
                }
            });
            return Task.CompletedTask;
        }

        if (_hostRebuilding)
        {
            return Task.CompletedTask;
        }

        var hasLocalHost = _slotsById.Values.Any(s => s.Seat.IsHost);
        if (!hasLocalHost && IsTerminalExternalLobbyDisconnect(ex))
        {
            _logger?.LogInformation(
                "Seat {AccountId} disconnected from external lobby {LobbyCode} ({Message}); not requeueing.",
                slot.Seat.AccountId,
                ActiveLobbyCode,
                ex.Message);
            return Task.CompletedTask;
        }

        var task = Task.Run(async () =>
        {
            try
            {
                slot.RestartCount++;
                var minMs = (int)Math.Max(0, _timing.RetryMinDelay.TotalMilliseconds);
                var maxMs = (int)Math.Max(minMs + 1, _timing.RetryMaxDelay.TotalMilliseconds);
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs));
                await Task.Delay(jitter).ConfigureAwait(false);

                while (_hostRebuilding && !_isShuttingDown)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }

                if (!_isShuttingDown && !string.IsNullOrWhiteSpace(ActiveLobbyCode))
                {
                    await TrySeatWithBackoffAsync(slot, ActiveLobbyCode, maxAttempts: 4, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception retryEx)
            {
                _logger?.LogWarning(retryEx, "Isolated requeue failed for {AccountId}.", slot.Seat.AccountId);
            }
        });

        _requeueTasks[slot.Seat.AccountId] = task;
        return Task.CompletedTask;
    }

    private static bool IsTerminalExternalLobbyDisconnect(Exception ex) =>
        ex is RoomLookupException ||
        ex.Message.Contains(nameof(DisconnectReason.Banned), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.Kicked), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.Destroy), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.LobbyInactivity), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.MatchmakerInactivity), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.GameNotFound), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.GameFull), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.GameStarted), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.IncorrectVersion), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.MismatchedVersion), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.Custom), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.TooManyRequests), StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains(nameof(DisconnectReason.Sanctions), StringComparison.OrdinalIgnoreCase);

    public void UpdateSeatsHeldCounter()
    {
        var count = _slotsById.Values.Count(s => s.Client.IsConnected);
        _counters.SetSeatsHeld(count);
    }

    public async Task ShutdownAsync()
    {
        _isShuttingDown = true;
        foreach (var slot in _slotsById.Values)
        {
            slot.Client.OnConnectionLostAsync = null;
        }

        await Task.WhenAll(_slotsById.Values.Select(s => s.Client.StopAsync())).ConfigureAwait(false);
        UpdateSeatsHeldCounter();
    }
}
