using System.Diagnostics;
using System.Net;
using System.Numerics;
using BeanBot.Accounts;
using BeanBot.Behaviors;
using BeanBot.Matchmaker;
using BeanBot.Nav;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.Maps;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using BeanBot.World;
using Microsoft.Extensions.Logging;

namespace BeanBot.Commands;

public sealed record SharedRoomLookup(GameCode Code, IPEndPoint RelayEndPoint);

public sealed record BotStatusSnapshot(
    bool IsConnected,
    string ProfileMode,
    string Region,
    string AccountId,
    string DisplayName,
    string Puid,
    string LobbyCode,
    int LocalClientId,
    byte LocalPlayerId,
    GamePhase Phase,
    PlayerRole Role,
    bool IsAlive,
    Vector2 Position,
    int CompletedTasks,
    int TotalTasks,
    int PlayerCount,
    int DeadBodyCount,
    bool TasksEnabled,
    string? FollowTarget,
    bool InVent,
    long CurrentTick,
    object Counters);

/// <summary>
/// Headless single-bot client coordinating Accounts, Matchmaker, Transport/Hazel,
/// WorldModel, NavMesh/PathFollower, pure Behaviors, and Replay/Counters observability.
/// Ground Rules G1..G8 enforced throughout.
/// </summary>
public sealed class BotClient : IAsyncDisposable
{
    private readonly ServerProfile _profile;
    private readonly Vault _vault;
    private readonly DeviceAuthSource _auth;
    private readonly MatchmakerClient _matchmaker;
    private readonly NavMesh _mesh;
    private readonly ProtocolRegistry _registry;
    private readonly Counters _counters;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<BotClient>? _logger;
    private ServerProfile _activeProfile;

    public ServerProfile EffectiveProfile => _activeProfile;
    public ServerProfile? PreferredRegionProfile { get; set; }

    /// <summary>
    /// Room lookup already resolved by another bot in the same fleet. When it matches the requested code,
    /// this bot connects straight to that relay instead of sending its own matchmaker lookup.
    /// Cleared automatically if joining through it fails, so the next attempt does a fresh lookup.
    /// </summary>
    public SharedRoomLookup? SharedLookup { get; set; }

    /// <summary>Relay endpoint this bot resolved for its last join-by-code (null for host/auto joins).</summary>
    public IPEndPoint? ResolvedRelayEndPoint { get; private set; }

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<uint, int> _netIdToClientId = new();
    private readonly Dictionary<uint, int> _transformNetIdToClientId = new();
    private readonly Dictionary<uint, int> _playerInfoNetIdToClientId = new();
    private readonly Dictionary<int, uint> _playerInfoNetIdByClientId = new();
    private readonly Dictionary<int, (uint Level, string FriendCode)> _playerMetaByClientId = new();
    private readonly Dictionary<int, HostPlayerSpawnRecord> _hostPlayerSpawns = new();
    private readonly HashSet<int> _playersPositionedSinceStartGame = new();

    private SessionLock? _sessionLock;
    private ITransport? _transport;
    private HazelFramer? _framer;
    private ReplayLogger? _replay;
    private PathFollower _pathFollower;
    private bool _wasMovingLastTick;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private Task? _tickTask;
    private GameCode _currentGameCode;
    private uint? _normalGameManagerNetId;
    private uint? _meetingHudNetId;
    private uint _hostNextNetId = 1;
    private uint _hostNextPlayerInfoNetId = 100_000;
    private int _hostStartCounterSeq;
    private byte _localCosmeticSeq;
    private uint _hostVoteBanNetId;
    private uint _hostLobbyBehaviourNetId;
    private NormalGameOptions _hostGameOptions = new();
    private bool _isHostingSession;
    private bool _hostLobbySpawned;
    private bool _hostPendingMakePublic;
    private bool _hostSawInitialPrivateAlterGame;
    private string _matchmakerToken = string.Empty;
    private bool _lastMatchmakerTokenWasCached;
    private string? _pendingHelloReplayDetail;
    private readonly Dictionary<int, (uint ControlNetId, ColorType Color)> _pendingHostColorReplies = new();
    private AccountRecord? _activeAccount;
    private byte? _lastInboundCauseId;
    private string? _lastInboundCauseName;
    private DateTimeOffset _lastPingUtc = DateTimeOffset.UtcNow;
    private readonly TimeSpan _reconnectDeadlineWindow = TimeSpan.FromSeconds(60);
    private bool _disposed;

    private sealed record HostPlayerSpawnRecord(
        int ClientId,
        byte PlayerId,
        uint PlayerInfoNetId,
        uint ControlNetId,
        uint PhysicsNetId,
        uint TransformNetId,
        string Name,
        ColorType Color,
        uint Level,
        string FriendCode,
        string Puid);

    public WorldModel World { get; }
    public BotBehaviorState BehaviorState { get; } = new();
    public NavMesh Mesh => _mesh;
    public Counters Counters => _counters;
    public string? ReplayLogPath { get; private set; }
    public bool IsConnected { get; private set; }
    public bool DeterministicManualTick { get; set; }
    public GameCode CurrentGameCode => _currentGameCode;
    public uint? NormalGameManagerNetId => _normalGameManagerNetId;
    public string? PreferredColorName { get; set; }
    public QuickChatModes ChatMode { get; set; } = QuickChatModes.QuickChatOnly;

    /// <summary>
    /// Effective handshake chat mode. Anonymous EOS DeviceAuth accounts on InnerSloth Live do not
    /// have KWS Free-Chat permission and are rejected at Hello if they advertise FreeChatOrQuickChat.
    /// </summary>
    public QuickChatModes EffectiveChatMode =>
        (_profile.Mode == ServerProfileMode.Live || _profile.UseDeviceAuth)
            ? QuickChatModes.QuickChatOnly
            : ChatMode;
    public bool MakePublicWhenHosting { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool ManageSeatsHeldCounter { get; set; } = true;
    public TimeSpan HostGameResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan JoinGameResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public Func<Exception, Task>? OnConnectionLostAsync { get; set; }
    public Func<int, Task>? OnPlayerJoinedAsync { get; set; }
    public Func<int, uint, string, Task>? OnCheckNameRequestedAsync { get; set; }
    public Func<int, uint, string, Task>? OnCheckColorRequestedAsync { get; set; }

    public BotClient(
        ServerProfile profile,
        Vault vault,
        DeviceAuthSource auth,
        MatchmakerClient matchmaker,
        NavMesh? mesh = null,
        ProtocolRegistry? registry = null,
        Counters? counters = null,
        ILoggerFactory? loggerFactory = null)
    {
        _profile = profile;
        _activeProfile = profile;
        _vault = vault;
        _auth = auth;
        _matchmaker = matchmaker;
        _mesh = mesh ?? NavMesh.LoadSkeld();
        _registry = registry ?? ProtocolRegistry.Default;
        _counters = counters ?? new Counters();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<BotClient>();

        World = new WorldModel(loggerFactory?.CreateLogger<PhaseMachine>());
        _pathFollower = new PathFollower(_mesh);
        ChatMode = (profile.Mode == ServerProfileMode.Live || profile.UseDeviceAuth)
            ? QuickChatModes.QuickChatOnly
            : QuickChatModes.FreeChatOrQuickChat;
    }

    public BotStatusSnapshot GetStatus()
    {
        var local = World.LocalPlayer;
        var completed = World.LocalTasks.Count(t => t.IsCompleted);
        return new BotStatusSnapshot(
            IsConnected: IsConnected,
            ProfileMode: _activeProfile.Mode.ToString(),
            Region: _activeProfile.Region,
            AccountId: _activeAccount?.AccountId ?? "none",
            DisplayName: local?.Name ?? _activeAccount?.DisplayName ?? "BeanBot",
            Puid: local?.Puid ?? _activeAccount?.Puid ?? string.Empty,
            LobbyCode: World.LobbyCode,
            LocalClientId: World.LocalClientId,
            LocalPlayerId: World.LocalPlayerId,
            Phase: World.Phase,
            Role: World.LocalRole,
            IsAlive: World.IsLocalAlive,
            Position: local?.Position ?? Vector2.Zero,
            CompletedTasks: completed,
            TotalTasks: World.LocalTasks.Count,
            PlayerCount: World.PlayersByClientId.Count,
            DeadBodyCount: World.DeadBodies.Count,
            TasksEnabled: BehaviorState.TasksEnabled,
            FollowTarget: BehaviorState.FollowTargetName,
            InVent: World.InVent,
            CurrentTick: World.CurrentTick,
            Counters: _counters.Snapshot());
    }

    public async Task JoinAsync(
        string roomCodeOrAuto,
        string accountId = "bot-00",
        string displayName = "BeanBot",
        string replayDirectory = "data/replays",
        string locksDirectory = "data/locks",
        CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        _netIdToClientId.Clear();
        _transformNetIdToClientId.Clear();
        _playerInfoNetIdToClientId.Clear();
        _playerInfoNetIdByClientId.Clear();
        _playerMetaByClientId.Clear();
        _hostPlayerSpawns.Clear();
        _pendingHostColorReplies.Clear();
        _hostNextNetId = 1;
        _hostNextPlayerInfoNetId = 100_000;
        _hostStartCounterSeq = 0;
        _localCosmeticSeq = 0;
        _hostVoteBanNetId = 0;
        _hostLobbyBehaviourNetId = 0;
        _hostLobbySpawned = false;
        _hostPendingMakePublic = false;
        _hostSawInitialPrivateAlterGame = false;
        _isHostingSession = string.Equals(roomCodeOrAuto, "host", StringComparison.OrdinalIgnoreCase);
        _activeProfile = PreferredRegionProfile ?? _profile;
        var isAutoJoin = string.Equals(roomCodeOrAuto, "auto", StringComparison.OrdinalIgnoreCase);
        if (!_isHostingSession && !isAutoJoin)
        {
            var precheckCode = GameCode.From(roomCodeOrAuto);
            if (precheckCode.IsInvalid)
            {
                throw new RoomLookupException(
                    JoinLookupStatus.Error,
                    $"Invalid room code '{roomCodeOrAuto}'. Expected a 4- or 6-letter Among Us lobby code, 'auto', or 'host'.");
            }
        }
        _normalGameManagerNetId = null;
        _meetingHudNetId = null;
        World.PlayersByClientId.Clear();
        World.LocalTasks.Clear();
        World.DeadBodies.Clear();
        World.ClosedDoorRooms.Clear();
        World.LocalClientId = 0;
        World.LocalPlayerId = byte.MaxValue;
        World.HostClientId = 0;
        World.InVent = false;
        World.CurrentVentId = null;
        World.TransitionPhase(GamePhase.Lobby, "SessionReset", null);
        _counters.IncrementJoinAttempt();

        var account = _vault.GetOrCreate(accountId, displayName);
        if (account.State == AccountState.Banned)
        {
            throw new InvalidOperationException($"Account '{accountId}' is marked banned in vault.");
        }

        // G5: single live connection per account enforced by OS file lock.
        _sessionLock = SessionLock.Acquire(account.AccountId, locksDirectory);
        var usedSharedLookup = false;

        try
        {
            // Authenticate via EOS DeviceAuth (when UseDeviceAuth=true) or deterministic synthetic local token.
            var authResult = await _auth.AuthenticateAsync(account, _activeProfile, ct).ConfigureAwait(false);
            account = authResult.Account;
            _activeAccount = account;

            // Obtain Matchmaker identity token (/api/user), reusing a valid cached token when available.
            var (mmToken, _) = await ObtainMatchmakerTokenAsync(
                _activeProfile, account, authResult, forceRefresh: false, ct).ConfigureAwait(false);
            _matchmakerToken = mmToken;

            // Look up room, host a new room if "host", or browse first Skeld room if "auto".
            IPEndPoint relayEndpoint = default!;
            var isHosting = string.Equals(roomCodeOrAuto, "host", StringComparison.OrdinalIgnoreCase);
            ResolvedRelayEndPoint = null;
            usedSharedLookup = false;
            _pendingHelloReplayDetail = null;

            async Task ResolveEndpointAndConnectAsync()
            {
                if (isHosting)
                {
                    relayEndpoint = await _matchmaker.HostRoomAsync(_activeProfile, _matchmakerToken, ct).ConfigureAwait(false);
                    await OpenFramerAndSayHelloAsync(relayEndpoint, ct).ConfigureAwait(false);
                    _currentGameCode = await RequestHostedGameCodeAsync(ct).ConfigureAwait(false);
                }
                else if (string.Equals(roomCodeOrAuto, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    var games = await _matchmaker.BrowseByFilterAsync(
                        _activeProfile, _matchmakerToken, MapTypes.Skeld, ct: ct).ConfigureAwait(false);
                    var chosen = games.FirstOrDefault(g => g.PlayerCount < g.MaxPlayers)
                        ?? throw new InvalidOperationException("Matchmaker returned no joinable Skeld rooms.");
                    _currentGameCode = chosen.Code;
                    relayEndpoint = chosen.RelayEndPoint;
                    await OpenFramerAndSayHelloAsync(relayEndpoint, ct).ConfigureAwait(false);
                }
                else
                {
                    _currentGameCode = GameCode.From(roomCodeOrAuto);
                    // On live DTLS relays, each bot's matchmakerToken must be authorized via POST /api/games?gameId={code}.
                    // Non-DTLS local/private servers can skip redundant HTTP lookups via SharedLookup.
                    if (!_activeProfile.RequiresRelayDtlsAuth &&
                        SharedLookup is { } shared &&
                        shared.Code.Value == _currentGameCode.Value)
                    {
                        relayEndpoint = shared.RelayEndPoint;
                        usedSharedLookup = true;
                        _logger?.LogDebug("Reusing fleet room lookup for '{RoomCode}' -> {Relay} (no matchmaker request).", _currentGameCode.Code, relayEndpoint);
                    }
                    else
                    {
                        relayEndpoint = await LookupRoomWithRegionFallbackAsync(
                            account, authResult, _currentGameCode, ct).ConfigureAwait(false);
                    }

                    ResolvedRelayEndPoint = relayEndpoint;
                    await OpenFramerAndSayHelloAsync(relayEndpoint, ct).ConfigureAwait(false);
                }
            }

            try
            {
                await ResolveEndpointAndConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (_lastMatchmakerTokenWasCached && IsMatchmakerTokenRejected(ex))
            {
                _logger?.LogInformation("Cached matchmaker token was rejected; refreshing via /api/user.");
                InvalidateCachedMatchmakerToken(account);
                (_matchmakerToken, _) = await ObtainMatchmakerTokenAsync(
                    _activeProfile, account, authResult, forceRefresh: true, ct).ConfigureAwait(false);
                await ResolveEndpointAndConnectAsync().ConfigureAwait(false);
            }

            World.LobbyCode = _currentGameCode.Code;
            replayDirectory = Vault.ResolveSafePath(replayDirectory);
            Directory.CreateDirectory(replayDirectory);
            ReplayLogPath = Path.Combine(
                replayDirectory,
                $"replay-{account.AccountId}-{_currentGameCode.Code}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.jsonl");
            _replay = new ReplayLogger(ReplayLogPath, _registry, _counters);
            if (_pendingHelloReplayDetail is not null)
            {
                LogOutbound("Hello", detail: _pendingHelloReplayDetail);
                _pendingHelloReplayDetail = null;
            }

            await SendJoinGameAsync(ct).ConfigureAwait(false);

            if (!DeterministicManualTick)
            {
                _sessionCts = new CancellationTokenSource();
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token), CancellationToken.None);
                _tickTask = Task.Run(() => TickLoopAsync(_sessionCts.Token), CancellationToken.None);
            }

            _counters.IncrementJoinSuccess();
            if (ManageSeatsHeldCounter)
            {
                _counters.SetSeatsHeld(1);
            }
        }
        catch
        {
            if (usedSharedLookup)
            {
                // The shared relay may be stale (lobby moved/closed); force a real lookup on the next attempt.
                SharedLookup = null;
            }

            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenFramerAndSayHelloAsync(IPEndPoint relayEndpoint, CancellationToken ct)
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        uint authNonce = 0u;
        if (_activeProfile.RequiresRelayDtlsAuth)
        {
            var dtlsAuth = _activeProfile.CreateHazelDtlsAuthClient(_loggerFactory);
            authNonce = await dtlsAuth.AuthenticateAsync(
                relayEndpoint,
                ProtocolVersion.CLIENT_VERSION.Value,
                Platforms.Android,
                _matchmakerToken,
                friendCode: string.Empty,
                ct).ConfigureAwait(false);
        }

        _transport = _activeProfile.CreateRelayGameTransport(_loggerFactory);
        await _transport.ConnectAsync(relayEndpoint, ct).ConfigureAwait(false);
        _framer = new HazelFramer(_transport, _registry, logger: _loggerFactory?.CreateLogger<HazelFramer>())
        {
            IgnoreUnknownRootMessages = true,
        };

        // Handshake uses single CLIENT_VERSION constant via PlatformRecord.
        var crossplay = _activeProfile.RequiresRelayDtlsAuth ? CrossplayFlags.None : CrossplayFlags.All;
        await _framer.SendHelloAsync(
            _activeAccount!.DisplayName,
            _activeAccount.DeviceId,
            Language.English,
            EffectiveChatMode,
            crossplay,
            authNonce: authNonce,
            ct: ct).ConfigureAwait(false);
        var helloDetail = $"version={ProtocolVersion.CLIENT_VERSION},endpoint={relayEndpoint},nonce={authNonce}";
        if (_replay is not null)
        {
            LogOutbound("Hello", detail: helloDetail);
        }
        else
        {
            _pendingHelloReplayDetail = helloDetail;
        }

        if (_activeProfile.RequiresRelayDtlsAuth)
        {
            await _framer.WaitForAcknowledgementAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    private async Task<GameCode> RequestHostedGameCodeAsync(CancellationToken ct)
    {
        var framer = _framer ?? throw new InvalidOperationException("Framer not initialized.");
        var hostRequest = new HostGameMessage(
            GameOptionsOrCode.FromRequest(
                new NormalGameOptions(),
                CrossplayFlags.All,
                GameFilterOptions.CreateDefault()));
        await framer.SendReliableRootAsync(hostRequest, ct).ConfigureAwait(false);
        LogOutbound("HostGame");

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hostCts.CancelAfter(HostGameResponseTimeout);

        try
        {
            while (!hostCts.IsCancellationRequested)
            {
                var frame = await ReceiveNextFrameSliceAsync(framer, hostCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    continue;
                }

                if (frame.Disconnect is not null)
                {
                    var reasonStr = frame.Disconnect.Reason?.ToString() ?? "Unknown";
                    throw new IOException($"Server sent Disconnect during HostGame: {reasonStr} ({frame.Disconnect.CustomMessage})");
                }

                foreach (var msg in frame.Messages)
                {
                    if (msg is HostGameMessage hg && hg.Payload.GameCode is { } gc)
                    {
                        return gc;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fall through to descriptive TimeoutException below.
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out after {HostGameResponseTimeout.TotalSeconds:0.#}s waiting for HostGame response from relay ({_transport?.RemoteEndPoint}).");
    }

    private static async Task<InboundFrame?> ReceiveNextFrameSliceAsync(HazelFramer framer, CancellationToken overallCt)
    {
        await framer.PumpRetransmitsAsync(overallCt).ConfigureAwait(false);
        using var sliceCts = CancellationTokenSource.CreateLinkedTokenSource(overallCt);
        sliceCts.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            return await framer.ReceiveFrameAsync(sliceCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!overallCt.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task SendJoinGameAsync(CancellationToken ct)
    {
        var framer = _framer ?? throw new InvalidOperationException("Framer not initialized.");
        var joinMsg = new JoinGameMessage(_currentGameCode);
        await framer.SendReliableRootAsync(joinMsg, ct).ConfigureAwait(false);
        LogOutbound("JoinGame", detail: $"code={_currentGameCode.Code}");

        if (!DeterministicManualTick && !_isHostingSession)
        {
            await WaitForJoinedGameAsync(framer, ct).ConfigureAwait(false);
        }

        IsConnected = true;
    }

    private async Task WaitForJoinedGameAsync(HazelFramer framer, CancellationToken ct)
    {
        using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        joinCts.CancelAfter(JoinGameResponseTimeout);

        try
        {
            var joinedConfirmed = false;
            while (!joinedConfirmed && !joinCts.IsCancellationRequested)
            {
                var frame = await ReceiveNextFrameSliceAsync(framer, joinCts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    continue;
                }

                if (frame.Disconnect is not null)
                {
                    var reasonStr = frame.Disconnect.Reason?.ToString() ?? "Unknown";
                    _counters.IncrementDisconnect(reasonStr);
                    if (frame.Disconnect.Reason is DisconnectReason.Banned or DisconnectReason.Kicked)
                    {
                        _counters.IncrementKick();
                    }

                    throw CreateJoinRejectionException(frame.Disconnect.Reason, frame.Disconnect.CustomMessage);
                }

                foreach (var message in frame.Messages)
                {
                    if (message is RedirectMessage redirect)
                    {
                        _logger?.LogInformation("Server redirected connection to {Ip}:{Port}.", redirect.Address, redirect.Port);
                        var redirectEndpoint = new IPEndPoint(redirect.Address, redirect.Port);
                        ResolvedRelayEndPoint = redirectEndpoint;
                        await OpenFramerAndSayHelloAsync(redirectEndpoint, ct).ConfigureAwait(false);
                        framer = _framer ?? throw new InvalidOperationException("Framer not initialized after redirect.");
                        await framer.SendReliableRootAsync(new JoinGameMessage(_currentGameCode), ct).ConfigureAwait(false);
                        LogOutbound("JoinGame", detail: $"code={_currentGameCode.Code},redirect={redirectEndpoint}");
                        break;
                    }

                    await ProcessInboundRootMessageAsync(message, ct).ConfigureAwait(false);
                    if (message is JoinedGameMessage or WaitForHostMessage)
                    {
                        joinedConfirmed = true;
                    }
                }
            }

            if (joinedConfirmed)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fall through to descriptive TimeoutException below.
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out after {JoinGameResponseTimeout.TotalSeconds:0.#}s waiting for JoinedGame response for '{_currentGameCode.Code}' from relay ({_transport?.RemoteEndPoint}).");
    }

    private Exception CreateJoinRejectionException(DisconnectReason? reason, string? customMessage)
    {
        var detail = string.IsNullOrWhiteSpace(customMessage)
            ? reason?.ToString() ?? "Unknown"
            : $"{reason} ({customMessage})";

        return reason switch
        {
            DisconnectReason.GameFull =>
                new RoomLookupException(JoinLookupStatus.Full, $"Room '{_currentGameCode.Code}' is full ({detail})."),
            DisconnectReason.GameNotFound or DisconnectReason.IncorrectGame =>
                new RoomLookupException(JoinLookupStatus.NotFound, $"Room '{_currentGameCode.Code}' was not found on relay ({detail})."),
            DisconnectReason.TooManyRequests =>
                CreateRateLimitedException(_currentGameCode, detail),
            DisconnectReason.GameStarted =>
                new RoomLookupException(JoinLookupStatus.Error, $"Room '{_currentGameCode.Code}' has already started ({detail})."),
            DisconnectReason.Banned or
            DisconnectReason.Kicked or
            DisconnectReason.Custom or
            DisconnectReason.IncorrectVersion or
            DisconnectReason.MismatchedVersion or
            DisconnectReason.InvalidName or
            DisconnectReason.PlatformLock or
            DisconnectReason.SelfPlatformLock or
            DisconnectReason.QuickchatLock or
            DisconnectReason.Sanctions or
            DisconnectReason.PlatformParentalControlsBlock or
            DisconnectReason.PlatformUserBlock =>
                new RoomLookupException(JoinLookupStatus.Error, $"Relay rejected join to '{_currentGameCode.Code}': {detail}."),
            _ =>
                new IOException($"Server sent Disconnect during JoinGame for '{_currentGameCode.Code}': {detail}"),
        };
    }

    private static bool IsTerminalDisconnect(Exception ex) =>
        ex is RoomLookupException ||
        (ex is IOException &&
         (ex.Message.Contains(nameof(DisconnectReason.Banned), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.Kicked), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.Destroy), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.LobbyInactivity), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.GameNotFound), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.IncorrectVersion), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.MismatchedVersion), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.Custom), StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains(nameof(DisconnectReason.TooManyRequests), StringComparison.OrdinalIgnoreCase)));

    private async Task ConnectTransportAndJoinAsync(IPEndPoint relayEndpoint, CancellationToken ct)
    {
        await OpenFramerAndSayHelloAsync(relayEndpoint, ct).ConfigureAwait(false);
        await SendJoinGameAsync(ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var backoff = new ReconnectBackoff(baseDelayMs: 1000, maxDelayMs: 60_000);
        DateTimeOffset? disconnectedAt = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_framer is null)
                {
                    break;
                }

                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _framer.PumpRetransmitsAsync(ct).ConfigureAwait(false);
                    if ((DateTimeOffset.UtcNow - _lastPingUtc) >= TimeSpan.FromSeconds(2))
                    {
                        await _framer.SendPingAsync(ct).ConfigureAwait(false);
                        _lastPingUtc = DateTimeOffset.UtcNow;
                    }
                }
                finally
                {
                    _sendLock.Release();
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(250));

                InboundFrame? frame = null;
                try
                {
                    frame = await _framer.ReceiveFrameAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    continue;
                }

                if (frame is null)
                {
                    continue;
                }

                backoff.Reset();
                disconnectedAt = null;

                if (frame.Disconnect is not null)
                {
                    var reasonStr = frame.Disconnect.Reason?.ToString() ?? "Unknown";
                    _counters.IncrementDisconnect(reasonStr);
                    if (frame.Disconnect.Reason is DisconnectReason.Banned or DisconnectReason.Kicked)
                    {
                        _counters.IncrementKick();
                    }

                    throw new IOException($"Server sent Disconnect: {reasonStr} ({frame.Disconnect.CustomMessage})");
                }

                foreach (var message in frame.Messages)
                {
                    await ProcessInboundRootMessageAsync(message, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                if (ManageSeatsHeldCounter)
                {
                    _counters.SetSeatsHeld(0);
                }
                _counters.IncrementDisconnect(ex.GetType().Name);

                if (!AutoReconnect || IsTerminalDisconnect(ex))
                {
                    if (OnConnectionLostAsync is not null)
                    {
                        await OnConnectionLostAsync(ex).ConfigureAwait(false);
                    }
                    break;
                }

                disconnectedAt ??= DateTimeOffset.UtcNow;

                var elapsed = DateTimeOffset.UtcNow - disconnectedAt.Value;
                if (elapsed >= _reconnectDeadlineWindow)
                {
                    _logger?.LogError(ex, "Reconnect window (60s) exhausted for {Account}. Stopping bot.", _activeAccount?.AccountId);
                    if (OnConnectionLostAsync is not null)
                    {
                        await OnConnectionLostAsync(ex).ConfigureAwait(false);
                    }
                    break;
                }

                var delay = backoff.NextDelay();
                _logger?.LogWarning(ex, "Connection dropped; reconnecting in {DelayMs}ms (attempt {Attempt}).", delay.TotalMilliseconds, backoff.Attempt);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    var lookup = await _matchmaker.JoinByCodeAsync(_activeProfile, _matchmakerToken, _currentGameCode, ct).ConfigureAwait(false);
                    if (lookup.Status == JoinLookupStatus.Ok && lookup.RelayEndPoint is not null)
                    {
                        await ConnectTransportAndJoinAsync(lookup.RelayEndPoint, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception retryEx)
                {
                    _logger?.LogWarning(retryEx, "Reconnect attempt {Attempt} failed.", backoff.Attempt);
                }
            }
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var nextTickMs = 0L;
        while (!ct.IsCancellationRequested)
        {
            var intervalMs = Math.Max(20, (int)Math.Round(1000.0 / Math.Max(1.0, World.TickRateHz)));
            nextTickMs += intervalMs;
            var delayMs = (int)(nextTickMs - sw.ElapsedMilliseconds);
            try
            {
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                else if (delayMs < -intervalMs * 2)
                {
                    nextTickMs = sw.ElapsedMilliseconds;
                }

                if (IsConnected)
                {
                    await RunMatchTickAsync(intervalMs / 1000.0, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in 10Hz bot tick loop.");
            }
        }
    }

    /// <summary>
    /// Processes a single inbound root message against WorldModel &amp; PhaseMachine.
    /// Public so in-process deterministic integration tests can feed root frames directly.
    /// </summary>
    public async Task ProcessInboundRootMessageAsync(IRootMessage message, CancellationToken ct = default)
    {
        _lastInboundCauseId = message.MessageId;
        _lastInboundCauseName = _registry.GetRootName(message.MessageId);
        _logger?.LogDebug("Inbound root {Name}: {Msg}", _lastInboundCauseName, message);

        switch (message)
        {
            case JoinedGameMessage joined:
                World.LobbyCode = joined.GameCode.Code;
                World.LocalClientId = joined.PlayerClientId;
                World.HostClientId = joined.HostClientId;
                var isHost = joined.HostClientId == joined.PlayerClientId;
                var localJoined = EnsurePlayerEntry(joined.PlayerClientId, _activeAccount?.DisplayName ?? "BeanBot", _activeAccount?.Puid ?? string.Empty, isHost: isHost);
                if (!string.IsNullOrWhiteSpace(PreferredColorName))
                {
                    localJoined.ColorName = PreferredColorName;
                }
                if (_activeAccount is not null)
                {
                    _playerMetaByClientId[joined.PlayerClientId] = (1u, _activeAccount.FriendCode ?? string.Empty);
                }

                foreach (var other in joined.OtherPlayers)
                {
                    EnsurePlayerEntry(other.ClientId, other.Name, other.Puid, isHost: joined.HostClientId == other.ClientId);
                    _playerMetaByClientId[other.ClientId] = (other.PlayerLevel, other.FriendCode);
                }

                World.TransitionPhase(GamePhase.Lobby, nameof(JoinedGameMessage), joined.MessageId);
                if (_framer is not null)
                {
                    await SendGameDataAsync(new[]
                    {
                        GameDataSubMessage.FromSceneChange(World.LocalClientId, "OnlineGame"),
                    }, reliable: true, ct).ConfigureAwait(false);
                    localJoined.InOnlineScene = true;
                    LogOutbound("SceneChange", detail: "OnlineGame");

                    if (_isHostingSession && isHost && !_hostLobbySpawned && _profile.Mode != ServerProfileMode.Live)
                    {
                        await InitializeHostLobbyObjectsAsync(localJoined, ct).ConfigureAwait(false);
                    }
                }

                if (OnPlayerJoinedAsync is not null)
                {
                    await OnPlayerJoinedAsync(joined.PlayerClientId).ConfigureAwait(false);
                }
                break;

            case JoinGameMessage { ServerDisconnectReason: { } rejectReason } joinReject:
                var rejectStr = rejectReason.ToString();
                _counters.IncrementDisconnect(rejectStr);
                if (rejectReason is DisconnectReason.Banned or DisconnectReason.Kicked)
                {
                    _counters.IncrementKick();
                }

                throw CreateJoinRejectionException(rejectReason, joinReject.ServerCustomMessage);

            case JoinGameMessage joinNotify when joinNotify.ServerJoinedPlayer is { } jp:
                var joinedPlayer = EnsurePlayerEntry(jp.ClientId, jp.Name, jp.Puid, isHost: jp.ClientId == World.HostClientId);
                _playerMetaByClientId[jp.ClientId] = (jp.PlayerLevel, jp.FriendCode);
                if (joinNotify.ServerHostClientId > 0)
                {
                    World.HostClientId = joinNotify.ServerHostClientId;
                }
                _ = joinedPlayer;
                break;

            case JoinGameMessage joinHostNotify when joinHostNotify.ServerHostClientId > 0:
                World.HostClientId = joinHostNotify.ServerHostClientId;
                break;

            case RedirectMessage redirect:
                _logger?.LogInformation("Server redirected connection to {Ip}:{Port}.", redirect.Address, redirect.Port);
                if (_framer is not null)
                {
                    await ConnectTransportAndJoinAsync(new IPEndPoint(redirect.Address, redirect.Port), ct).ConfigureAwait(false);
                }
                break;

            case WaitForHostMessage waitForHost:
                World.HostClientId = waitForHost.PlayerClientId;
                break;

            case StartGameMessage startGame:
                World.TransitionPhase(GamePhase.Dropship, nameof(StartGameMessage), startGame.MessageId);
                _pathFollower.Clear();
                _wasMovingLastTick = false;
                _playersPositionedSinceStartGame.Clear();
                BehaviorState.ResetForNewMatch(clearFollow: false);
                World.DeadBodies.Clear();
                World.ClosedDoorRooms.Clear();
                if (_framer is not null)
                {
                    await SendGameDataAsync(new[] { GameDataSubMessage.FromReady(World.LocalClientId) }, reliable: true, ct).ConfigureAwait(false);
                    LogOutbound("Ready", detail: $"clientId={World.LocalClientId}");
                }
                break;

            case EndGameMessage endGame:
                World.TransitionPhase(GamePhase.End, nameof(EndGameMessage), endGame.MessageId);
                _pathFollower.Clear();
                _wasMovingLastTick = false;
                BehaviorState.ResetForNewMatch();
                World.DeadBodies.Clear();
                break;

            case RemovePlayerMessage removed:
                World.PlayersByClientId.TryRemove(removed.PlayerId, out _);
                _hostPlayerSpawns.Remove(removed.PlayerId);
                _playerInfoNetIdByClientId.Remove(removed.PlayerId);
                _playerMetaByClientId.Remove(removed.PlayerId);
                if (removed.HostId > 0)
                {
                    World.HostClientId = removed.HostId;
                }
                break;

            case AlterGameMessage alter:
                if (!alter.IsPublic && _isHostingSession && MakePublicWhenHosting && World.LocalClientId == World.HostClientId)
                {
                    _hostSawInitialPrivateAlterGame = true;
                    if (_hostLobbySpawned && _hostPendingMakePublic)
                    {
                        _hostPendingMakePublic = false;
                        await SendRootMessageAsync(new AlterGameMessage(_currentGameCode, AlterGameTags.ChangePrivacy, IsPublic: true), reliable: true, ct).ConfigureAwait(false);
                        World.IsPublicLobby = true;
                    }
                    else
                    {
                        World.IsPublicLobby = false;
                    }
                }
                else
                {
                    if (alter.IsPublic)
                    {
                        _hostPendingMakePublic = false;
                    }

                    World.IsPublicLobby = alter.IsPublic;
                }
                break;

            case GameDataMessage gameData:
                await ProcessGameDataSubsAsync(gameData.RawSubMessages, ct).ConfigureAwait(false);
                break;

            case GameDataToMessage gameDataTo:
                if (gameDataTo.TargetClientId == World.LocalClientId || gameDataTo.TargetClientId < 0)
                {
                    await ProcessGameDataSubsAsync(gameDataTo.RawSubMessages, ct).ConfigureAwait(false);
                }
                break;
        }
    }

    public uint? GetPlayerInfoNetId(int clientId) =>
        _playerInfoNetIdByClientId.TryGetValue(clientId, out var netId) ? netId : null;

    /// <summary>
    /// On live matchmaker relays, a newly hosted lobby takes ~3s to become queryable by code/public list.
    /// Polls JoinByCode until indexed or <paramref name="timeout"/> expires.
    /// </summary>
    public async Task<bool> WaitForLobbyIndexedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_activeProfile.Mode != ServerProfileMode.Live ||
            _currentGameCode.Value == 0 ||
            string.IsNullOrWhiteSpace(_matchmakerToken))
        {
            return true;
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                var lookup = await _matchmaker.JoinByCodeAsync(_activeProfile, _matchmakerToken, _currentGameCode, ct).ConfigureAwait(false);
                if (lookup.Status == JoinLookupStatus.Ok)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Transient matchmaker lookup failure; retry until deadline.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task ProcessGameDataSubsAsync(byte[] rawSubMessages, CancellationToken ct)
    {
        IReadOnlyList<GameDataSubMessage> subs;
        try
        {
            subs = GameDataSubMessage.ParseAll(rawSubMessages);
        }
        catch (ProtocolException ex)
        {
            _logger?.LogDebug(ex, "Skipping malformed GameData container.");
            return;
        }

        foreach (var sub in subs)
        {
            try
            {
                switch (sub.Kind)
                {
                    case GameDataKind.Spawn when sub.TryParseSpawn(out var spawn) && spawn is not null:
                        await HandleSpawnSubMessageAsync(spawn, ct).ConfigureAwait(false);
                        break;

                    case GameDataKind.Data when sub.TryParseData(out var targetNetId, out var data):
                        HandleDataSubMessage(targetNetId, data);
                        break;

                    case GameDataKind.Rpc when sub.TryParseRpc(_registry, out var rpcTargetNetId, out var rpc) && rpc is not null:
                        await HandleRpcSubMessageAsync(rpcTargetNetId, rpc, ct).ConfigureAwait(false);
                        break;

                    case GameDataKind.Ready when sub.TryParseReady(out var readyClientId):
                        EnsurePlayerEntry(readyClientId, string.Empty, string.Empty, isHost: readyClientId == World.HostClientId).IsReady = true;
                        break;

                    case GameDataKind.SceneChange when sub.TryParseSceneChange(out var sceneClientId, out var sceneName):
                        var scenePlayer = EnsurePlayerEntry(sceneClientId, string.Empty, string.Empty, isHost: sceneClientId == World.HostClientId);
                        scenePlayer.InOnlineScene = sceneName.Equals("OnlineGame", StringComparison.OrdinalIgnoreCase);
                        if (_isHostingSession &&
                            World.LocalClientId == World.HostClientId &&
                            scenePlayer.InOnlineScene &&
                            sceneClientId != World.LocalClientId &&
                            _hostLobbySpawned)
                        {
                            await SpawnJoinerForHostAsync(scenePlayer, ct).ConfigureAwait(false);
                        }

                        if (World.Phase == GamePhase.Lobby && scenePlayer.InOnlineScene && OnPlayerJoinedAsync is not null)
                        {
                            await OnPlayerJoinedAsync(sceneClientId).ConfigureAwait(false);
                        }
                        break;
                }
            }
            catch (ProtocolException ex)
            {
                _logger?.LogDebug(ex, "Ignoring unrecognized GameData sub-message (tag={Tag}, packetId={PacketId}).", sub.Tag, ex.PacketId);
            }
        }
    }

    private async Task HandleSpawnSubMessageAsync(SpawnObjectPacket spawn, CancellationToken ct)
    {
        foreach (var comp in spawn.Components)
        {
            _netIdToClientId[comp.NetId] = spawn.OwnerClientId;
        }

        if (spawn.SpawnId == SpawnObjectId.PlayerInfo && spawn.Components.Count >= 1)
        {
            if (ComponentDataCodec.TryParsePlayerInfoInitial(spawn.Components[0].InitialData, out var pi) && pi is not null)
            {
                _netIdToClientId[spawn.Components[0].NetId] = pi.ClientId;
                _playerInfoNetIdToClientId[spawn.Components[0].NetId] = pi.ClientId;
                _playerInfoNetIdByClientId[pi.ClientId] = spawn.Components[0].NetId;
                if (_pendingHostColorReplies.Remove(pi.ClientId, out var pendingColor))
                {
                    await SendRpcAsync(
                        pendingColor.ControlNetId,
                        new SetColorRpc(spawn.Components[0].NetId, pendingColor.Color),
                        ct).ConfigureAwait(false);
                }

                var infoPlayer = EnsurePlayerEntry(pi.ClientId, pi.PlayerName, string.Empty, isHost: pi.ClientId == World.HostClientId);
                if (pi.PlayerId != byte.MaxValue)
                {
                    infoPlayer.PlayerId = pi.PlayerId;
                    if (pi.ClientId == World.LocalClientId)
                    {
                        World.LocalPlayerId = pi.PlayerId;
                    }
                }

                if (Enum.IsDefined(typeof(ColorType), (byte)pi.ColorId))
                {
                    infoPlayer.ColorName = ((ColorType)(byte)pi.ColorId).ToString();
                }

                if (_isHostingSession &&
                    _profile.Mode == ServerProfileMode.Live &&
                    pi.ClientId == World.LocalClientId &&
                    World.LocalClientId == World.HostClientId &&
                    !_hostLobbySpawned &&
                    _framer is not null)
                {
                    await InitializeHostLobbyObjectsAsync(infoPlayer, ct).ConfigureAwait(false);
                }
            }

            return;
        }

        if (spawn.SpawnId == SpawnObjectId.PlayerControl && spawn.Components.Count >= 1)
        {
            var player = EnsurePlayerEntry(spawn.OwnerClientId, string.Empty, string.Empty, isHost: spawn.OwnerClientId == World.HostClientId);
            if (string.IsNullOrWhiteSpace(player.Name))
            {
                player.Name = $"Player{spawn.OwnerClientId}";
            }
            player.NetId = spawn.Components[0].NetId;
            if (spawn.Components[0].InitialData.Length > 0)
            {
                var pid = ComponentDataCodec.ParsePlayerControlPlayerId(spawn.Components[0].InitialData, initialState: true);
                if (pid != byte.MaxValue)
                {
                    player.PlayerId = pid;
                }
            }

            if (spawn.Components.Count >= 3)
            {
                var transformComp = spawn.Components[2];
                player.TransformNetId = transformComp.NetId;
                _transformNetIdToClientId[transformComp.NetId] = spawn.OwnerClientId;
                if (transformComp.InitialData.Length >= 6)
                {
                    using var r = BeanBot.Protocol.Hazel.MessageReader.Get(transformComp.InitialData);
                    var xform = CustomNetworkTransformUpdate.Deserialize(r, initialState: true);
                    var rawPos = xform.Positions[0];
                    var initialPos = World.Phase == GamePhase.Lobby
                        ? rawPos
                        : _mesh.SnapToNavMesh(rawPos, _logger).SnappedPoint;
                    if (World.Phase != GamePhase.Lobby)
                    {
                        _playersPositionedSinceStartGame.Add(player.ClientId);
                    }
                    World.UpdatePlayerPosition(player.ClientId, initialPos, xform.SequenceId);
                    if (spawn.OwnerClientId == World.LocalClientId)
                    {
                        World.SyncTransformSequenceId(xform.SequenceId);
                    }
                }
            }

            if (player.ClientId == World.LocalClientId)
            {
                World.LocalPlayerId = player.PlayerId;
                var localColor = ColorPalette.TryParse(PreferredColorName, out var preferredColor)
                    ? preferredColor
                    : (ColorType)((Math.Abs(player.ClientId) + 1) % 18);
                player.ColorName = localColor.ToString();

                // Negotiate name & color with the host (via GameDataTo so server anti-cheat does not kick).
                if (_framer is not null && player.NetId != 0 && !player.IsHost)
                {
                    var desiredName = _activeAccount?.DisplayName ?? "BeanBot";
                    var subs = new[]
                    {
                        GameDataSubMessage.FromRpc(player.NetId, new CheckNameRpc(desiredName)),
                        GameDataSubMessage.FromRpc(player.NetId, new CheckColorRpc(localColor)),
                    };
                    if (World.HostClientId > 0)
                    {
                        await SendGameDataToAsync(World.HostClientId, subs, reliable: true, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendGameDataAsync(subs, reliable: true, ct).ConfigureAwait(false);
                    }

                    // Live relays kick clients after ~15s (DisconnectReason.Hacking) unless SetLevel and cosmetic RPCs are broadcast on their owned PlayerControl.
                    var localLevel = _playerMetaByClientId.TryGetValue(player.ClientId, out var pm) && pm.Level > 0 ? pm.Level : 1u;
                    await SendGameDataAsync(BuildLocalLevelAndCosmeticRpcSubs(player.NetId, localLevel), reliable: true, ct).ConfigureAwait(false);
                }
            }
        }
        else if (spawn.SpawnId == SpawnObjectId.NormalGameManager && spawn.Components.Count >= 1)
        {
            _normalGameManagerNetId = spawn.Components[0].NetId;
            if (ComponentDataCodec.TryExtractGameManagerOptions(spawn.Components[0].InitialData, out var opts) && opts is NormalGameOptions normal)
            {
                ApplyNormalGameOptions(normal);
            }
        }
        else if (spawn.SpawnId == SpawnObjectId.SkeldShipStatus)
        {
            World.TransitionPhase(GamePhase.Tasks, "Spawn:SkeldShipStatus", null);
            World.ClosedDoorRooms.Clear();
            _pathFollower.Clear();
            SnapAllPlayersToSkeldSpawnRing(initialSpawn: true);
        }
        else if (spawn.SpawnId == SpawnObjectId.MeetingHud && spawn.Components.Count >= 1)
        {
            _meetingHudNetId = spawn.Components[0].NetId;
            World.Meeting.Reset(World.LocalPlayerId, null);
            World.TransitionPhase(GamePhase.Meeting, "Spawn:MeetingHud", null);
            BehaviorState.ResetForNewMeeting();
            _pathFollower.Clear();
            _wasMovingLastTick = false;
        }
    }

    private void SnapAllPlayersToSkeldSpawnRing(bool initialSpawn)
    {
        // ShipStatus.SpawnPlayer (GameAssembly 0x1807B5790) calls CustomNetworkTransform.SnapTo
        // locally for every player without sending SnapToRpc, snapping from Dropship/Meeting
        // coordinates to the Cafeteria spawn ring and advancing lastSequenceId by +2.
        var playerCount = Math.Max(1, World.PlayersByClientId.Count);
        var center = initialSpawn ? _mesh.InitialSpawnCenter : _mesh.MeetingSpawnCenter;
        var nextLocalSeq = World.AdvanceTransformSequenceBy(2);
        _wasMovingLastTick = false;

        foreach (var p in World.PlayersByClientId.Values)
        {
            var pid = p.PlayerId == byte.MaxValue ? (byte)0 : p.PlayerId;
            var spawnPoint = ComputeSkeldSpawnPosition(pid, playerCount, center);
            var (snapped, _, _) = _mesh.SnapToNavMesh(spawnPoint, _logger);
            var shouldSnapPosition = !initialSpawn || !_playersPositionedSinceStartGame.Contains(p.ClientId);
            if (p.ClientId == World.LocalClientId)
            {
                World.UpdatePlayerPosition(p.ClientId, shouldSnapPosition ? snapped : p.Position, nextLocalSeq);
            }
            else if (shouldSnapPosition)
            {
                World.UpdatePlayerPosition(p.ClientId, snapped);
            }
        }
    }

    private Vector2 ComputeSkeldSpawnPosition(byte playerId, int playerCount, Vector2? centerOverride = null)
    {
        var angleRad = -(2f * MathF.PI / Math.Max(1, playerCount)) * (playerId - 1);
        var (sin, cos) = MathF.SinCos(angleRad);
        // Extensions.Rotate(Vector2.up, deg): (-sin, cos) scaled inside SpawnRadius.
        var offset = new Vector2(-sin, cos) * (_mesh.SpawnRadius * 0.8f);
        return (centerOverride ?? _mesh.InitialSpawnCenter) + offset;
    }

    private void HandleDataSubMessage(uint targetNetId, byte[] data)
    {
        if (_normalGameManagerNetId == targetNetId &&
            ComponentDataCodec.TryExtractGameManagerOptions(data, out var opts) &&
            opts is NormalGameOptions normal)
        {
            ApplyNormalGameOptions(normal);
            return;
        }

        if (_playerInfoNetIdToClientId.TryGetValue(targetNetId, out _) &&
            ComponentDataCodec.TryParsePlayerInfoInitial(data, out var updatedPi) &&
            updatedPi is not null)
        {
            _netIdToClientId[targetNetId] = updatedPi.ClientId;
            _playerInfoNetIdToClientId[targetNetId] = updatedPi.ClientId;
            _playerInfoNetIdByClientId[updatedPi.ClientId] = targetNetId;
            var infoPlayer = EnsurePlayerEntry(updatedPi.ClientId, updatedPi.PlayerName, string.Empty, isHost: updatedPi.ClientId == World.HostClientId);
            if (!string.IsNullOrWhiteSpace(updatedPi.PlayerName))
            {
                infoPlayer.Name = updatedPi.PlayerName;
            }
            if (updatedPi.PlayerId != byte.MaxValue)
            {
                infoPlayer.PlayerId = updatedPi.PlayerId;
                if (updatedPi.ClientId == World.LocalClientId)
                {
                    World.LocalPlayerId = updatedPi.PlayerId;
                }
            }
            if (Enum.IsDefined(typeof(ColorType), (byte)updatedPi.ColorId))
            {
                infoPlayer.ColorName = ((ColorType)(byte)updatedPi.ColorId).ToString();
            }
            return;
        }

        if (_transformNetIdToClientId.TryGetValue(targetNetId, out var clientId) && data.Length >= 3)
        {
            using var reader = BeanBot.Protocol.Hazel.MessageReader.Get(data);
            var update = CustomNetworkTransformUpdate.Deserialize(reader, initialState: false);
            if (update.Positions.Count > 0)
            {
                World.PlayersByClientId.TryGetValue(clientId, out var trackedPlayer);
                for (var i = 0; i < update.Positions.Count; i++)
                {
                    var sid = unchecked((ushort)(update.SequenceId + i));
                    if (trackedPlayer is not null &&
                        trackedPlayer.LastTransformSequenceId != 0 &&
                        sid != trackedPlayer.LastTransformSequenceId &&
                        !WorldModel.IsSequenceNewer(sid, trackedPlayer.LastTransformSequenceId))
                    {
                        continue;
                    }

                    var rawPos = update.Positions[i];
                    if (World.Phase != GamePhase.Lobby)
                    {
                        _playersPositionedSinceStartGame.Add(clientId);
                    }
                    if (clientId == World.LocalClientId && World.Phase != GamePhase.Lobby)
                    {
                        var (snapped, _, _) = _mesh.SnapToNavMesh(rawPos, _logger);
                        World.UpdatePlayerPosition(clientId, snapped, sid);
                    }
                    else
                    {
                        World.UpdatePlayerPosition(clientId, rawPos, sid);
                    }
                }
            }
        }
    }

    private async Task HandleRpcSubMessageAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct)
    {
        _netIdToClientId.TryGetValue(targetNetId, out var ownerClientId);
        World.PlayersByClientId.TryGetValue(ownerClientId, out var targetPlayer);

        switch (rpc)
        {
            case SyncSettingsRpc syncSettings when syncSettings.Options is NormalGameOptions normal:
                ApplyNormalGameOptions(normal);
                break;

            case CheckNameRpc checkName:
                if (OnCheckNameRequestedAsync is not null)
                {
                    await OnCheckNameRequestedAsync(ownerClientId, targetNetId, checkName.Name).ConfigureAwait(false);
                }
                else if (_isHostingSession && World.LocalClientId == World.HostClientId && targetPlayer is not null && targetNetId != 0)
                {
                    await ReplyCheckNameAsHostAsync(targetPlayer, targetNetId, checkName.Name, ct).ConfigureAwait(false);
                }
                break;

            case CheckColorRpc checkColor:
                if (OnCheckColorRequestedAsync is not null)
                {
                    await OnCheckColorRequestedAsync(ownerClientId, targetNetId, checkColor.Color.ToString()).ConfigureAwait(false);
                }
                else if (_isHostingSession && World.LocalClientId == World.HostClientId && targetPlayer is not null && targetNetId != 0)
                {
                    await ReplyCheckColorAsHostAsync(targetPlayer, targetNetId, checkColor.Color, ct).ConfigureAwait(false);
                }
                break;

            case SetNameRpc setName:
            {
                var namedPlayer = targetPlayer;
                if (namedPlayer is null && _netIdToClientId.TryGetValue(setName.NetId, out var piClientId))
                {
                    World.PlayersByClientId.TryGetValue(piClientId, out namedPlayer);
                }
                if (namedPlayer is not null && !string.IsNullOrWhiteSpace(setName.Name))
                {
                    namedPlayer.Name = setName.Name;
                }
                break;
            }

            case SetColorRpc setColor:
            {
                var coloredPlayer = targetPlayer;
                if (coloredPlayer is null && _netIdToClientId.TryGetValue(setColor.NetId, out var piClientId))
                {
                    World.PlayersByClientId.TryGetValue(piClientId, out coloredPlayer);
                }
                if (coloredPlayer is not null)
                {
                    coloredPlayer.ColorName = setColor.Color.ToString();
                }
                break;
            }

            case SetRoleRpc setRole when targetPlayer is not null:
                targetPlayer.Role = MapRoleType(setRole.Role, targetPlayer.IsAlive);
                targetPlayer.IsAlive = setRole.Role is not (RoleTypes.CrewmateGhost or RoleTypes.ImpostorGhost);
                if (World.Phase == GamePhase.Dropship)
                {
                    World.TransitionPhase(GamePhase.Tasks, nameof(SetRoleRpc), setRole.RpcCallId);
                }
                break;

            case SetInfectedRpc setInfected:
                foreach (var p in World.PlayersByClientId.Values)
                {
                    p.Role = setInfected.InfectedIds.Contains(p.PlayerId)
                        ? PlayerRole.Impostor
                        : PlayerRole.Crewmate;
                }

                if (World.Phase == GamePhase.Dropship)
                {
                    World.TransitionPhase(GamePhase.Tasks, nameof(SetInfectedRpc), setInfected.RpcCallId);
                }
                break;

            case SetTasksRpc setTasks when targetPlayer is not null && targetPlayer.ClientId == World.LocalClientId:
                var isInitialTaskSync = World.LocalTasks.Count == 0;
                World.LocalTasks.Clear();
                for (var i = 0; i < setTasks.TaskTypeIds.Length; i++)
                {
                    var rawTaskIndex = setTasks.TaskTypeIds[i];
                    var taskType = SkeldCatalog.MapSkeldTaskIndexToTaskType(rawTaskIndex);
                    if (SkeldCatalog.Tasks.TryGetValue(taskType, out var meta))
                    {
                        World.LocalTasks.Add(new AssignedTask
                        {
                            TaskId = (uint)i,
                            TaskTypeName = meta.Name,
                            Room = meta.DefaultSteps[0].Room,
                            StepPositions = meta.DefaultSteps.Select(s => s.Position).ToArray(),
                        });
                    }
                    else
                    {
                        World.LocalTasks.Add(new AssignedTask
                        {
                            TaskId = (uint)i,
                            TaskTypeName = $"Task#{rawTaskIndex}",
                            Room = "Admin",
                            StepPositions = new[] { new Vector2(2.5f, -6.3f) },
                        });
                    }
                }

                // In Among Us.exe (CoStartGame), ShipStatus.Begin -> RpcSetTasks runs only after all clients
                // send ClientReady, right before ShipStatus.SpawnPlayer and IntroCutscene.CoBegin (~4.5s).
                if (isInitialTaskSync && BehaviorState.IntroSettleDurationSec > 0 && BehaviorState.IntroSettleRemainingSec > 0)
                {
                    BehaviorState.IntroSettleRemainingSec = BehaviorState.IntroSettleDurationSec;
                    _pathFollower.Clear();
                }
                break;

            case CompleteTaskRpc completeTask when targetPlayer is not null && targetPlayer.ClientId == World.LocalClientId:
                var task = World.LocalTasks.FirstOrDefault(t => t.TaskId == completeTask.TaskId);
                if (task is not null)
                {
                    task.CurrentStep++;
                    if (task.CurrentStep >= task.TotalSteps)
                    {
                        task.IsCompleted = true;
                    }
                }
                break;

            case MurderPlayerRpc murder when murder.Result.HasFlag(MurderResultFlags.Succeeded):
                var victim = World.PlayersByClientId.Values.FirstOrDefault(p => p.NetId == murder.TargetNetId);
                if (victim is not null && victim.IsAlive)
                {
                    victim.IsAlive = false;
                    victim.Role = victim.IsImpostor ? PlayerRole.ImpostorGhost : PlayerRole.CrewmateGhost;
                    World.DeadBodies.Add(new DeadBodyState(victim.PlayerId, victim.Position));
                }

                if (targetPlayer is not null && targetPlayer.ClientId == World.LocalClientId)
                {
                    World.LastKillTimestampSec = World.MatchTimeSec;
                    _counters.IncrementKill();
                }
                break;

            case ReportDeadBodyRpc report:
                World.Meeting.Reset(targetPlayer?.PlayerId ?? byte.MaxValue, report.TargetPlayerId == byte.MaxValue ? null : report.TargetPlayerId);
                for (var i = 0; i < World.DeadBodies.Count; i++)
                {
                    if (World.DeadBodies[i].PlayerId == report.TargetPlayerId)
                    {
                        World.DeadBodies[i] = World.DeadBodies[i] with { Reported = true };
                    }
                }

                World.TransitionPhase(GamePhase.Meeting, nameof(ReportDeadBodyRpc), report.RpcCallId);
                BehaviorState.ResetForNewMeeting();
                _pathFollower.Clear();
                break;

            case StartMeetingRpc startMeeting:
                World.Meeting.Reset(targetPlayer?.PlayerId ?? byte.MaxValue, startMeeting.TargetPlayerId == byte.MaxValue ? null : startMeeting.TargetPlayerId);
                World.TransitionPhase(GamePhase.Meeting, nameof(StartMeetingRpc), startMeeting.RpcCallId);
                BehaviorState.ResetForNewMeeting();
                _pathFollower.Clear();
                break;

            case CastVoteRpc castVote:
                World.Meeting.VotesByVoter[castVote.VoterPlayerId] = castVote.SuspectPlayerId;
                break;

            case VotingCompleteRpc votingComplete:
                World.Meeting.IsVotingComplete = true;
                World.Meeting.IsTie = votingComplete.IsTie;
                if (votingComplete.ExiledPlayerId != byte.MaxValue)
                {
                    World.Meeting.ExiledPlayerId = votingComplete.ExiledPlayerId;
                    var exiled = World.FindPlayerByPlayerId(votingComplete.ExiledPlayerId);
                    if (exiled is not null)
                    {
                        exiled.IsAlive = false;
                        exiled.Role = exiled.IsImpostor ? PlayerRole.ImpostorGhost : PlayerRole.CrewmateGhost;
                    }
                }
                break;

            case CloseMeetingRpc closeMeeting:
                World.DeadBodies.Clear();
                World.TransitionPhase(GamePhase.Tasks, nameof(CloseMeetingRpc), closeMeeting.RpcCallId);
                World.LastKillTimestampSec = World.MatchTimeSec - World.Settings.KillCooldownSeconds + Math.Min(10.0, World.Settings.KillCooldownSeconds);
                _pathFollower.Clear();
                SnapAllPlayersToSkeldSpawnRing(initialSpawn: false);
                break;

            case EnterVentRpc enterVent when targetPlayer is not null && targetPlayer.ClientId == World.LocalClientId:
                World.InVent = true;
                World.CurrentVentId = enterVent.VentId;
                _wasMovingLastTick = false;
                break;

            case ExitVentRpc:
                if (targetPlayer is not null && targetPlayer.ClientId == World.LocalClientId)
                {
                    World.InVent = false;
                    World.CurrentVentId = null;
                    World.LastVentTimestampSec = World.MatchTimeSec;
                    _wasMovingLastTick = false;
                }
                break;

            case SnapToRpc snap when targetPlayer is not null:
                var snapPos = World.Phase == GamePhase.Lobby
                    ? snap.Position
                    : _mesh.SnapToNavMesh(snap.Position, _logger).SnappedPoint;
                if (World.Phase != GamePhase.Lobby)
                {
                    _playersPositionedSinceStartGame.Add(targetPlayer.ClientId);
                }
                World.UpdatePlayerPosition(targetPlayer.ClientId, snapPos, snap.MinSequenceId);
                if (targetPlayer.ClientId == World.LocalClientId)
                {
                    World.SyncTransformSequenceId(snap.MinSequenceId);
                    _pathFollower.Clear();
                    _wasMovingLastTick = false;
                }
                break;

            case CloseDoorsOfTypeRpc closeDoors:
                World.ClosedDoorRooms.Add(closeDoors.SystemType.ToString());
                break;

            case RepairSystemRpc repair when repair.SystemType == SystemTypes.Doors:
                World.ClosedDoorRooms.Clear();
                break;

            case SetStartCounterRpc startCounter:
                World.LastStartCounter = startCounter.StartCounter;
                break;
        }
    }

    /// <summary>
    /// Runs one tick of world time + pure behavior evaluation + throttled movement emission + G4 replay logging.
    /// </summary>
    public async Task RunMatchTickAsync(double dtSeconds, CancellationToken ct = default)
    {
        World.CurrentTick++;
        World.MatchTimeSec += dtSeconds;
        if (World.Phase == GamePhase.Meeting)
        {
            BehaviorState.MatchElapsedInCurrentMeetingSec += dtSeconds;
        }

        var local = World.LocalPlayer;
        if (local is null)
        {
            return;
        }

        var actions = EvaluateBehaviors(dtSeconds);
        Vector2? desiredDestination = null;
        float stopDist = PathFollower.WaypointArrivalEpsilon;

        foreach (var action in actions)
        {
            switch (action)
            {
                case HoldPositionAction:
                    _pathFollower.Clear();
                    break;

                case MoveToAction move:
                    desiredDestination = move.Destination;
                    stopDist = move.StopDistance;
                    break;

                case CompleteTaskAction completeTask:
                    await ExecuteCompleteTaskAsync(local, completeTask, ct).ConfigureAwait(false);
                    break;

                case KillAction kill:
                    await ExecuteKillAsync(local, kill, ct).ConfigureAwait(false);
                    break;

                case EnterVentAction enterVent:
                    await ExecuteEnterVentAsync(local, enterVent.VentId, ct).ConfigureAwait(false);
                    break;

                case ExitVentAction exitVent:
                    await ExecuteExitVentAsync(local, exitVent.VentId, ct).ConfigureAwait(false);
                    break;

                case ReportBodyAction report:
                    await ExecuteReportBodyAsync(local, report.BodyPlayerId, ct).ConfigureAwait(false);
                    break;

                case CastVoteAction vote:
                    await ExecuteVoteAsync(local, vote.TargetPlayerId, ct).ConfigureAwait(false);
                    break;
            }
        }

        // G3: Movement emission clamped to PlayerSpeed; freeze in Meeting (F6).
        // Among Us.exe CustomNetworkTransform.FixedUpdate (0x180670A80) / MoveTowardNextPoint (0x180671120)
        // runs at 50 Hz (fixedDeltaTime = 0.02s) and stalls with ShouldExtendCurrentFrame = true (idealSpeed = 0)
        // whenever incomingPosQueue.Count <= 5 on initial movement from rest. Emitting 6 sub-positions on the
        // first tick from rest and 5 sub-positions per 100ms tick thereafter feeds incomingPosQueue at 50 Hz.
        var movedThisTick = false;
        if (desiredDestination.HasValue && World.Phase != GamePhase.Meeting && !World.InVent)
        {
            var distToGoal = Vector2.Distance(local.Position, desiredDestination.Value);
            if (distToGoal > stopDist)
            {
                var prevPos = local.Position;
                var substeps = _wasMovingLastTick ? 5 : 6;
                var subPositions = new List<Vector2>(substeps);
                Vector2 nextPos;

                if (World.Phase == GamePhase.Lobby)
                {
                    // Dropship lobby uses direct straight-line stepping (not Skeld A*).
                    var maxSpeed = MathF.Max(0.1f, World.Settings.PlayerSpeed) * PathFollower.BaseWalkSpeedUnitsPerSec;
                    var maxStep = maxSpeed * MathF.Max(0.01f, (float)dtSeconds);
                    var delta = desiredDestination.Value - prevPos;
                    nextPos = distToGoal <= maxStep
                        ? desiredDestination.Value
                        : prevPos + (delta / distToGoal) * maxStep;

                    if (Vector2.DistanceSquared(nextPos, prevPos) > 1e-6f)
                    {
                        for (var s = 1; s <= substeps; s++)
                        {
                            subPositions.Add(Vector2.Lerp(prevPos, nextPos, s / (float)substeps));
                        }
                    }
                }
                else
                {
                    if (!_pathFollower.CurrentGoal.HasValue ||
                        Vector2.Distance(_pathFollower.CurrentGoal.Value, desiredDestination.Value) > 0.35f)
                    {
                        _pathFollower.SetDestination(prevPos, desiredDestination.Value, World.ClosedDoorRooms);
                    }

                    var rawPolyline = new List<Vector2>(substeps + 1) { prevPos };
                    var cursor = prevPos;
                    var subDt = MathF.Max(0.002f, (float)dtSeconds / substeps);
                    for (var s = 0; s < substeps; s++)
                    {
                        var step = _pathFollower.Step(
                            cursor,
                            World.Settings.PlayerSpeed,
                            subDt,
                            freezeMovement: World.Phase == GamePhase.Meeting,
                            World.ClosedDoorRooms);
                        if (Vector2.DistanceSquared(step.Position, cursor) > 1e-8f)
                        {
                            rawPolyline.Add(step.Position);
                            cursor = step.Position;
                        }
                    }

                    nextPos = cursor;
                    if (Vector2.DistanceSquared(nextPos, prevPos) > 1e-6f)
                    {
                        SamplePolylineEvenly(rawPolyline, substeps, subPositions);
                    }
                }

                if (subPositions.Count > 0)
                {
                    movedThisTick = true;
                    _wasMovingLastTick = true;
                    var firstSeq = World.NextTransformSequenceId();
                    var lastSeq = subPositions.Count > 1
                        ? World.AdvanceTransformSequenceBy((ushort)(subPositions.Count - 1))
                        : firstSeq;
                    World.UpdatePlayerPosition(local.ClientId, nextPos, lastSeq);

                    if (_framer is not null && local.TransformNetId != 0)
                    {
                        var xformSub = GameDataSubMessage.FromTransform(
                            local.TransformNetId,
                            new CustomNetworkTransformUpdate(firstSeq, subPositions));
                        // CustomNetworkTransform uses SendOption.None (unreliable) in Among Us.exe
                        // (InnerNetClient.SendAllStreamedObjects / PackAndSendDirtyStreamedObjects).
                        await SendGameDataAsync(new[] { xformSub }, reliable: false, ct).ConfigureAwait(false);
                        LogOutbound("TransformUpdate", detail: $"seq={firstSeq}..{lastSeq},netId={local.TransformNetId},pos=({nextPos.X:F2},{nextPos.Y:F2})");
                    }
                }
            }
        }

        if (!movedThisTick)
        {
            _wasMovingLastTick = false;
        }
    }

    private static void SamplePolylineEvenly(IReadOnlyList<Vector2> polyline, int sampleCount, List<Vector2> output)
    {
        if (polyline.Count < 2 || sampleCount <= 0)
        {
            return;
        }

        var cumulative = new float[polyline.Count];
        var totalLen = 0f;
        for (var i = 1; i < polyline.Count; i++)
        {
            totalLen += Vector2.Distance(polyline[i - 1], polyline[i]);
            cumulative[i] = totalLen;
        }

        if (totalLen <= 1e-6f)
        {
            return;
        }

        var segIndex = 0;
        for (var s = 1; s <= sampleCount; s++)
        {
            if (s == sampleCount)
            {
                output.Add(polyline[^1]);
                break;
            }

            var targetDist = totalLen * (s / (float)sampleCount);
            while (segIndex < polyline.Count - 2 && cumulative[segIndex + 1] < targetDist)
            {
                segIndex++;
            }

            var segStartDist = cumulative[segIndex];
            var segLen = cumulative[segIndex + 1] - segStartDist;
            var t = segLen > 1e-6f ? Math.Clamp((targetDist - segStartDist) / segLen, 0f, 1f) : 1f;
            output.Add(Vector2.Lerp(polyline[segIndex], polyline[segIndex + 1], t));
        }
    }

    private IReadOnlyList<BotAction> EvaluateBehaviors(double dtSeconds)
    {
        if (World.Phase == GamePhase.Meeting)
        {
            return Vote.Evaluate(World, BehaviorState);
        }

        if (World.Phase == GamePhase.Lobby)
        {
            return Follow.Evaluate(World, BehaviorState, _mesh, dtSeconds);
        }

        if (World.Phase != GamePhase.Tasks)
        {
            return Array.Empty<BotAction>();
        }

        if (BehaviorState.IntroSettleRemainingSec > 0)
        {
            BehaviorState.IntroSettleRemainingSec = Math.Max(0, BehaviorState.IntroSettleRemainingSec - dtSeconds);
            return Array.Empty<BotAction>();
        }

        var reportActions = Report.Evaluate(World);
        if (reportActions.Count > 0)
        {
            return reportActions;
        }

        var killActions = Kill.EvaluateCommanded(World, BehaviorState, _mesh);
        if (killActions.Count > 0)
        {
            return killActions;
        }

        var ventActions = Vent.Evaluate(World, BehaviorState, _mesh);
        if (ventActions.Count > 0)
        {
            return ventActions;
        }

        if (BehaviorState.FollowTargetPlayerId.HasValue || !string.IsNullOrWhiteSpace(BehaviorState.FollowTargetName))
        {
            var followActions = Follow.Evaluate(World, BehaviorState, _mesh, dtSeconds);
            if (followActions.Count > 0)
            {
                return followActions;
            }
        }

        return TaskLoop.Evaluate(World, BehaviorState, _mesh, dtSeconds);
    }

    private async Task ExecuteCompleteTaskAsync(PlayerState local, CompleteTaskAction action, CancellationToken ct)
    {
        var task = World.LocalTasks.FirstOrDefault(t => t.TaskId == action.TaskId);
        if (task is not null)
        {
            task.CurrentStep++;
            if (task.CurrentStep >= task.TotalSteps)
            {
                task.IsCompleted = true;
            }
        }

        if (action.IsFakeTask)
        {
            LogOutbound("FakeTaskStep", detail: $"taskId={action.TaskId},step={action.StepIndex}");
            return;
        }

        if (task is not null && !task.IsCompleted)
        {
            LogOutbound("TaskStep", detail: $"taskId={action.TaskId},step={action.StepIndex},remaining={task.TotalSteps - task.CurrentStep}");
            return;
        }

        _counters.IncrementTaskCompletion();
        if (_framer is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new CompleteTaskRpc(action.TaskId), ct).ConfigureAwait(false);
        }

        LogOutbound("CompleteTask", detail: $"taskId={action.TaskId},step={action.StepIndex}");
    }

    private async Task ExecuteKillAsync(PlayerState local, KillAction action, CancellationToken ct)
    {
        var victim = World.FindPlayerByPlayerId(action.TargetPlayerId);
        if (victim is null)
        {
            return;
        }

        World.LastKillTimestampSec = World.MatchTimeSec;
        if (_framer is not null && local.NetId != 0 && victim.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new CheckMurderRpc(victim.NetId), ct).ConfigureAwait(false);
        }

        LogOutbound("CheckMurder", detail: $"targetPlayerId={victim.PlayerId}");
    }

    private async Task ExecuteEnterVentAsync(PlayerState local, int ventId, CancellationToken ct)
    {
        World.InVent = true;
        World.CurrentVentId = ventId;
        var ventPos = _mesh.Vents.FirstOrDefault(v => v.Id == ventId)?.Position ?? local.Position;
        var seq = World.NextTransformSequenceId();
        World.UpdatePlayerPosition(local.ClientId, ventPos, seq);

        if (_framer is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new EnterVentRpc(ventId), ct).ConfigureAwait(false);
        }

        LogOutbound("EnterVent", detail: $"ventId={ventId}");
    }

    private async Task ExecuteExitVentAsync(PlayerState local, int exitVentId, CancellationToken ct)
    {
        World.InVent = false;
        World.CurrentVentId = null;
        World.LastVentTimestampSec = World.MatchTimeSec;
        var exitPos = _mesh.Vents.FirstOrDefault(v => v.Id == exitVentId)?.Position ?? local.Position;
        var (snappedExit, _, _) = _mesh.SnapToNavMesh(exitPos, _logger);
        var seq = World.NextTransformSequenceId();
        World.UpdatePlayerPosition(local.ClientId, snappedExit, seq);

        if (_framer is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new ExitVentRpc(exitVentId), ct).ConfigureAwait(false);
            if (local.TransformNetId != 0)
            {
                await SendRpcAsync(local.TransformNetId, new SnapToRpc(snappedExit, seq), ct).ConfigureAwait(false);
            }
        }

        LogOutbound("ExitVent", detail: $"exitVentId={exitVentId},pos=({snappedExit.X:F2},{snappedExit.Y:F2})");
    }

    private async Task ExecuteReportBodyAsync(PlayerState local, byte bodyPlayerId, CancellationToken ct)
    {
        _counters.IncrementReport();
        if (_framer is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new ReportDeadBodyRpc(bodyPlayerId), ct).ConfigureAwait(false);
        }

        LogOutbound("ReportDeadBody", detail: $"bodyPlayerId={bodyPlayerId}");
    }

    private async Task ExecuteVoteAsync(PlayerState local, sbyte targetPlayerId, CancellationToken ct)
    {
        _counters.IncrementVote();
        World.Meeting.VotesByVoter[local.PlayerId] = targetPlayerId;

        if (_framer is not null)
        {
            var voteTargetNetId = _meetingHudNetId ?? local.NetId;
            if (voteTargetNetId != 0)
            {
                await SendRpcAsync(voteTargetNetId, new CastVoteRpc(local.PlayerId, targetPlayerId), ct).ConfigureAwait(false);
            }
        }

        LogOutbound("CastVote", detail: $"voter={local.PlayerId},suspect={targetPlayerId}");
    }

    // Operator command helpers
    public void CommandFollow(string targetNameOrClear, bool killRangeHold = false)
        => _ = CommandFollowWithStatus(targetNameOrClear, killRangeHold);

    public (bool Matched, string Message) CommandFollowWithStatus(string targetNameOrClear, bool killRangeHold = false)
    {
        var clean = targetNameOrClear?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clean) ||
            string.Equals(clean, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "stop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "clear", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(clean, "none", StringComparison.OrdinalIgnoreCase))
        {
            BehaviorState.FollowTargetName = null;
            BehaviorState.FollowTargetPlayerId = null;
            BehaviorState.FollowKillRangeHold = false;
            BehaviorState.TooCloseAccumulatedSec = 0;
            _pathFollower.Clear();
            LogOutbound("CommandFollow", detail: "cleared");
            return (true, "Follow cleared.");
        }

        var matched = World.FindPlayerByQuery(clean, excludeLocal: true);
        BehaviorState.FollowTargetName = matched?.Name ?? clean;
        BehaviorState.FollowTargetPlayerId = matched is not null && matched.PlayerId != byte.MaxValue
            ? matched.PlayerId
            : null;
        BehaviorState.FollowKillRangeHold = killRangeHold;
        BehaviorState.TooCloseAccumulatedSec = 0;
        _pathFollower.Clear();
        LogOutbound("CommandFollow", detail: $"target={BehaviorState.FollowTargetName},matched={matched is not null},killHold={killRangeHold}");

        if (matched is not null)
        {
            return (true, $"Following {matched.Name} (playerId={matched.PlayerId}, pos=({matched.Position.X:F2},{matched.Position.Y:F2}), killHold={killRangeHold}).");
        }

        return (false, $"Follow queued for '{clean}' (not currently visible in room; will attach when seen).");
    }

    public (bool Accepted, string Message) CommandKill(string targetName, bool allowBlind = false)
    {
        var target = World.FindPlayerByName(targetName);
        if (target is null)
        {
            return (false, $"Player '{targetName}' not found.");
        }

        var check = Kill.CheckPreconditions(World, _mesh, target, allowBlind);
        BehaviorState.CommandedKillTargetPlayerId = target.PlayerId;
        BehaviorState.CommandedKillAllowBlind = allowBlind;
        LogOutbound("CommandKill", detail: $"target={targetName},blind={allowBlind},precheck={check.Reason}");
        return (true, $"Kill queued for {target.Name} (precheck={check.Reason})");
    }

    public void CommandTasks(bool enabled)
    {
        BehaviorState.TasksEnabled = enabled;
        LogOutbound("CommandTasks", detail: $"enabled={enabled}");
    }

    public (bool Accepted, string Message) CommandVent(int ventId, int? exitVentId = null)
    {
        var vent = _mesh.Vents.FirstOrDefault(v => v.Id == ventId);
        if (vent is null)
        {
            return (false, $"Unknown Skeld vent id {ventId}.");
        }

        BehaviorState.CommandedVentId = ventId;
        BehaviorState.PendingVentExitId = exitVentId;
        LogOutbound("CommandVent", detail: $"ventId={ventId},exitId={exitVentId}");
        return (true, $"Vent sequence queued for vent {vent.Id} ({vent.Name}).");
    }

    public (bool Accepted, string Message) CommandVote(string targetOrSkip)
    {
        if (string.Equals(targetOrSkip, "skip", StringComparison.OrdinalIgnoreCase))
        {
            BehaviorState.CommandedVoteTargetId = MeetingState.SkipVoteId;
            LogOutbound("CommandVote", detail: "skip");
            return (true, "Vote queued: skip");
        }

        if (string.Equals(targetOrSkip, "clear", StringComparison.OrdinalIgnoreCase))
        {
            BehaviorState.CommandedVoteTargetId = null;
            BehaviorState.HasVotedThisMeeting = false;
            LogOutbound("CommandVote", detail: "clear");
            return (true, "Cleared queued vote.");
        }

        var target = World.FindPlayerByName(targetOrSkip);
        if (target is null)
        {
            return (false, $"Player '{targetOrSkip}' not found.");
        }

        BehaviorState.CommandedVoteTargetId = (sbyte)target.PlayerId;
        LogOutbound("CommandVote", detail: $"target={target.Name}({target.PlayerId})");
        return (true, $"Vote queued for {target.Name}");
    }

    public Task SayAsync(string message, CancellationToken ct = default) =>
        SayWithStatusAsync(message, ct);

    public async Task<(bool Ok, string Message)> SayWithStatusAsync(string message, CancellationToken ct = default)
    {
        var clean = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0)
        {
            return (false, "Message cannot be empty.");
        }

        if (clean.Length > 100)
        {
            clean = clean[..100];
        }

        if (SendQuickChatRpc.TryParseStringNameId(clean, out _) || EffectiveChatMode == QuickChatModes.QuickChatOnly)
        {
            if (!SendQuickChatRpc.TryResolvePhrase(clean, out var stringNameId, out var canonicalKey))
            {
                return (false, "Quick Chat Only mode requires a preset phrase (e.g. hello, hi, yes, no, thanks, sorry, gg, bye, ready, start, wait, follow, who, where, why, nice, oops, agree, qc:<id>).");
            }

            await SendResolvedQuickChatAsync(stringNameId, canonicalKey, ct).ConfigureAwait(false);
            return (true, $"Sent quick chat '{canonicalKey}' (qc:{stringNameId}).");
        }

        var local = World.LocalPlayer;
        if (_framer is not null && local is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, new SendChatRpc(clean), ct).ConfigureAwait(false);
        }

        LogOutbound("SendChat", detail: clean);
        return (true, $"Sent chat '{clean}'.");
    }

    public async Task SayQuickChatAsync(string phraseKey, CancellationToken ct = default)
    {
        if (!SendQuickChatRpc.TryResolvePhrase(phraseKey, out var stringNameId, out var canonicalKey))
        {
            return;
        }

        await SendResolvedQuickChatAsync(stringNameId, canonicalKey, ct).ConfigureAwait(false);
    }

    private async Task SendResolvedQuickChatAsync(int stringNameId, string canonicalKey, CancellationToken ct)
    {
        var rpc = SendQuickChatRpc.Simple(stringNameId, canonicalKey);
        var local = World.LocalPlayer;
        if (_framer is not null && local is not null && local.NetId != 0)
        {
            await SendRpcAsync(local.NetId, rpc, ct).ConfigureAwait(false);
        }

        LogOutbound("SendQuickChat", detail: $"{canonicalKey}:{stringNameId}");
    }

    public async Task SendRootMessageAsync(IRootMessage root, bool reliable = true, CancellationToken ct = default)
    {
        if (_framer is null)
        {
            throw new InvalidOperationException("Cannot send root message: framer is not connected.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (reliable)
            {
                await _framer.SendReliableRootAsync(root, ct).ConfigureAwait(false);
            }
            else
            {
                await _framer.SendUnreliableRootAsync(root, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }

        LogOutbound(_registry.GetRootName(root.MessageId));
    }

    public Task SendHostRpcAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct = default)
        => SendRpcAsync(targetNetId, rpc, ct);

    public async Task StopAsync()
    {
        IsConnected = false;
        if (ManageSeatsHeldCounter)
        {
            _counters.SetSeatsHeld(0);
        }

        if (_sessionCts is not null)
        {
            _sessionCts.Cancel();
            try
            {
                if (_tickTask is not null) await _tickTask.ConfigureAwait(false);
            }
            catch { }

            try
            {
                if (_receiveTask is not null) await _receiveTask.ConfigureAwait(false);
            }
            catch { }

            _sessionCts.Dispose();
            _sessionCts = null;
        }

        if (_framer is not null)
        {
            try
            {
                await _framer.SendDisconnectAsync(new DisconnectMessage(true, DisconnectReason.ExitGame, null)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Ignored error while sending graceful disconnect.");
            }
            _framer = null;
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        _sessionLock?.Dispose();
        _sessionLock = null;
        _replay?.Dispose();
        _replay = null;
    }

    private async Task SendRpcAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct)
    {
        if (_isHostingSession &&
            _profile.Mode == ServerProfileMode.Live &&
            rpc is SetColorRpc setColor &&
            (setColor.NetId == 0 || setColor.NetId == targetNetId) &&
            _netIdToClientId.TryGetValue(targetNetId, out var clientId))
        {
            if (_playerInfoNetIdByClientId.TryGetValue(clientId, out var resolvedPi) && resolvedPi != 0)
            {
                rpc = new SetColorRpc(resolvedPi, setColor.Color);
            }
            else
            {
                // Live relay kicks+bans (Hacking) when SetColor targets PlayerControl instead of PlayerInfo;
                // defer until Relay's Spawn(PlayerInfo) arrives.
                _pendingHostColorReplies[clientId] = (targetNetId, setColor.Color);
                return;
            }
        }

        var sub = GameDataSubMessage.FromRpc(targetNetId, rpc);
        await SendGameDataAsync(new[] { sub }, reliable: true, ct).ConfigureAwait(false);
    }

    private async Task SendGameDataAsync(IEnumerable<GameDataSubMessage> subs, bool reliable, CancellationToken ct)
    {
        if (_framer is null)
        {
            return;
        }

        var raw = GameDataSubMessage.SerializeAll(subs);
        await SendRootMessageAsync(new GameDataMessage(_currentGameCode, raw), reliable, ct).ConfigureAwait(false);
    }

    private async Task SendGameDataToAsync(int targetClientId, IEnumerable<GameDataSubMessage> subs, bool reliable, CancellationToken ct)
    {
        if (_framer is null)
        {
            return;
        }

        var raw = GameDataSubMessage.SerializeAll(subs);
        await SendRootMessageAsync(new GameDataToMessage(_currentGameCode, targetClientId, raw), reliable, ct).ConfigureAwait(false);
    }

    private uint AllocateHostNetId() => _hostNextNetId++;

    private uint AllocateHostPlayerInfoNetId() => _hostNextPlayerInfoNetId++;

    private byte AllocateFreePlayerId()
    {
        var used = _hostPlayerSpawns.Values.Select(p => p.PlayerId).ToHashSet();
        for (byte pid = 0; pid < 15; pid++)
        {
            if (!used.Contains(pid))
            {
                return pid;
            }
        }

        return (byte)(_hostPlayerSpawns.Count % 255);
    }

    private ColorType PickFreeHostColor(string? preferredColorName = null)
    {
        var used = _hostPlayerSpawns.Values.Select(p => p.Color).ToHashSet();
        if (ColorPalette.TryParse(preferredColorName, out var preferred) && !used.Contains(preferred))
        {
            return preferred;
        }

        foreach (var c in ColorPalette.AppendixAOrder)
        {
            if (!used.Contains(c))
            {
                return c;
            }
        }

        return ColorType.Red;
    }

    private static GameDataSubMessage BuildPlayerInfoSpawnSub(HostPlayerSpawnRecord rec) =>
        GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.PlayerInfo,
            OwnerClientId: -4,
            Flags: 0,
            Components:
            [
                new SpawnComponentEntry(
                    rec.PlayerInfoNetId,
                    ComponentDataCodec.SerializePlayerInfoInitial(
                        rec.PlayerId,
                        rec.ClientId,
                        rec.Name,
                        rec.Color,
                        rec.Level,
                        rec.FriendCode,
                        rec.Puid)),
            ]));

    private GameDataSubMessage BuildPlayerControlSpawnSub(HostPlayerSpawnRecord rec, bool isNew)
    {
        var pos = new Vector2(0f, 1.5f);
        ushort seq = 1;
        if (World.PlayersByClientId.TryGetValue(rec.ClientId, out var existingState) &&
            existingState.LastTransformSequenceId > 0)
        {
            pos = existingState.Position;
            seq = existingState.LastTransformSequenceId;
        }

        using var xformW = BeanBot.Protocol.Hazel.MessageWriter.Get(BeanBot.Protocol.Hazel.MessageType.Reliable);
        new CustomNetworkTransformUpdate(seq, [pos]).Serialize(xformW, initialState: true);
        return GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.PlayerControl,
            OwnerClientId: rec.ClientId,
            Flags: 1,
            Components:
            [
                new SpawnComponentEntry(rec.ControlNetId, ComponentDataCodec.SerializePlayerControlInitial(rec.PlayerId, isNew)),
                new SpawnComponentEntry(rec.PhysicsNetId, Array.Empty<byte>()),
                new SpawnComponentEntry(rec.TransformNetId, xformW.ToByteArray(includeHeader: false)),
            ]));
    }

    private GameDataSubMessage[] BuildGlobalLobbySpawnSubs() =>
    [
        GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.VoteBanSystem,
            OwnerClientId: -2,
            Flags: 0,
            Components: [new SpawnComponentEntry(_hostVoteBanNetId, [0x00])])),
        GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.NormalGameManager,
            OwnerClientId: -2,
            Flags: 0,
            Components: [new SpawnComponentEntry(_normalGameManagerNetId ?? 2u, ComponentDataCodec.SerializeNormalGameManagerInitial(_hostGameOptions))])),
        GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.LobbyBehaviour,
            OwnerClientId: -2,
            Flags: 0,
            Components: [new SpawnComponentEntry(_hostLobbyBehaviourNetId, Array.Empty<byte>())])),
    ];

    private byte NextCosmeticSequenceId()
    {
        _localCosmeticSeq++;
        if (_localCosmeticSeq == 0)
        {
            _localCosmeticSeq = 1;
        }

        return _localCosmeticSeq;
    }

    private GameDataSubMessage[] BuildLocalLevelAndCosmeticRpcSubs(uint controlNetId, uint level = 1u)
    {
        var seq = NextCosmeticSequenceId();
        return
        [
            GameDataSubMessage.FromRpc(controlNetId, new SetLevelRpc(level > 0 ? level : 1u)),
            GameDataSubMessage.FromRpc(controlNetId, SetCosmeticStrRpc.Hat(nextSeq: seq)),
            GameDataSubMessage.FromRpc(controlNetId, SetCosmeticStrRpc.Skin(nextSeq: seq)),
            GameDataSubMessage.FromRpc(controlNetId, SetCosmeticStrRpc.Pet(nextSeq: seq)),
            GameDataSubMessage.FromRpc(controlNetId, SetCosmeticStrRpc.Visor(nextSeq: seq)),
            GameDataSubMessage.FromRpc(controlNetId, SetCosmeticStrRpc.NamePlate(nextSeq: seq)),
        ];
    }

    private async Task InitializeHostLobbyObjectsAsync(PlayerState hostPlayer, CancellationToken ct)
    {
        _hostLobbySpawned = true;
        _hostPendingMakePublic = MakePublicWhenHosting;
        _hostVoteBanNetId = AllocateHostNetId();
        _normalGameManagerNetId = AllocateHostNetId();
        _hostLobbyBehaviourNetId = AllocateHostNetId();

        var relayOwnsPlayerInfo = _profile.Mode == ServerProfileMode.Live;
        var hostColor = PickFreeHostColor(PreferredColorName ?? hostPlayer.ColorName);
        var hostName = !string.IsNullOrWhiteSpace(hostPlayer.Name) ? hostPlayer.Name : (_activeAccount?.DisplayName ?? "BeanBot");
        var meta = _playerMetaByClientId.TryGetValue(hostPlayer.ClientId, out var m) ? m : (1u, string.Empty);
        var hostPlayerInfoNetId = relayOwnsPlayerInfo
            ? (_playerInfoNetIdByClientId.TryGetValue(hostPlayer.ClientId, out var pidNet) && pidNet != 0 ? pidNet : 0u)
            : AllocateHostPlayerInfoNetId();

        var rec = new HostPlayerSpawnRecord(
            ClientId: hostPlayer.ClientId,
            PlayerId: hostPlayer.PlayerId < 15 ? hostPlayer.PlayerId : (byte)0,
            PlayerInfoNetId: hostPlayerInfoNetId,
            ControlNetId: AllocateHostNetId(),
            PhysicsNetId: AllocateHostNetId(),
            TransformNetId: AllocateHostNetId(),
            Name: hostName,
            Color: hostColor,
            Level: meta.Item1 > 0 ? meta.Item1 : 1u,
            FriendCode: meta.Item2,
            Puid: hostPlayer.Puid);

        _hostPlayerSpawns[hostPlayer.ClientId] = rec;
        RegisterHostPlayerMaps(rec, hostPlayer);

        var spawns = BuildGlobalLobbySpawnSubs().ToList();
        if (!relayOwnsPlayerInfo)
        {
            spawns.Add(BuildPlayerInfoSpawnSub(rec));
        }

        spawns.Add(BuildPlayerControlSpawnSub(rec, isNew: false));
        spawns.Add(GameDataSubMessage.FromRpc(rec.ControlNetId, new SetNameRpc(rec.ControlNetId, rec.Name)));
        spawns.Add(GameDataSubMessage.FromRpc(
            rec.ControlNetId,
            new SetColorRpc(hostPlayerInfoNetId != 0 ? hostPlayerInfoNetId : rec.ControlNetId, rec.Color)));
        spawns.AddRange(BuildLocalLevelAndCosmeticRpcSubs(rec.ControlNetId, rec.Level));
        await SendGameDataAsync(spawns, reliable: true, ct).ConfigureAwait(false);
        LogOutbound("HostSpawnLobby", detail: $"hostNetId={rec.ControlNetId}");

        if (MakePublicWhenHosting && (_profile.Mode != ServerProfileMode.Live || _hostSawInitialPrivateAlterGame))
        {
            _hostPendingMakePublic = false;
            await SendRootMessageAsync(new AlterGameMessage(_currentGameCode, AlterGameTags.ChangePrivacy, IsPublic: true), reliable: true, ct).ConfigureAwait(false);
            World.IsPublicLobby = true;
        }
    }

    private async Task SpawnJoinerForHostAsync(PlayerState joiner, CancellationToken ct)
    {
        if (_hostPlayerSpawns.ContainsKey(joiner.ClientId))
        {
            return;
        }

        var relayOwnsPlayerInfo = _profile.Mode == ServerProfileMode.Live;
        var playerId = joiner.PlayerId < 15 && !_hostPlayerSpawns.Values.Any(p => p.PlayerId == joiner.PlayerId)
            ? joiner.PlayerId
            : AllocateFreePlayerId();
        var color = PickFreeHostColor(joiner.ColorName);
        var name = !string.IsNullOrWhiteSpace(joiner.Name) ? joiner.Name : $"Player{joiner.ClientId}";
        var meta = _playerMetaByClientId.TryGetValue(joiner.ClientId, out var m) ? m : (1u, string.Empty);

        var newRec = new HostPlayerSpawnRecord(
            ClientId: joiner.ClientId,
            PlayerId: playerId,
            PlayerInfoNetId: relayOwnsPlayerInfo ? 0u : AllocateHostPlayerInfoNetId(),
            ControlNetId: AllocateHostNetId(),
            PhysicsNetId: AllocateHostNetId(),
            TransformNetId: AllocateHostNetId(),
            Name: name,
            Color: color,
            Level: meta.Item1,
            FriendCode: meta.Item2,
            Puid: joiner.Puid);

        // Send existing lobby objects + existing players directly to the joining client.
        var toJoiner = new List<GameDataSubMessage>();
        if (!relayOwnsPlayerInfo)
        {
            foreach (var existing in _hostPlayerSpawns.Values)
            {
                toJoiner.Add(BuildPlayerInfoSpawnSub(existing));
            }
        }

        toJoiner.AddRange(BuildGlobalLobbySpawnSubs());
        foreach (var existing in _hostPlayerSpawns.Values)
        {
            toJoiner.Add(BuildPlayerControlSpawnSub(existing, isNew: false));
        }

        await SendGameDataToAsync(joiner.ClientId, toJoiner, reliable: true, ct).ConfigureAwait(false);

        // Broadcast new player's PlayerControl (and PlayerInfo on non-Live transports) to everyone.
        _hostPlayerSpawns[joiner.ClientId] = newRec;
        RegisterHostPlayerMaps(newRec, joiner);

        var hostPcNetId = _hostPlayerSpawns.TryGetValue(World.LocalClientId, out var hRec) ? hRec.ControlNetId : newRec.ControlNetId;
        var broadcastSubs = new List<GameDataSubMessage>();
        if (!relayOwnsPlayerInfo)
        {
            broadcastSubs.Add(BuildPlayerInfoSpawnSub(newRec));
        }

        broadcastSubs.Add(GameDataSubMessage.FromRpc(hostPcNetId, new SetStartCounterRpc(++_hostStartCounterSeq, -1)));
        broadcastSubs.Add(BuildPlayerControlSpawnSub(newRec, isNew: true));
        await SendGameDataAsync(broadcastSubs, reliable: true, ct).ConfigureAwait(false);
        LogOutbound("HostSpawnPlayer", detail: $"clientId={joiner.ClientId},playerId={playerId}");
    }

    private void RegisterHostPlayerMaps(HostPlayerSpawnRecord rec, PlayerState state)
    {
        state.PlayerId = rec.PlayerId;
        state.NetId = rec.ControlNetId;
        state.TransformNetId = rec.TransformNetId;
        state.Name = rec.Name;
        state.ColorName = rec.Color.ToString();
        if (rec.ClientId == World.LocalClientId)
        {
            World.LocalPlayerId = rec.PlayerId;
        }

        if (rec.PlayerInfoNetId != 0)
        {
            _netIdToClientId[rec.PlayerInfoNetId] = rec.ClientId;
            _playerInfoNetIdByClientId[rec.ClientId] = rec.PlayerInfoNetId;
        }

        _netIdToClientId[rec.ControlNetId] = rec.ClientId;
        _netIdToClientId[rec.PhysicsNetId] = rec.ClientId;
        _netIdToClientId[rec.TransformNetId] = rec.ClientId;
        _transformNetIdToClientId[rec.TransformNetId] = rec.ClientId;
        var spawnPos = new Vector2(0f, 1.5f);
        if (World.Phase != GamePhase.Lobby)
        {
            (spawnPos, _, _) = _mesh.SnapToNavMesh(spawnPos, _logger);
        }

        World.UpdatePlayerPosition(rec.ClientId, spawnPos, 1);
    }

    private async Task ReplyCheckNameAsHostAsync(PlayerState player, uint controlNetId, string requestedName, CancellationToken ct)
    {
        var clean = string.IsNullOrWhiteSpace(requestedName) ? $"Player{player.ClientId}" : requestedName.Trim();
        var others = World.PlayersByClientId.Values.Where(p => p.ClientId != player.ClientId).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = clean;
        var suffix = 1;
        while (others.Contains(unique))
        {
            unique = $"{clean} {suffix++}";
        }

        player.Name = unique;
        if (_hostPlayerSpawns.TryGetValue(player.ClientId, out var rec))
        {
            _hostPlayerSpawns[player.ClientId] = rec with { Name = unique };
        }

        await SendRpcAsync(controlNetId, new SetNameRpc(controlNetId, unique), ct).ConfigureAwait(false);
    }

    private async Task ReplyCheckColorAsHostAsync(PlayerState player, uint controlNetId, ColorType requestedColor, CancellationToken ct)
    {
        var used = World.PlayersByClientId.Values
            .Where(p => p.ClientId != player.ClientId && ColorPalette.TryParse(p.ColorName, out _))
            .Select(p => ColorPalette.TryParse(p.ColorName, out var c) ? c : ColorType.Red)
            .ToHashSet();

        var chosen = !used.Contains(requestedColor)
            ? requestedColor
            : ColorPalette.AppendixAOrder.FirstOrDefault(c => !used.Contains(c));

        player.ColorName = chosen.ToString();
        if (_hostPlayerSpawns.TryGetValue(player.ClientId, out var rec))
        {
            _hostPlayerSpawns[player.ClientId] = rec with { Color = chosen };
        }

        var piNetId = _playerInfoNetIdByClientId.TryGetValue(player.ClientId, out var pidNet) && pidNet != 0
            ? pidNet
            : (_profile.Mode == ServerProfileMode.Live ? 0u : controlNetId);
        await SendRpcAsync(controlNetId, new SetColorRpc(piNetId, chosen), ct).ConfigureAwait(false);
    }

    private void LogOutbound(string actionName, string? detail = null)
    {
        _replay?.LogAction(
            botId: _activeAccount?.AccountId ?? "bot-00",
            action: actionName,
            causePacketId: _lastInboundCauseId,
            stateHash: World.ComputeStateHash(),
            detail: detail,
            causePacketNameOverride: _lastInboundCauseName);
    }

    private PlayerState EnsurePlayerEntry(int clientId, string name, string puid, bool isHost)
    {
        if (!World.PlayersByClientId.TryGetValue(clientId, out var player))
        {
            player = new PlayerState
            {
                ClientId = clientId,
                Name = name,
                Puid = puid,
                IsHost = isHost,
            };
            World.PlayersByClientId[clientId] = player;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(name)) player.Name = name;
            if (!string.IsNullOrWhiteSpace(puid)) player.Puid = puid;
            player.IsHost = isHost;
        }

        return player;
    }

    private void ApplyNormalGameOptions(NormalGameOptions normal)
    {
        var killDist = normal.KillDistance switch
        {
            KillDistances.Short => 1.0f,
            KillDistances.Normal => 1.5f,
            KillDistances.Long => 2.5f,
            _ => 1.5f,
        };

        World.Settings = new SyncSettingsModel
        {
            PlayerSpeed = normal.PlayerSpeedMod,
            KillCooldownSeconds = normal.KillCooldown,
            KillDistanceUnits = killDist,
            VentCooldownSeconds = 15.0f,
            MapName = normal.Map.ToString(),
            MaxPlayers = normal.MaxPlayers,
            NumImpostors = normal.NumImpostors,
            DiscussionTimeSeconds = normal.DiscussionTime,
            VotingTimeSeconds = normal.VotingTime,
            ConfirmEjects = normal.ConfirmImpostor,
            VisualTasks = normal.VisualTasks,
            AnonymousVotes = normal.AnonymousVotes,
        };
    }

    private static PlayerRole MapRoleType(RoleTypes role, bool isAlive) => role switch
    {
        RoleTypes.Impostor or RoleTypes.Shapeshifter or RoleTypes.Phantom or RoleTypes.Viper =>
            isAlive ? PlayerRole.Impostor : PlayerRole.ImpostorGhost,
        RoleTypes.ImpostorGhost => PlayerRole.ImpostorGhost,
        RoleTypes.CrewmateGhost or RoleTypes.GuardianAngel => PlayerRole.CrewmateGhost,
        _ => isAlive ? PlayerRole.Crewmate : PlayerRole.CrewmateGhost,
    };

    private async Task<IPEndPoint> LookupRoomWithRegionFallbackAsync(
        AccountRecord account,
        AuthSessionResult authResult,
        GameCode code,
        CancellationToken ct)
    {
        var initialLookup = await JoinByCodeHonoringRateLimitAsync(_activeProfile, _matchmakerToken, code, ct).ConfigureAwait(false);
        if (initialLookup.Status == JoinLookupStatus.Ok && initialLookup.RelayEndPoint is not null)
        {
            return initialLookup.RelayEndPoint;
        }

        if (initialLookup.Status == JoinLookupStatus.Full)
        {
            throw new RoomLookupException(JoinLookupStatus.Full, $"Room '{code.Code}' is full (code 2).");
        }

        if (initialLookup.Status == JoinLookupStatus.RateLimited)
        {
            throw CreateRateLimitedException(code, initialLookup.ErrorMessage);
        }

        if (initialLookup.Status != JoinLookupStatus.NotFound)
        {
            throw new RoomLookupException(initialLookup.Status, $"Join lookup failed for '{code.Code}': {initialLookup.ErrorMessage}");
        }

        var checkedRegions = new List<string> { _activeProfile.Region };
        if (_activeProfile.Mode == ServerProfileMode.Live)
        {
            foreach (var region in ServerProfile.LiveRegions)
            {
                if (string.Equals(region, _activeProfile.Region, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                checkedRegions.Add(region);
                var candidateProfile = ServerProfile.Live(region, _activeProfile.CertsDirectory, _activeProfile.AcceptNewPin);
                var (candidateToken, candidateUsedCache) = await ObtainMatchmakerTokenAsync(
                    candidateProfile, account, authResult, forceRefresh: false, ct).ConfigureAwait(false);
                JoinLookupResult candidateLookup;
                try
                {
                    candidateLookup = await _matchmaker.JoinByCodeAsync(
                        candidateProfile, candidateToken, code, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (candidateUsedCache && IsMatchmakerTokenRejected(ex))
                {
                    InvalidateCachedMatchmakerToken(account);
                    (candidateToken, _) = await ObtainMatchmakerTokenAsync(
                        candidateProfile, account, authResult, forceRefresh: true, ct).ConfigureAwait(false);
                    candidateLookup = await _matchmaker.JoinByCodeAsync(
                        candidateProfile, candidateToken, code, ct).ConfigureAwait(false);
                }

                if (candidateLookup.Status == JoinLookupStatus.Ok && candidateLookup.RelayEndPoint is not null)
                {
                    _activeProfile = candidateProfile;
                    _matchmakerToken = candidateToken;
                    _logger?.LogInformation("Resolved room '{RoomCode}' on '{Region}' matchmaker.", code.Code, candidateProfile.Region);
                    return candidateLookup.RelayEndPoint;
                }

                if (candidateLookup.Status == JoinLookupStatus.Full)
                {
                    throw new RoomLookupException(JoinLookupStatus.Full, $"Room '{code.Code}' is full on region '{region}' (code 2).");
                }

                if (candidateLookup.Status == JoinLookupStatus.RateLimited)
                {
                    // Stop sweeping regions: every extra lookup extends the matchmaker's rate-limit window.
                    throw CreateRateLimitedException(code, candidateLookup.ErrorMessage);
                }
            }
        }

        throw new RoomLookupException(
            JoinLookupStatus.NotFound,
            $"Room '{code.Code}' not found (checked region(s): {string.Join(", ", checkedRegions)}; code 3).");
    }

    /// <summary>
    /// Delays used when the matchmaker answers TooManyRequests (116) / HTTP 429. One polite retry per delay,
    /// then the join fails with a clear message instead of hammering the matchmaker.
    /// </summary>
    public IReadOnlyList<TimeSpan> RateLimitRetryDelays { get; set; } =
        new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };

    public Func<TimeSpan, CancellationToken, Task> RateLimitDelayAsync { get; set; } = Task.Delay;

    private async Task<JoinLookupResult> JoinByCodeHonoringRateLimitAsync(
        ServerProfile profile,
        string token,
        GameCode code,
        CancellationToken ct)
    {
        var lookup = await _matchmaker.JoinByCodeAsync(profile, token, code, ct).ConfigureAwait(false);
        foreach (var delay in RateLimitRetryDelays)
        {
            if (lookup.Status != JoinLookupStatus.RateLimited)
            {
                break;
            }

            _logger?.LogWarning(
                "Matchmaker rate-limited lookup for '{RoomCode}' ({Detail}); waiting {DelaySec:F0}s before retrying.",
                code.Code, lookup.ErrorMessage, delay.TotalSeconds);
            await RateLimitDelayAsync(delay, ct).ConfigureAwait(false);
            lookup = await _matchmaker.JoinByCodeAsync(profile, token, code, ct).ConfigureAwait(false);
        }

        return lookup;
    }

    private static RoomLookupException CreateRateLimitedException(GameCode code, string? detail) =>
        new(
            JoinLookupStatus.RateLimited,
            $"Among Us matchmaker is rate-limiting this connection ({detail ?? "TooManyRequests (116)"}) while looking up '{code.Code}'. " +
            "Wait a few minutes before trying again, and use fewer bots or fewer rapid retries.");

    private async Task<(string Token, bool UsedCache)> ObtainMatchmakerTokenAsync(
        ServerProfile profile,
        AccountRecord account,
        AuthSessionResult authResult,
        bool forceRefresh,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(account.CachedMatchmakerToken) &&
            string.Equals(account.CachedMatchmakerBaseUrl, profile.MatchmakerBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            account.CachedMatchmakerTokenExpiresAt is { } expiresAt &&
            expiresAt > now.AddMinutes(1))
        {
            _lastMatchmakerTokenWasCached = true;
            return (account.CachedMatchmakerToken, true);
        }

        var token = await _matchmaker.GetMatchmakerTokenAsync(
            profile,
            authResult.Puid,
            authResult.DisplayName,
            Language.English,
            profile.UseDeviceAuth ? (authResult.IdToken ?? authResult.AccessToken) : null,
            ct).ConfigureAwait(false);

        account.CachedMatchmakerToken = token;
        account.CachedMatchmakerBaseUrl = profile.MatchmakerBaseUrl;
        account.CachedMatchmakerTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        _vault.SaveRecord(account);
        _lastMatchmakerTokenWasCached = false;
        return (token, false);
    }

    private void InvalidateCachedMatchmakerToken(AccountRecord account)
    {
        account.CachedMatchmakerToken = null;
        account.CachedMatchmakerTokenExpiresAt = null;
        _lastMatchmakerTokenWasCached = false;
        _vault.SaveRecord(account);
    }

    private static bool IsMatchmakerTokenRejected(Exception ex) =>
        (ex is HttpRequestException httpEx && httpEx.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ||
        (ex is InvalidOperationException or IOException && ex.Message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
