using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Host;
using BeanBot.Matchmaker;
using BeanBot.Nav;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using BeanBot.World;
using Microsoft.Extensions.Logging;

namespace BeanBot.Orchestrator;

internal sealed class BotClientHostBridge(BotClient hostBot) : IHostTransportBridge
{
    public GameCode GameCode => hostBot.CurrentGameCode;
    public WorldModel World => hostBot.World;
    public uint? NormalGameManagerNetId => hostBot.NormalGameManagerNetId;
    public uint? GetPlayerInfoNetId(int clientId) => hostBot.GetPlayerInfoNetId(clientId);

    public Task SendRootAsync(IRootMessage root, CancellationToken ct = default)
        => hostBot.SendRootMessageAsync(root, reliable: true, ct);

    public Task SendRpcAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct = default)
        => hostBot.SendHostRpcAsync(targetNetId, rpc, ct);
}

public sealed record BotSeatStatusSnapshot(
    int SeatIndex,
    bool IsHost,
    string AccountId,
    string DisplayName,
    string Color,
    bool IsConnected,
    BotSlotHealth Health,
    int ClientId,
    GamePhase Phase,
    PlayerRole Role,
    bool IsReady,
    bool CircuitOpen,
    int ConsecutiveFailures,
    int RestartCount);

public sealed record FleetStatusSnapshot(
    string LobbyCode,
    int SeatsHeld,
    int TargetSeats,
    int ConnectedCount,
    bool HostHealthy,
    HostLifecycleState HostState,
    bool IsRebuilding,
    double KickRate,
    object Counters,
    IReadOnlyList<BotSeatStatusSnapshot> Bots);

/// <summary>
/// P2 Fleet Orchestrator: coordinates 15 persisted identities, HostController, SeatFiller/Supervisor,
/// isolated joiners, host-first rebuild, and Section 13 observability snapshots.
/// </summary>
public sealed class Fleet : IFleetControlTarget
{
    private readonly ServerProfile _profile;
    private readonly Vault _vault;
    private readonly IDeviceAuthSource _auth;
    private readonly DeviceAuthSource _concreteAuth;
    private readonly MatchmakerClient _matchmaker;
    private readonly Roster _roster;
    private readonly HostLobbyOptions _hostOptions;
    private readonly OrchestratorTimingConfig _timing;
    private readonly Counters _counters;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<Fleet>? _logger;
    private readonly string _replayDirectory;
    private readonly string _locksDirectory;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly Supervisor _supervisor;
    private readonly List<ManagedBotSlot> _slots = new();
    private HostController? _hostController;
    private CancellationTokenSource? _superviseCts;
    private Task? _superviseTask;
    private bool _disposed;

    public Roster Roster => _roster;
    public Counters Counters => _counters;
    public Supervisor Supervisor => _supervisor;
    public HostController? HostController => _hostController;
    public string CurrentLobbyCode { get; private set; } = string.Empty;

    public Fleet(
        ServerProfile profile,
        Vault vault,
        DeviceAuthSource auth,
        MatchmakerClient matchmaker,
        Roster? roster = null,
        HostLobbyOptions? hostOptions = null,
        OrchestratorTimingConfig? timing = null,
        Counters? counters = null,
        string replayDirectory = "data/replays",
        string locksDirectory = "data/locks",
        ILoggerFactory? loggerFactory = null,
        IDeviceAuthSource? authOverrideForPrecheck = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _profile = profile;
        _vault = vault;
        _concreteAuth = auth;
        _auth = authOverrideForPrecheck ?? auth;
        _matchmaker = matchmaker;
        _roster = roster ?? Roster.CreateDefault();
        _hostOptions = (hostOptions ?? new HostLobbyOptions()) with
        {
            ExpectedJoiners = _roster.JoinerSeats.Count,
            StartGateDwell = (hostOptions ?? new HostLobbyOptions()).StartGateDwell,
        };
        _timing = timing ?? (profile.Mode == ServerProfileMode.Live
            ? OrchestratorTimingConfig.LiveDefault
            : OrchestratorTimingConfig.FastLocal);
        _counters = counters ?? new Counters();
        _replayDirectory = replayDirectory;
        _locksDirectory = locksDirectory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<Fleet>();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        _supervisor = new Supervisor(
            _vault,
            _timing,
            _counters,
            _replayDirectory,
            _locksDirectory,
            RebuildHostLobbyAsync,
            _loggerFactory?.CreateLogger<Supervisor>());
    }

    public async Task<string> StartHostLobbyAsync(bool deterministicManualTick = false, CancellationToken ct = default)
    {
        // P2.0: 15 distinct persisted DeviceIds before first login + 15 distinct PUIDs verified before lobby open.
        await _roster.AuthenticateAndVerifyDistinctPuidsAsync(
            _vault,
            _auth,
            _profile,
            _timing.AuthSpacing,
            _counters,
            _logger,
            ct).ConfigureAwait(false);

        InitializeSlots(deterministicManualTick);

        var hostSlot = _slots[0];
        var seatedHost = await _supervisor.TrySeatWithBackoffAsync(hostSlot, "host", maxAttempts: 4, ct).ConfigureAwait(false);
        if (!seatedHost)
        {
            throw new InvalidOperationException($"Host failed to create/join lobby: {hostSlot.LastError}");
        }

        CurrentLobbyCode = hostSlot.Client.CurrentGameCode.Code;
        await FinalizeHostInitializationAsync(hostSlot, ct).ConfigureAwait(false);

        // P2.2: Ramp joiner seats 1..14 with jitter & single-flight.
        var joiners = _slots.Skip(1).ToArray();
        await _supervisor.SeatJoinersWithRampAsync(joiners, CurrentLobbyCode, ct).ConfigureAwait(false);
        _supervisor.UpdateSeatsHeldCounter();

        if (!deterministicManualTick)
        {
            _superviseCts = new CancellationTokenSource();
            _superviseTask = Task.Run(() => HostSupervisionLoopAsync(_superviseCts.Token), CancellationToken.None);
        }

        _logger?.LogInformation("Fleet lobby {LobbyCode} active ({Seated}/{Target} seats).",
            CurrentLobbyCode, _slots.Count(s => s.Client.IsConnected), _roster.Seats.Count);
        return CurrentLobbyCode;
    }

    /// <summary>
    /// Joins all configured roster bots (<c>1..15</c>) into an existing lobby by room code (or <c>auto</c>).
    /// </summary>
    public async Task<string> StartJoinLobbyAsync(
        string roomCodeOrAuto,
        bool deterministicManualTick = false,
        CancellationToken ct = default)
    {
        var targetRoom = string.IsNullOrWhiteSpace(roomCodeOrAuto) ? "auto" : roomCodeOrAuto.Trim();
        if (!targetRoom.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            targetRoom = targetRoom.ToUpperInvariant();
            if (GameCode.From(targetRoom).IsInvalid)
            {
                throw new RoomLookupException(
                    JoinLookupStatus.Error,
                    $"Invalid room code '{roomCodeOrAuto}'. Expected a 4- or 6-letter Among Us lobby code, or 'auto'.");
            }
        }

        await _roster.AuthenticateAndVerifyDistinctPuidsAsync(
            _vault,
            _auth,
            _profile,
            _timing.AuthSpacing,
            _counters,
            _logger,
            ct).ConfigureAwait(false);

        InitializeSlots(deterministicManualTick);

        await _supervisor.SeatJoinersWithRampAsync(_slots, targetRoom, ct).ConfigureAwait(false);
        _supervisor.UpdateSeatsHeldCounter();

        var firstConnected = _slots.FirstOrDefault(s => s.Client.IsConnected);
        if (firstConnected is null)
        {
            var fatalLookup = _slots.Select(s => s.NonTransientJoinError).FirstOrDefault(e => e is not null);
            if (fatalLookup is not null)
            {
                throw new RoomLookupException(fatalLookup.Status, $"No bots could join lobby '{targetRoom}': {fatalLookup.Message}");
            }

            var err = _slots.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.LastError))?.LastError ?? "unknown error";
            throw new InvalidOperationException($"No bots could join lobby '{targetRoom}': {err}");
        }

        CurrentLobbyCode = !string.IsNullOrWhiteSpace(firstConnected.Client.World.LobbyCode)
            ? firstConnected.Client.World.LobbyCode
            : targetRoom;
        _supervisor.ActiveLobbyCode = CurrentLobbyCode;

        if (!deterministicManualTick)
        {
            _superviseCts = new CancellationTokenSource();
            _superviseTask = Task.Run(() => HostSupervisionLoopAsync(_superviseCts.Token), CancellationToken.None);
        }

        _logger?.LogInformation("Fleet joined lobby {LobbyCode} ({Seated}/{Target} seats).",
            CurrentLobbyCode, _slots.Count(s => s.Client.IsConnected), _roster.Seats.Count);
        return CurrentLobbyCode;
    }

    private void InitializeSlots(bool deterministicManualTick)
    {
        if (_slots.Count > 0)
        {
            return;
        }

        foreach (var seat in _roster.Seats)
        {
            var client = new BotClient(
                _profile,
                _vault,
                _concreteAuth,
                _matchmaker,
                NavMesh.LoadSkeld(),
                ProtocolRegistry.Default,
                _counters,
                _loggerFactory)
            {
                PreferredColorName = seat.Color.ToString(),
                DeterministicManualTick = deterministicManualTick,
            };

            var slot = new ManagedBotSlot(seat, client);
            if (_timing.RateLimitRetryDelays is { } rateLimitDelays)
            {
                client.RateLimitRetryDelays = rateLimitDelays;
            }

            _slots.Add(slot);
            _supervisor.RegisterSlot(slot);
        }
    }

    private async Task InitializeHostControllerForSlotAsync(ManagedBotSlot hostSlot, CancellationToken ct)
    {
        var bridge = new BotClientHostBridge(hostSlot.Client);
        _hostController = new HostController(
            bridge,
            _hostOptions,
            _counters,
            _loggerFactory?.CreateLogger<HostController>(),
            _utcNow);
        _hostController.SetExpectedRoster(_roster.BuildExpectedSeats(_vault));

        hostSlot.Client.OnPlayerJoinedAsync = async _ =>
        {
            if (_hostController is not null)
            {
                await _hostController.TickAsync().ConfigureAwait(false);
            }
        };

        hostSlot.Client.OnCheckNameRequestedAsync = async (clientId, netId, requestedName) =>
        {
            if (_hostController is not null)
            {
                await _hostController.HandleCheckNameAsync(clientId, netId, requestedName).ConfigureAwait(false);
            }
        };

        hostSlot.Client.OnCheckColorRequestedAsync = async (clientId, netId, requestedColor) =>
        {
            if (_hostController is not null)
            {
                await _hostController.HandleCheckColorAsync(clientId, netId, requestedColor).ConfigureAwait(false);
            }
        };

        await _hostController.InitializeLobbyAsync(ct).ConfigureAwait(false);
    }

    private async Task FinalizeHostInitializationAsync(ManagedBotSlot hostSlot, CancellationToken ct)
    {
        await InitializeHostControllerForSlotAsync(hostSlot, ct).ConfigureAwait(false);
        if (_hostController is not null)
        {
            await _hostController.TickAsync(ct).ConfigureAwait(false);
        }

        if (!hostSlot.Client.DeterministicManualTick)
        {
            await hostSlot.Client.WaitForLobbyIndexedAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
    }

    private async Task<string> RebuildHostLobbyAsync(CancellationToken ct)
    {
        _hostController?.MarkRebuilding();
        var hostSlot = _slots[0];
        hostSlot.RestartCount++;
        var ok = await _supervisor.TrySeatWithBackoffAsync(hostSlot, "host", maxAttempts: 4, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new IOException($"Host failed to rebuild lobby: {hostSlot.LastError}");
        }

        CurrentLobbyCode = hostSlot.Client.CurrentGameCode.Code;
        await FinalizeHostInitializationAsync(hostSlot, ct).ConfigureAwait(false);
        return CurrentLobbyCode;
    }

    private async Task HostSupervisionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                if (_hostController is not null && !_supervisor.IsHostRebuilding && _slots[0].Client.IsConnected)
                {
                    await _hostController.TickAsync(ct).ConfigureAwait(false);
                }

                _supervisor.UpdateSeatsHeldCounter();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during host supervision loop.");
            }
        }
    }

    public FleetStatusSnapshot GetStatusSnapshot()
    {
        var bots = _slots.Select(s =>
        {
            var local = s.Client.World.LocalPlayer;
            return new BotSeatStatusSnapshot(
                SeatIndex: s.Seat.SeatIndex,
                IsHost: s.Seat.IsHost,
                AccountId: s.Seat.AccountId,
                DisplayName: s.Seat.DisplayName,
                Color: s.Seat.Color.ToString(),
                IsConnected: s.Client.IsConnected,
                Health: s.Health,
                ClientId: s.Client.World.LocalClientId,
                Phase: s.Client.World.Phase,
                Role: s.Client.World.LocalRole,
                IsReady: local?.IsReady ?? false,
                CircuitOpen: s.CircuitOpen,
                ConsecutiveFailures: s.ConsecutiveFailures,
                RestartCount: s.RestartCount);
        }).ToArray();

        var connected = bots.Count(b => b.IsConnected);
        var attempts = Math.Max(1L, _counters.JoinAttempts);
        var kickRate = Math.Round(_counters.Kicks / (double)attempts, 4);

        return new FleetStatusSnapshot(
            LobbyCode: CurrentLobbyCode,
            SeatsHeld: connected,
            TargetSeats: _roster.Seats.Count,
            ConnectedCount: connected,
            HostHealthy: _slots.Count > 0 && _slots[0].Client.IsConnected && !_supervisor.IsHostRebuilding,
            HostState: _hostController?.State ?? HostLifecycleState.Idle,
            IsRebuilding: _supervisor.IsHostRebuilding,
            KickRate: kickRate,
            Counters: _counters.Snapshot(),
            Bots: bots);
    }

    public object GetFleetStatus() => GetStatusSnapshot();

    public IReadOnlyList<BotClient> GetBots() => _slots.Select(s => s.Client).ToArray();

    public BotClient? ResolveBot(string botIdOrName)
    {
        var trimmed = botIdOrName.Trim();
        var byAccount = _slots.FirstOrDefault(s =>
            s.Seat.AccountId.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
            s.Seat.DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (byAccount is not null)
        {
            return byAccount.Client;
        }

        if (int.TryParse(trimmed, out var idx) && idx >= 0 && idx < _slots.Count)
        {
            return _slots[idx].Client;
        }

        return null;
    }

    public async Task<string> StopBotOrAllAsync(string? botIdOrAll, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(botIdOrAll) || string.Equals(botIdOrAll, "--all", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(botIdOrAll, "all", StringComparison.OrdinalIgnoreCase))
        {
            await StopAllAsync().ConfigureAwait(false);
            return "Stopped entire bot fleet.";
        }

        var bot = ResolveBot(botIdOrAll);
        if (bot is null)
        {
            throw new KeyNotFoundException($"Bot '{botIdOrAll}' not found.");
        }

        await bot.StopAsync().ConfigureAwait(false);
        _supervisor.UpdateSeatsHeldCounter();
        return $"Stopped bot '{botIdOrAll}'.";
    }

    public async Task StopAllAsync()
    {
        if (_superviseCts is not null)
        {
            _superviseCts.Cancel();
            try
            {
                if (_superviseTask is not null) await _superviseTask.ConfigureAwait(false);
            }
            catch { }
            _superviseCts.Dispose();
            _superviseCts = null;
        }

        if (_hostController is not null && _slots.Count > 0 && _slots[0].Client.IsConnected)
        {
            try
            {
                await _hostController.EndGameAsync(GameOverReason.CrewmateDisconnect).ConfigureAwait(false);
            }
            catch { }
        }

        await _supervisor.ShutdownAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Ignored error while stopping fleet during disposal.");
        }

        foreach (var slot in _slots)
        {
            try
            {
                await slot.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Ignored error while disposing slot {AccountId}.", slot.Seat.AccountId);
            }
        }
    }
}
