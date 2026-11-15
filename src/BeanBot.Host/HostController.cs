using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.World;
using Microsoft.Extensions.Logging;

namespace BeanBot.Host;

public enum HostLifecycleState
{
    Idle,
    PrivateSeating,
    PublicOpen,
    StartCountdown,
    InMatch,
    Ended,
    Rebuilding,
}

public sealed record ExpectedRosterSeat(
    string Puid,
    string DisplayName,
    ColorType Color,
    bool IsHost = false);

public sealed record HostLobbyOptions
{
    public MapTypes Map { get; init; } = MapTypes.Skeld;
    public int NumImpostors { get; init; } = 1;
    public float PlayerSpeedMod { get; init; } = 1.0f;
    public float KillCooldown { get; init; } = 15.0f;
    public KillDistances KillDistance { get; init; } = KillDistances.Normal;
    public int NumCommonTasks { get; init; } = 1;
    public int NumLongTasks { get; init; } = 1;
    public int NumShortTasks { get; init; } = 2;
    public bool ConfirmImpostor { get; init; } = true;
    public bool VisualTasks { get; init; } = false;
    public byte MaxPlayers { get; init; } = 15;
    public int ExpectedJoiners { get; init; } = 14;
    public bool PublicWhenSeated { get; init; } = false;
    public bool AutoStartWhenReady { get; init; } = true;
    public TimeSpan StartGateDwell { get; init; } = TimeSpan.FromSeconds(3);
    public bool KickUnexpectedClients { get; init; } = true;
    public bool AcceptOnlineSceneAsReady { get; init; } = true;
    public bool AutoEndOnAbandonment { get; init; } = false;
    public int AbandonmentThreshold { get; init; } = 1;

    public int TargetTotalSeats => 1 + ExpectedJoiners;

    public NormalGameOptions ToNormalGameOptions() => new()
    {
        Map = Map,
        NumImpostors = NumImpostors,
        PlayerSpeedMod = PlayerSpeedMod,
        KillCooldown = KillCooldown,
        KillDistance = KillDistance,
        NumCommonTasks = NumCommonTasks,
        NumLongTasks = NumLongTasks,
        NumShortTasks = NumShortTasks,
        ConfirmImpostor = ConfirmImpostor,
        VisualTasks = VisualTasks,
        MaxPlayers = MaxPlayers,
        Keywords = GameKeywords.English,
        IsDefaults = false,
    };
}

/// <summary>
/// Transport-agnostic bridge allowing HostController to send root messages and RPCs through BotClient
/// without importing Transport or raw wire ids into Orchestrator/Host (G1).
/// </summary>
public interface IHostTransportBridge
{
    GameCode GameCode { get; }
    WorldModel World { get; }
    uint? NormalGameManagerNetId { get; }
    uint? GetPlayerInfoNetId(int clientId) => null;
    Task SendRootAsync(IRootMessage root, CancellationToken ct = default);
    Task SendRpcAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct = default);
}

/// <summary>
/// P2.1 & P2.4 HostController:
/// - Owns Skeld GameOptions (SyncSettingsRpc)
/// - Sets AlterGame private during seating, then public once all expected seats are held
/// - Kicks unexpected clients while building the private fleet
/// - Runs 14-Ready -> 3s dwell StartGate -> StartGame
/// - Sends EndGame on match abandonment or operator request
/// </summary>
public sealed class HostController
{
    private readonly IHostTransportBridge _bridge;
    private readonly HostLobbyOptions _options;
    private readonly Counters? _counters;
    private readonly ILogger<HostController>? _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    private readonly Dictionary<string, ExpectedRosterSeat> _expectedByPuid = new(StringComparer.Ordinal);
    private readonly HashSet<int> _kickedClientIds = new();
    private bool _settingsSynced;
    private DateTimeOffset? _countdownStartedAtUtc;

    public HostLifecycleState State { get; private set; } = HostLifecycleState.Idle;
    public HostLobbyOptions Options => _options;
    public DateTimeOffset? CountdownStartedAtUtc => _countdownStartedAtUtc;

    public HostController(
        IHostTransportBridge bridge,
        HostLobbyOptions? options = null,
        Counters? counters = null,
        ILogger<HostController>? logger = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _bridge = bridge;
        _options = options ?? new HostLobbyOptions();
        _counters = counters;
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void SetExpectedRoster(IEnumerable<ExpectedRosterSeat> seats)
    {
        _expectedByPuid.Clear();
        foreach (var seat in seats)
        {
            if (!string.IsNullOrWhiteSpace(seat.Puid))
            {
                _expectedByPuid[seat.Puid] = seat;
            }
        }
    }

    public async Task InitializeLobbyAsync(CancellationToken ct = default)
    {
        _kickedClientIds.Clear();
        _settingsSynced = false;
        _countdownStartedAtUtc = null;

        // Always mark private during initial seating so strangers don't race bot seats.
        await _bridge.SendRootAsync(
            new AlterGameMessage(_bridge.GameCode, AlterGameTags.ChangePrivacy, IsPublic: false),
            ct).ConfigureAwait(false);
        _bridge.World.IsPublicLobby = false;
        State = HostLifecycleState.PrivateSeating;

        await EnsureSettingsSyncedAsync(ct).ConfigureAwait(false);
        _logger?.LogInformation("Host initialized private lobby {LobbyCode} (targetSeats={Seats}).", _bridge.GameCode.Code, _options.TargetTotalSeats);
    }

    public async Task EnsureSettingsSyncedAsync(CancellationToken ct = default)
    {
        if (_settingsSynced || _bridge.NormalGameManagerNetId is not { } netId || netId == 0)
        {
            return;
        }

        await _bridge.SendRpcAsync(netId, new SyncSettingsRpc(_options.ToNormalGameOptions()), ct).ConfigureAwait(false);
        _settingsSynced = true;
    }

    public async Task EnforceSeatIdentityAsync(int clientId, uint playerNetId, CancellationToken ct = default)
    {
        if (playerNetId == 0 || !_bridge.World.PlayersByClientId.TryGetValue(clientId, out var player))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(player.Puid) &&
            _expectedByPuid.TryGetValue(player.Puid, out var expected))
        {
            await ApplyIdentityAsync(clientId, playerNetId, player, expected.DisplayName, expected.Color, ct).ConfigureAwait(false);
        }
    }

    public async Task HandleCheckNameAsync(int clientId, uint playerNetId, string requestedName, CancellationToken ct = default)
    {
        if (playerNetId == 0 || !_bridge.World.PlayersByClientId.TryGetValue(clientId, out var player))
        {
            return;
        }

        string chosenName;
        if (!string.IsNullOrWhiteSpace(player.Puid) &&
            _expectedByPuid.TryGetValue(player.Puid, out var expected))
        {
            chosenName = expected.DisplayName;
        }
        else if (_options.KickUnexpectedClients && _expectedByPuid.Count > 0 && State != HostLifecycleState.PublicOpen && !_bridge.World.IsPublicLobby)
        {
            return;
        }
        else
        {
            var baseName = string.IsNullOrWhiteSpace(requestedName) ? $"Player{clientId}" : requestedName.Trim();
            var taken = _bridge.World.PlayersByClientId.Values
                .Where(p => p.ClientId != clientId && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            chosenName = baseName;
            var suffix = 1;
            while (taken.Contains(chosenName))
            {
                chosenName = $"{baseName} {suffix++}";
            }
        }

        await _bridge.SendRpcAsync(playerNetId, new SetNameRpc(playerNetId, chosenName), ct).ConfigureAwait(false);
        player.Name = chosenName;
    }

    public async Task HandleCheckColorAsync(int clientId, uint playerNetId, string requestedColorName, CancellationToken ct = default)
    {
        if (playerNetId == 0 || !_bridge.World.PlayersByClientId.TryGetValue(clientId, out var player))
        {
            return;
        }

        ColorType chosenColor;
        if (!string.IsNullOrWhiteSpace(player.Puid) &&
            _expectedByPuid.TryGetValue(player.Puid, out var expected))
        {
            chosenColor = expected.Color;
        }
        else if (_options.KickUnexpectedClients && _expectedByPuid.Count > 0 && State != HostLifecycleState.PublicOpen && !_bridge.World.IsPublicLobby)
        {
            return;
        }
        else
        {
            var used = _bridge.World.PlayersByClientId.Values
                .Where(p => p.ClientId != clientId && Enum.TryParse<ColorType>(p.ColorName, ignoreCase: true, out _))
                .Select(p => Enum.TryParse<ColorType>(p.ColorName, ignoreCase: true, out var c) ? c : ColorType.Red)
                .ToHashSet();

            var requested = Enum.TryParse<ColorType>(requestedColorName, ignoreCase: true, out var parsed)
                ? parsed
                : ColorType.Red;
            chosenColor = !used.Contains(requested)
                ? requested
                : Enum.GetValues<ColorType>().FirstOrDefault(c => !used.Contains(c));
        }

        var colorNetId = _bridge.GetPlayerInfoNetId(clientId) ?? playerNetId;
        await _bridge.SendRpcAsync(playerNetId, new SetColorRpc(colorNetId, chosenColor), ct).ConfigureAwait(false);
        player.ColorName = chosenColor.ToString();
    }

    private async Task ApplyIdentityAsync(
        int clientId,
        uint playerNetId,
        PlayerState player,
        string name,
        ColorType color,
        CancellationToken ct)
    {
        await _bridge.SendRpcAsync(playerNetId, new SetNameRpc(playerNetId, name), ct).ConfigureAwait(false);
        var colorNetId = _bridge.GetPlayerInfoNetId(clientId) ?? playerNetId;
        await _bridge.SendRpcAsync(playerNetId, new SetColorRpc(colorNetId, color), ct).ConfigureAwait(false);
        player.Name = name;
        player.ColorName = color.ToString();
    }

    public int CountAuthorizedSeatedPlayers()
    {
        if (_expectedByPuid.Count == 0)
        {
            return _bridge.World.PlayersByClientId.Count;
        }

        return _bridge.World.PlayersByClientId.Values.Count(
            p => !string.IsNullOrWhiteSpace(p.Puid) && _expectedByPuid.ContainsKey(p.Puid));
    }

    public int CountReadyJoiners()
    {
        var localId = _bridge.World.LocalClientId;
        var count = 0;
        foreach (var player in _bridge.World.PlayersByClientId.Values)
        {
            if (player.ClientId == localId)
            {
                continue;
            }

            if (player.IsReady || (_options.AcceptOnlineSceneAsReady && player.InOnlineScene))
            {
                count++;
            }
        }

        return count;
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        await EnsureSettingsSyncedAsync(ct).ConfigureAwait(false);

        if (State is HostLifecycleState.PrivateSeating or HostLifecycleState.PublicOpen or HostLifecycleState.StartCountdown)
        {
            await KickUnexpectedClientsAsync(ct).ConfigureAwait(false);
        }

        if (State == HostLifecycleState.PrivateSeating && _options.PublicWhenSeated)
        {
            var seatedCount = CountAuthorizedSeatedPlayers();
            if (seatedCount >= _options.TargetTotalSeats)
            {
                await _bridge.SendRootAsync(
                    new AlterGameMessage(_bridge.GameCode, AlterGameTags.ChangePrivacy, IsPublic: true),
                    ct).ConfigureAwait(false);
                _bridge.World.IsPublicLobby = true;
                State = HostLifecycleState.PublicOpen;
                _logger?.LogInformation("Lobby {LobbyCode} reached {Count} seats -> flipped public.", _bridge.GameCode.Code, seatedCount);
            }
        }

        if (_options.AutoStartWhenReady &&
            _bridge.World.Phase == GamePhase.Lobby &&
            State is HostLifecycleState.PrivateSeating or HostLifecycleState.PublicOpen or HostLifecycleState.StartCountdown)
        {
            await EvaluateStartGateAsync(ct).ConfigureAwait(false);
        }

        if (_options.AutoEndOnAbandonment &&
            State == HostLifecycleState.InMatch &&
            _bridge.World.Phase is GamePhase.Dropship or GamePhase.Tasks or GamePhase.Meeting &&
            _bridge.World.PlayersByClientId.Count <= _options.AbandonmentThreshold)
        {
            _logger?.LogWarning("Match {LobbyCode} abandoned (players={Count} <= {Threshold}); ending match.",
                _bridge.GameCode.Code, _bridge.World.PlayersByClientId.Count, _options.AbandonmentThreshold);
            await EndGameAsync(GameOverReason.CrewmateDisconnect, ct).ConfigureAwait(false);
        }
    }

    public async Task KickUnexpectedClientsAsync(CancellationToken ct = default)
    {
        if (!_options.KickUnexpectedClients || _expectedByPuid.Count == 0 || State == HostLifecycleState.PublicOpen || _bridge.World.IsPublicLobby)
        {
            return;
        }

        var localId = _bridge.World.LocalClientId;
        foreach (var player in _bridge.World.PlayersByClientId.Values)
        {
            if (player.ClientId == localId || _kickedClientIds.Contains(player.ClientId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(player.Puid) || !_expectedByPuid.ContainsKey(player.Puid))
            {
                _kickedClientIds.Add(player.ClientId);
                await KickPlayerAsync(player.ClientId, isBan: false, ct).ConfigureAwait(false);
                _logger?.LogWarning("Kicked unexpected clientId={ClientId} (puid={Puid}, name={Name}) from lobby {LobbyCode}.",
                    player.ClientId, player.Puid, player.Name, _bridge.GameCode.Code);
            }
        }
    }

    public async Task EvaluateStartGateAsync(CancellationToken ct = default)
    {
        var readyJoiners = CountReadyJoiners();
        if (readyJoiners < _options.ExpectedJoiners)
        {
            if (_countdownStartedAtUtc is not null)
            {
                _countdownStartedAtUtc = null;
                State = _bridge.World.IsPublicLobby ? HostLifecycleState.PublicOpen : HostLifecycleState.PrivateSeating;
            }
            return;
        }

        var now = _utcNow();
        if (_countdownStartedAtUtc is null)
        {
            _countdownStartedAtUtc = now;
            State = HostLifecycleState.StartCountdown;
            _logger?.LogInformation("Start gate armed: {Ready}/{Expected} ready; holding {Dwell}s.",
                readyJoiners, _options.ExpectedJoiners, _options.StartGateDwell.TotalSeconds);
        }

        if (now - _countdownStartedAtUtc.Value >= _options.StartGateDwell)
        {
            await StartGameAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task StartGameAsync(CancellationToken ct = default)
    {
        _countdownStartedAtUtc = null;
        State = HostLifecycleState.InMatch;
        await _bridge.SendRootAsync(new StartGameMessage(_bridge.GameCode), ct).ConfigureAwait(false);
        _logger?.LogInformation("Host sent StartGame for lobby {LobbyCode}.", _bridge.GameCode.Code);
    }

    public async Task KickPlayerAsync(int clientId, bool isBan = false, CancellationToken ct = default)
    {
        _counters?.IncrementKick();
        await _bridge.SendRootAsync(new KickPlayerMessage(_bridge.GameCode, clientId, isBan), ct).ConfigureAwait(false);
    }

    public async Task EndGameAsync(GameOverReason reason = GameOverReason.CrewmateDisconnect, CancellationToken ct = default)
    {
        State = HostLifecycleState.Ended;
        await _bridge.SendRootAsync(new EndGameMessage(_bridge.GameCode, reason, ShowAd: false), ct).ConfigureAwait(false);
        _logger?.LogInformation("Host sent EndGame ({Reason}) for lobby {LobbyCode}.", reason, _bridge.GameCode.Code);
    }

    public void MarkRebuilding()
    {
        _countdownStartedAtUtc = null;
        _settingsSynced = false;
        _kickedClientIds.Clear();
        State = HostLifecycleState.Rebuilding;
    }
}
