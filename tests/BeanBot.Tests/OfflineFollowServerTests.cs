using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Orchestrator;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.Maps;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using Xunit;
using Xunit.Abstractions;

namespace BeanBot.Tests;

/// <summary>
/// Full offline Among Us server (HTTP matchmaker + UDP InnerNetServer + GameAssembly.dll-exact Host CustomNetworkTransform state machine)
/// to verify end-to-end bot spawning, movement, task walking, and live following as "You" walks across Lobby and Skeld.
/// </summary>
public sealed class OfflineFollowServerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OfflineServer_BotsMoveInTasks_AndFollowMovingHostAcrossRooms_InRealTime()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-offline-{Guid.NewGuid():N}");
        var replayDir = Path.Combine(dir, "replays");
        var locksDir = Path.Combine(dir, "locks");
        Directory.CreateDirectory(replayDir);
        Directory.CreateDirectory(locksDir);

        try
        {
            const int botCount = 5;
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "offline-test-pass");
            var roster = Roster.CreateJoiners(botCount);
            roster.EnsurePersistedAccounts(vault);

            await using var server = new FaithfulOfflineAmongUsServer(hostName: "Beesmart", roomCode: "OFFLNE");
            server.Start();

            using var http = new HttpClient();
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http, maxRetries: 2, delayAsync: (_, _) => Task.CompletedTask);
            var profile = ServerProfile.Local("127.0.0.1", server.HttpPort);
            var timing = OrchestratorTimingConfig.FastLocal with
            {
                RetryMinDelay = TimeSpan.FromMilliseconds(20),
                RetryMaxDelay = TimeSpan.FromMilliseconds(50),
                JoinSpacing = TimeSpan.FromMilliseconds(30),
            };

            await using var fleet = new Fleet(
                profile,
                vault,
                auth,
                matchmaker,
                roster: roster,
                timing: timing,
                replayDirectory: replayDir,
                locksDirectory: locksDir);

            var joinedCode = await fleet.StartJoinLobbyAsync("OFFLNE");
            Assert.Equal("OFFLNE", joinedCode);

            // Wait for all bots to join and see both themselves and host "Beesmart".
            await SpinWaitUntilAsync(() =>
                GetConnectedBots(fleet).Count == botCount &&
                GetConnectedBots(fleet).All(b =>
                    b.World.LocalPlayer is not null &&
                    b.World.PlayersByClientId.Values.Any(p => p.Name == "Beesmart")),
                timeoutMs: 8_000,
                describeFailure: () => $"connected={GetConnectedBots(fleet).Count}");

            output.WriteLine($"[Test] All {botCount} bots joined lobby OFFLNE and see host Beesmart.");

            var surface = new CommandSurface(fleet, vault);

            // 1. Test FOLLOW in Lobby while Host walks around Lobby!
            var lobbyFollowRes = await surface.ExecuteAsync("follow all Beesmart");
            Assert.True(lobbyFollowRes.Ok, lobbyFollowRes.Message);
            output.WriteLine($"[Test] Lobby follow command: {lobbyFollowRes.Message}");

            var hostLobbyWalk = new[]
            {
                new Vector2(0.0f, 0.5f),
                new Vector2(1.5f, 0.5f),
                new Vector2(2.6f, -0.5f),
                new Vector2(2.6f, -0.5f),
            };
            await WalkHostAlongAsync(server, hostLobbyWalk, stepIntervalMs: 250, settleMs: 1800);

            var lobbySnap = server.SnapshotHostView();
            foreach (var (cid, state) in lobbySnap.BotStates)
            {
                var dist = Vector2.Distance(state.Position, lobbySnap.HostPosition);
                output.WriteLine($"[LobbyAfterFollow] bot clientId={cid} ({state.Name}) hostViewPos=({state.Position.X:F2},{state.Position.Y:F2}) distToHost={dist:F2} accepted={state.AcceptedUpdates} rejected={state.RejectedUpdates}");
                Assert.True(state.AcceptedUpdates > 0, $"Host accepted 0 TransformUpdates in Lobby from {state.Name}!");
                Assert.True(dist < 3.0f, $"In Lobby, {state.Name} did not follow Beesmart closely (dist={dist:F2}, pos={state.Position}, host={lobbySnap.HostPosition}).");
            }

            // Dump raw TransformUpdate packet bytes seen by server for verification.
            Assert.NotEmpty(server.CapturedTransformHexSamples);
            output.WriteLine($"[RawHexSample] First TransformUpdate sub-message: {server.CapturedTransformHexSamples[0]}");

            // 2. Clear follow, start match cleanly (replicating Among Us 2026.7.21 ShipStatus.SpawnPlayer + SnapTo),
            //    verify bots walk to tasks after intro settle, then issue follow all Beesmart and walk across Skeld!
            var clearRes = await surface.ExecuteAsync("follow all off");
            Assert.True(clearRes.Ok, clearRes.Message);

            await server.StartMatchOnSkeldAsync();
            output.WriteLine("[Test] Host started match on Skeld.");

            // Wait for intro settle (~4.5s in Tasks phase) + task walking (~2.0s).
            await SpinWaitUntilAsync(() =>
                server.SnapshotHostView().BotStates.Values.All(s => s.AcceptedUpdatesAfterStart >= 5),
                timeoutMs: 10_000,
                describeFailure: () =>
                {
                    var snap = server.SnapshotHostView();
                    return string.Join("; ", snap.BotStates.Values.Select(b => $"{b.Name}:afterStart={b.AcceptedUpdatesAfterStart},rej={b.RejectedUpdates},pos=({b.Position.X:F2},{b.Position.Y:F2})"));
                });

            var taskSnap = server.SnapshotHostView();
            foreach (var (_, state) in taskSnap.BotStates)
            {
                output.WriteLine($"[TasksPhase] {state.Name} hostViewPos=({state.Position.X:F2},{state.Position.Y:F2}) acceptedAfterStart={state.AcceptedUpdatesAfterStart} rejected={state.RejectedUpdates}");
                Assert.Equal(0, state.RejectedUpdates);
            }

            // Issue follow command during Tasks phase (matching user flow).
            var matchFollowRes = await surface.ExecuteAsync("follow all \"Beesmart\"");
            Assert.True(matchFollowRes.Ok, matchFollowRes.Message);
            output.WriteLine($"[Test] Tasks follow command: {matchFollowRes.Message}");

            // Host walks Cafeteria -> Weapons -> O2 corridor -> Admin -> Storage corridor
            var skeldWalk = new[]
            {
                new Vector2(0.0f, 2.2f),
                new Vector2(3.5f, 1.8f),
                new Vector2(7.0f, 1.8f),
                new Vector2(9.0f, 1.8f),   // Weapons
                new Vector2(9.0f, -1.5f),
                new Vector2(6.5f, -3.5f),  // O2 / Nav junction
                new Vector2(2.5f, -6.2f),  // Admin entrance
                new Vector2(0.5f, -8.5f),  // Cafeteria-Storage hallway
            };
            await WalkHostAlongAsync(server, skeldWalk, stepIntervalMs: 650, settleMs: 4500);

            var finalSnap = server.SnapshotHostView();
            foreach (var (_, state) in finalSnap.BotStates)
            {
                var dist = Vector2.Distance(state.Position, finalSnap.HostPosition);
                output.WriteLine($"[MatchAfterWalk] {state.Name} hostViewPos=({state.Position.X:F2},{state.Position.Y:F2}) vs Host=({finalSnap.HostPosition.X:F2},{finalSnap.HostPosition.Y:F2}) dist={dist:F2} acceptedAfterStart={state.AcceptedUpdatesAfterStart} rejected={state.RejectedUpdates}");
                Assert.True(dist < 3.5f, $"In Tasks phase, {state.Name} failed to follow Beesmart to ({finalSnap.HostPosition.X:F2},{finalSnap.HostPosition.Y:F2}); host sees {state.Name} at ({state.Position.X:F2},{state.Position.Y:F2}) (dist={dist:F2}).");
            }

            // Also verify BotClient World view matches host position accurately.
            foreach (var bot in GetConnectedBots(fleet))
            {
                var hostInBotWorld = bot.World.PlayersByClientId.Values.First(p => p.Name == "Beesmart");
                var distHostSync = Vector2.Distance(hostInBotWorld.Position, finalSnap.HostPosition);
                Assert.True(distHostSync < 0.5f, $"{bot.World.LocalPlayer?.Name} thought Beesmart was at {hostInBotWorld.Position} instead of {finalSnap.HostPosition}");
            }

            await fleet.StopAllAsync();
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    private static IReadOnlyList<BotClient> GetConnectedBots(Fleet fleet) =>
        fleet.GetBots().Where(b => b.IsConnected).ToArray();

    private static async Task WalkHostAlongAsync(FaithfulOfflineAmongUsServer server, Vector2[] waypoints, int stepIntervalMs, int settleMs)
    {
        for (var i = 0; i < waypoints.Length; i++)
        {
            var from = server.HostPosition;
            var to = waypoints[i];
            const int substeps = 4;
            for (var s = 1; s <= substeps; s++)
            {
                var p = Vector2.Lerp(from, to, s / (float)substeps);
                await server.MoveHostAsync(p);
                await Task.Delay(stepIntervalMs / substeps);
            }
        }

        await Task.Delay(settleMs);
    }

    private static async Task SpinWaitUntilAsync(Func<bool> predicate, int timeoutMs, Func<string> describeFailure)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                throw new TimeoutException($"Timed out after {timeoutMs}ms: {describeFailure()}");
            }

            await Task.Delay(50);
        }
    }

    private sealed class FaithfulOfflineAmongUsServer(string hostName, string roomCode) : IAsyncDisposable
    {
        public sealed class BotTransformView
        {
            public required int ClientId { get; init; }
            public required string Name { get; set; }
            public required uint PlayerControlNetId { get; init; }
            public required uint PhysicsNetId { get; init; }
            public required uint TransformNetId { get; init; }
            public ushort LastSequenceId { get; set; }
            public bool IsPaused { get; set; }
            public Queue<Vector2> IncomingPosQueue { get; } = new();
            public Vector2 Position { get; set; }
            public Vector2 LastPosition { get; set; }
            public Vector2 Velocity { get; set; }
            public float IdealSpeed { get; set; }
            public float RubberbandModifier { get; set; } = 1.0f;
            public int AcceptedUpdates { get; set; }
            public int AcceptedUpdatesAfterStart { get; set; }
            public int RejectedUpdates { get; set; }
        }

        public sealed record HostSnapshot(Vector2 HostPosition, IReadOnlyDictionary<int, BotTransformView> BotStates);

        private sealed class ClientPeer
        {
            public required IPEndPoint EndPoint { get; init; }
            public int ClientId { get; init; }
            public byte PlayerId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Puid { get; set; } = string.Empty;
            public ushort ServerReliableSeq { get; set; } = 1;
            public bool SceneSpawned { get; set; }
            public uint PlayerControlNetId { get; set; }
            public uint PhysicsNetId { get; set; }
            public uint TransformNetId { get; set; }
            public uint PlayerInfoNetId { get; set; }
        }

        private readonly ProtocolRegistry _registry = ProtocolRegistry.Default;
        private readonly TcpListener _httpListener = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = CreateUdp();
        private readonly ConcurrentDictionary<string, ClientPeer> _peersByEp = new();
        private readonly ConcurrentDictionary<uint, BotTransformView> _botsByTransformNetId = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();

        private Task? _httpTask;
        private Task? _udpTask;
        private Task? _fixedUpdateTask;
        private int _nextClientId = 2; // Host is ClientId = 1
        private byte _nextPlayerId = 1; // Host is PlayerId = 0
        private uint _nextNetId = 10;
        private uint _nextInfoNetId = 1000;
        private bool _matchStarted;

        // Host's own InnerNet state (ClientId=1, NetId=1, Physics=2, Transform=3, Info=1001, Gm=900)
        private const int HostClientId = 1;
        private const uint HostPcNetId = 1;
        private const uint HostPhysicsNetId = 2;
        private const uint HostTransformNetId = 3;
        private const uint HostInfoNetId = 1001;
        private const uint GameManagerNetId = 900;
        private const uint SkeldShipNetId = 950;
        private ushort _hostTransformSeq = 1;

        public int HttpPort => ((IPEndPoint)_httpListener.LocalEndpoint).Port;
        public int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public Vector2 HostPosition { get; private set; } = new(-0.5f, 0.5f);
        public List<string> CapturedTransformHexSamples { get; } = new();

        private static UdpClient CreateUdp()
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnReset = -1744830452;
                udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            return udp;
        }

        public void Start()
        {
            _httpListener.Start();
            _httpTask = Task.Run(RunHttpAsync);
            _udpTask = Task.Run(RunUdpAsync);
            _fixedUpdateTask = Task.Run(RunFixedUpdateLoopAsync);
        }

        public HostSnapshot SnapshotHostView()
        {
            lock (_gate)
            {
                var copy = _botsByTransformNetId.Values.ToDictionary(
                    v => v.ClientId,
                    v => new BotTransformView
                    {
                        ClientId = v.ClientId,
                        Name = v.Name,
                        PlayerControlNetId = v.PlayerControlNetId,
                        PhysicsNetId = v.PhysicsNetId,
                        TransformNetId = v.TransformNetId,
                        LastSequenceId = v.LastSequenceId,
                        IsPaused = v.IsPaused,
                        Position = v.Position,
                        AcceptedUpdates = v.AcceptedUpdates,
                        AcceptedUpdatesAfterStart = v.AcceptedUpdatesAfterStart,
                        RejectedUpdates = v.RejectedUpdates,
                    });
                return new HostSnapshot(HostPosition, copy);
            }
        }

        public async Task MoveHostAsync(Vector2 pos)
        {
            HostPosition = pos;
            ushort seq;
            ClientPeer[] peers;
            lock (_gate)
            {
                seq = ++_hostTransformSeq;
                peers = _peersByEp.Values.Where(p => p.SceneSpawned).ToArray();
            }

            var sub = GameDataSubMessage.FromTransform(
                HostTransformNetId,
                new CustomNetworkTransformUpdate(seq, new[] { pos }));
            var root = new GameDataMessage(GameCode.From(roomCode), GameDataSubMessage.SerializeAll(new[] { sub }));
            foreach (var peer in peers)
            {
                await SendRootAsync(peer, root, reliable: false);
            }
        }

        public async Task StartMatchOnSkeldAsync()
        {
            ClientPeer[] peers;
            lock (_gate)
            {
                _matchStarted = true;
                peers = _peersByEp.Values.ToArray();
            }

            // 1. StartGame root message
            foreach (var peer in peers)
            {
                await SendRootAsync(peer, new StartGameMessage(GameCode.From(roomCode)), reliable: true);
            }

            // 2. Replicate GameAssembly.dll ShipStatus.SpawnPlayer(3) BEFORE sending SkeldShipStatus so no bot tick
            //    can arrive before the host advances lastSequenceId += 2:
            //    - Host is AmOwner for Host: sends RpcSnapTo with minSid = lastSequenceId + 2
            //    - Non-owner bots on Host: Host calls local CustomNetworkTransform.SnapTo(spawnPos) which sets lastSequenceId += 2 WITHOUT RpcSnapTo!
            HostPosition = new Vector2(-0.10f, 2.28f);
            ushort snapSeq;
            lock (_gate)
            {
                _hostTransformSeq += 2;
                snapSeq = _hostTransformSeq;
                foreach (var botView in _botsByTransformNetId.Values)
                {
                    // ShipStatus.SpawnPlayer -> SnapTo(pos) increments [rbx+62h] += 2, clears incomingPosQueue, and zeroes velocity
                    botView.LastSequenceId += 2;
                    botView.IncomingPosQueue.Clear();
                    botView.Position = new Vector2(-0.5f, 2.0f);
                    botView.LastPosition = botView.Position;
                    botView.Velocity = Vector2.Zero;
                    botView.IdealSpeed = 0f;
                    botView.RubberbandModifier = 1f;
                }
            }

            // 3. Host spawns SkeldShipStatus -> Triggers GamePhase.Tasks in BotClient
            var shipSub = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.SkeldShipStatus,
                OwnerClientId: -2,
                Flags: 0,
                Components: new[] { new SpawnComponentEntry(SkeldShipNetId, Array.Empty<byte>()) }));
            foreach (var peer in peers)
            {
                await SendRootAsync(peer, new GameDataMessage(GameCode.From(roomCode), GameDataSubMessage.SerializeAll(new[] { shipSub })), reliable: true);
            }

            var snapSub = GameDataSubMessage.FromRpc(HostTransformNetId, new SnapToRpc(HostPosition, snapSeq));
            var hostRole = GameDataSubMessage.FromRpc(HostPcNetId, new SetRoleRpc(RoleTypes.Crewmate));
            foreach (var peer in peers)
            {
                var roleSub = GameDataSubMessage.FromRpc(peer.PlayerControlNetId, new SetRoleRpc(RoleTypes.Crewmate));
                var taskSub = GameDataSubMessage.FromRpc(peer.PlayerControlNetId, new SetTasksRpc(new byte[] { 0, 1, 2 }));
                await SendRootAsync(peer, new GameDataMessage(GameCode.From(roomCode), GameDataSubMessage.SerializeAll(new[] { snapSub, hostRole, roleSub, taskSub })), reliable: true);
            }
        }

        private async Task RunHttpAsync()
        {
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _httpListener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    await using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
                    {
                        var requestLine = await reader.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(requestLine)) return;
                        var contentLen = 0;
                        while (true)
                        {
                            var hdr = await reader.ReadLineAsync();
                            if (string.IsNullOrEmpty(hdr)) break;
                            if (hdr.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(hdr["Content-Length:".Length..].Trim(), out var len))
                            {
                                contentLen = len;
                            }
                        }

                        if (contentLen > 0)
                        {
                            var chars = new char[contentLen];
                            await reader.ReadBlockAsync(chars, 0, contentLen);
                        }

                        var target = requestLine.Split(' ')[1];
                        string bodyJson;
                        if (target.StartsWith("/api/user", StringComparison.OrdinalIgnoreCase))
                        {
                            bodyJson = JsonSerializer.Serialize(new { token = "offline-tok" });
                        }
                        else if (target.Contains("/api/games", StringComparison.OrdinalIgnoreCase))
                        {
                            bodyJson = JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = UdpPort });
                        }
                        else
                        {
                            bodyJson = "{}";
                        }

                        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
                        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(headerBytes);
                        await stream.WriteAsync(bodyBytes);
                    }
                });
            }
        }

        private async Task RunUdpAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult recv;
                try
                {
                    recv = await _udp.ReceiveAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                var data = recv.Buffer;
                if (data.Length == 0) continue;

                var epKey = recv.RemoteEndPoint.ToString();
                var opt = (UdpSendOption)data[0];

                if (opt == UdpSendOption.Hello && data.Length >= 7)
                {
                    await SendAckAsync(recv.RemoteEndPoint, data[1], data[2]);
                    var name = ExtractHelloName(data);
                    var peer = _peersByEp.GetOrAdd(epKey, _ =>
                    {
                        uint pcId, physId, xformId, infoId;
                        int cid;
                        byte pid;
                        lock (_gate)
                        {
                            cid = _nextClientId++;
                            pid = _nextPlayerId++;
                            pcId = _nextNetId++;
                            physId = _nextNetId++;
                            xformId = _nextNetId++;
                            infoId = _nextInfoNetId++;
                        }

                        var p = new ClientPeer
                        {
                            EndPoint = recv.RemoteEndPoint,
                            ClientId = cid,
                            PlayerId = pid,
                            Name = name,
                            Puid = $"offline-puid-{cid}",
                            PlayerControlNetId = pcId,
                            PhysicsNetId = physId,
                            TransformNetId = xformId,
                            PlayerInfoNetId = infoId,
                        };
                        _botsByTransformNetId[xformId] = new BotTransformView
                        {
                            ClientId = cid,
                            Name = name,
                            PlayerControlNetId = pcId,
                            PhysicsNetId = physId,
                            TransformNetId = xformId,
                            LastSequenceId = 1,
                            Position = Vector2.Zero,
                            LastPosition = Vector2.Zero,
                        };
                        return p;
                    });
                }
                else if (opt == UdpSendOption.Ping && data.Length >= 3)
                {
                    await SendAckAsync(recv.RemoteEndPoint, data[1], data[2]);
                }
                else if (opt == UdpSendOption.Reliable && data.Length >= 3)
                {
                    await SendAckAsync(recv.RemoteEndPoint, data[1], data[2]);
                    if (_peersByEp.TryGetValue(epKey, out var peer))
                    {
                        await HandleRootPayloadAsync(peer, data.AsSpan(3).ToArray());
                    }
                }
                else if (opt == UdpSendOption.Normal && data.Length > 1)
                {
                    if (_peersByEp.TryGetValue(epKey, out var peer))
                    {
                        await HandleRootPayloadAsync(peer, data.AsSpan(1).ToArray());
                    }
                }
            }
        }

        private async Task HandleRootPayloadAsync(ClientPeer sender, byte[] payload)
        {
            using var outer = MessageReader.Get(payload);
            while (outer.BytesRemaining > 0)
            {
                using var rootReader = outer.ReadMessage();
                var root = _registry.DecodeRootMessage(rootReader);
                switch (root)
                {
                    case JoinGameMessage join:
                    {
                        ClientPeer[] others;
                        lock (_gate)
                        {
                            others = _peersByEp.Values.ToArray();
                        }

                        var joinedEntries = new List<JoinedPlayerEntry>
                        {
                            new(HostClientId, hostName, new PlatformSpecificData(Platforms.StandaloneSteamPC, hostName), 1, "host-puid", ""),
                        };
                        foreach (var other in others.Where(o => o.ClientId != sender.ClientId))
                        {
                            joinedEntries.Add(new JoinedPlayerEntry(other.ClientId, other.Name, new PlatformSpecificData(Platforms.Android, other.Name), 1, other.Puid, ""));
                        }

                        await SendRootAsync(sender, new JoinedGameMessage(join.GameCode, sender.ClientId, HostClientId, joinedEntries), reliable: true);
                        break;
                    }

                    case GameDataToMessage gdt:
                    {
                        var subs = GameDataSubMessage.ParseAll(gdt.RawSubMessages);
                        await ProcessAndRelayGameDataAsync(sender, subs, gdt.RawSubMessages, targetClientId: gdt.TargetClientId);
                        break;
                    }

                    case GameDataMessage gd:
                    {
                        var subs = GameDataSubMessage.ParseAll(gd.RawSubMessages);
                        await ProcessAndRelayGameDataAsync(sender, subs, gd.RawSubMessages, targetClientId: null);
                        break;
                    }
                }
            }
        }

        private async Task ProcessAndRelayGameDataAsync(ClientPeer sender, IReadOnlyList<GameDataSubMessage> subs, byte[] rawSubMessages, int? targetClientId)
        {
            foreach (var sub in subs)
            {
                if (sub.Kind == GameDataKind.SceneChange && sub.TryParseSceneChange(out _, out var sceneName) && sceneName == "OnlineGame")
                {
                    ClientPeer[] allPeers;
                    lock (_gate)
                    {
                        sender.SceneSpawned = true;
                        allPeers = _peersByEp.Values.ToArray();
                    }

                    // Send GameManager + Host Spawn + Existing Peers + Joiner's own Spawn (just like Among Us Host)
                    var initialSubs = new List<GameDataSubMessage>
                    {
                        GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                            SpawnObjectId.NormalGameManager,
                            -2,
                            0,
                            new[] { new SpawnComponentEntry(GameManagerNetId, ComponentDataCodec.SerializeGameManagerOptionsDelta(new NormalGameOptions())) })),
                        BuildHostPlayerControlSpawn(),
                        BuildPlayerInfoSpawn(HostClientId, 0, hostName, ColorType.Red, HostInfoNetId, "host-puid"),
                    };
                    foreach (var p in allPeers)
                    {
                        initialSubs.Add(BuildBotPlayerControlSpawn(p));
                        initialSubs.Add(BuildPlayerInfoSpawn(p.ClientId, p.PlayerId, p.Name, (ColorType)(p.PlayerId % 12), p.PlayerInfoNetId, p.Puid));
                    }

                    await SendRootAsync(sender, new GameDataMessage(GameCode.From(roomCode), GameDataSubMessage.SerializeAll(initialSubs)), reliable: true);

                    var notifyExisting = new[]
                    {
                        BuildBotPlayerControlSpawn(sender),
                        BuildPlayerInfoSpawn(sender.ClientId, sender.PlayerId, sender.Name, (ColorType)(sender.PlayerId % 12), sender.PlayerInfoNetId, sender.Puid),
                    };
                    foreach (var other in allPeers.Where(o => o.ClientId != sender.ClientId))
                    {
                        await SendRootAsync(other, new GameDataMessage(GameCode.From(roomCode), GameDataSubMessage.SerializeAll(notifyExisting)), reliable: true);
                    }
                }
                else if (sub.Kind == GameDataKind.Rpc && sub.TryParseRpc(_registry, out var rpcNet, out var rpc))
                {
                    if (rpc is CheckNameRpc cn && rpcNet == sender.PlayerControlNetId)
                    {
                        if (_botsByTransformNetId.TryGetValue(sender.TransformNetId, out var view))
                        {
                            view.Name = cn.Name;
                        }
                        sender.Name = cn.Name;
                    }
                }
                else if (sub.Kind == GameDataKind.Data && sub.TryParseData(out var targetNetId, out var payload))
                {
                    if (CapturedTransformHexSamples.Count < 3)
                    {
                        CapturedTransformHexSamples.Add(Convert.ToHexString(sub.Payload));
                    }

                    ApplyHostCustomNetworkTransformDeserialize(targetNetId, payload);
                }
            }

            // InnerNetServer Broadcast / SendTo other peers (excluding sender, matching InnerNetServer.Broadcast)
            ClientPeer[] recipients;
            lock (_gate)
            {
                recipients = targetClientId.HasValue
                    ? _peersByEp.Values.Where(p => p.ClientId == targetClientId.Value).ToArray()
                    : _peersByEp.Values.Where(p => p.ClientId != sender.ClientId).ToArray();
            }

            if (recipients.Length > 0)
            {
                var fwd = new GameDataMessage(GameCode.From(roomCode), rawSubMessages);
                foreach (var r in recipients)
                {
                    await SendRootAsync(r, fwd, reliable: true);
                }
            }
        }

        private void ApplyHostCustomNetworkTransformDeserialize(uint targetNetId, byte[] data)
        {
            if (!_botsByTransformNetId.TryGetValue(targetNetId, out var view))
            {
                return;
            }

            // Exact GameAssembly.dll CustomNetworkTransform.Deserialize(reader, initialState: false) + MoveTowardNextPoint
            using var r = MessageReader.Get(data);
            var update = CustomNetworkTransformUpdate.Deserialize(r, initialState: false);

            lock (_gate)
            {
                if (view.IsPaused)
                {
                    return;
                }

                for (var i = 0; i < update.Positions.Count; i++)
                {
                    var sid = (ushort)(update.SequenceId + i);
                    if (SidGreaterThan(sid, view.LastSequenceId))
                    {
                        view.LastSequenceId = sid;
                        view.IncomingPosQueue.Enqueue(update.Positions[i]);
                        view.AcceptedUpdates++;
                        if (_matchStarted)
                        {
                            view.AcceptedUpdatesAfterStart++;
                        }
                    }
                    else
                    {
                        view.RejectedUpdates++;
                    }
                }
            }
        }

        // Exact 50 Hz Unity FixedUpdate loop (GameAssembly.dll CustomNetworkTransform.FixedUpdate 0x180670A80 + MoveTowardNextPoint 0x180671120)
        private async Task RunFixedUpdateLoopAsync()
        {
            const float fixedDeltaTime = 0.02f;
            var sw = Stopwatch.StartNew();
            var simulatedTime = 0.0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(10, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var elapsed = sw.Elapsed.TotalSeconds;
                lock (_gate)
                {
                    var steps = 0;
                    while (simulatedTime + fixedDeltaTime <= elapsed && steps < 10)
                    {
                        simulatedTime += fixedDeltaTime;
                        steps++;
                        foreach (var view in _botsByTransformNetId.Values)
                        {
                            StepBotFixedUpdate(view, fixedDeltaTime);
                        }
                    }
                }
            }
        }

        private static void StepBotFixedUpdate(BotTransformView view, float dt)
        {
            if (view.IsPaused)
            {
                return;
            }

            if (view.IncomingPosQueue.Count <= 0)
            {
                var diff = view.Position - view.LastPosition;
                if (diff.LengthSquared() >= 0.0001f)
                {
                    view.Position = view.LastPosition;
                    view.Velocity = Vector2.Zero;
                }

                return;
            }

            MoveTowardNextPoint(view, dt);
            view.Position += view.Velocity * dt;
        }

        // Instruction-exact port of GameAssembly.dll CustomNetworkTransform.MoveTowardNextPoint (0x180671120)
        // using the exact .rdata float constants extracted from Among Us 2026.7.21 GameAssembly.dll:
        //   DidPass_678   = 0.003f  (0x182976678)
        //   Teleport_1A0  = 0.05f   (0x1829761A0)
        //   SnapLast_198  = 0.01f   (0x182976198)
        //   RubberLow_1B4 = 0.5f    (0x1829761B4)
        //   RubberHigh_684= 0.995f  (0x182976684)
        //   LerpRate_214  = 3.0f    (0x182976214)
        //   SendThresh_2F0= 0.0001f (0x1829762F0)
        //   ClampMag_224  = 10.0f   (0x182976224)
        private static void MoveTowardNextPoint(BotTransformView view, float dt)
        {
            var nextPos = view.IncomingPosQueue.Peek();
            var curPos = view.Position;
            var d = nextPos - curPos;

            // DidPassPosition(nextPos, curPos, lastPosition)
            var v1 = curPos - view.LastPosition;
            var v2 = nextPos - curPos;
            var v3 = nextPos - view.LastPosition;
            bool didPass;
            if (v1.LengthSquared() < 0.003f || v2.LengthSquared() < 0.003f)
            {
                didPass = true;
            }
            else
            {
                var n1 = Vector2.Normalize(v1);
                var n3 = Vector2.Normalize(v3);
                didPass = Vector2.Dot(n1, n3) <= 0.003f;
            }

            Vector2 finalNextPos;
            bool skipCountgt1Popped = false;
            if (!didPass)
            {
                finalNextPos = nextPos;
            }
            else if (view.IncomingPosQueue.Count > 1)
            {
                finalNextPos = view.IncomingPosQueue.Dequeue();
                skipCountgt1Popped = true;
                if (Vector2.DistanceSquared(curPos, finalNextPos) > 0.05f)
                {
                    view.Position = finalNextPos;
                }

                view.LastPosition = finalNextPos;
            }
            else
            {
                finalNextPos = nextPos;
                if (Vector2.DistanceSquared(curPos, finalNextPos) > 0.01f)
                {
                    view.Position = finalNextPos;
                }

                view.LastPosition = finalNextPos;
                view.Velocity = Vector2.Zero;
            }

            view.RubberbandModifier = view.IncomingPosQueue.Count <= 5 ? 0.5f : 0.995f;

            // ShouldExtendCurrentFrame(nextPos, curPos) -> incomingPosQueue.Count <= 5 && !didPass
            var shouldExtend = view.IncomingPosQueue.Count <= 5 && !didPass;
            if (!shouldExtend)
            {
                if (!skipCountgt1Popped)
                {
                    finalNextPos = view.IncomingPosQueue.Dequeue();
                }

                var dPop = finalNextPos - curPos;
                var reqSpeed = dPop.Length() / dt;
                if (reqSpeed > 10.0f)
                {
                    reqSpeed = 10.0f;
                }

                var lerpT = Math.Clamp(dt * 3.0f, 0f, 1f);
                view.IdealSpeed = view.IdealSpeed + (reqSpeed - view.IdealSpeed) * lerpT;
                view.LastPosition = finalNextPos;
            }

            if (d.LengthSquared() <= 0.0001f)
            {
                view.Velocity = Vector2.Zero;
            }
            else
            {
                var dir = Vector2.Normalize(d);
                view.Velocity = dir * (view.IdealSpeed * view.RubberbandModifier);
            }
        }

        // Exact mirror of GameAssembly.dll CustomNetworkTransform.Deserialize lines 518-529 (NetHelpers.SidGreaterThan)
        private static bool SidGreaterThan(ushort newSid, ushort prevSid)
        {
            var max = (ushort)(prevSid + 0x7FFF);
            return prevSid < max
                ? (newSid > prevSid && newSid <= max)
                : (newSid > prevSid || newSid <= max);
        }

        private GameDataSubMessage BuildHostPlayerControlSpawn()
        {
            using var xw = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, new[] { HostPosition }).Serialize(xw, initialState: true);
            return GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerControl,
                HostClientId,
                1,
                new[]
                {
                    new SpawnComponentEntry(HostPcNetId, ComponentDataCodec.SerializePlayerControlInitial(0)),
                    new SpawnComponentEntry(HostPhysicsNetId, Array.Empty<byte>()),
                    new SpawnComponentEntry(HostTransformNetId, xw.ToByteArray(includeHeader: false)),
                }));
        }

        private static GameDataSubMessage BuildBotPlayerControlSpawn(ClientPeer p)
        {
            using var xw = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, new[] { Vector2.Zero }).Serialize(xw, initialState: true);
            return GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerControl,
                p.ClientId,
                1,
                new[]
                {
                    new SpawnComponentEntry(p.PlayerControlNetId, ComponentDataCodec.SerializePlayerControlInitial(p.PlayerId)),
                    new SpawnComponentEntry(p.PhysicsNetId, Array.Empty<byte>()),
                    new SpawnComponentEntry(p.TransformNetId, xw.ToByteArray(includeHeader: false)),
                }));
        }

        private static GameDataSubMessage BuildPlayerInfoSpawn(int clientId, byte playerId, string name, ColorType color, uint infoNetId, string puid) =>
            GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerInfo,
                -4,
                0,
                new[]
                {
                    new SpawnComponentEntry(
                        infoNetId,
                        ComponentDataCodec.SerializePlayerInfoInitial(playerId, clientId, name, color, 1, string.Empty, puid)),
                }));

        private async Task SendAckAsync(IPEndPoint ep, byte seqHi, byte seqLo)
        {
            var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, seqHi, seqLo, 0xFF };
            await _udp.SendAsync(ack, ack.Length, ep);
        }

        private async Task SendRootAsync(ClientPeer peer, IRootMessage root, bool reliable)
        {
            var payload = _registry.EncodeRoot(root, reliable ? MessageType.Reliable : MessageType.Unreliable);
            if (!reliable)
            {
                var pkt = new byte[1 + payload.Length];
                pkt[0] = (byte)UdpSendOption.Normal;
                Buffer.BlockCopy(payload, 0, pkt, 1, payload.Length);
                await _udp.SendAsync(pkt, pkt.Length, peer.EndPoint);
                return;
            }

            ushort seq;
            lock (peer)
            {
                seq = peer.ServerReliableSeq++;
            }

            var relPkt = new byte[3 + payload.Length];
            relPkt[0] = (byte)UdpSendOption.Reliable;
            relPkt[1] = (byte)(seq >> 8);
            relPkt[2] = (byte)(seq & 0xFF);
            Buffer.BlockCopy(payload, 0, relPkt, 3, payload.Length);
            await _udp.SendAsync(relPkt, relPkt.Length, peer.EndPoint);
        }

        private static string ExtractHelloName(byte[] data)
        {
            try
            {
                using var r = MessageReader.Get(data, 3);
                _ = r.ReadByte();
                _ = r.ReadInt32();
                return r.ReadString();
            }
            catch
            {
                return "Bot";
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _httpListener.Stop(); } catch { }
            try { _udp.Dispose(); } catch { }
            if (_httpTask is not null) await Task.WhenAny(_httpTask, Task.Delay(500));
            if (_udpTask is not null) await Task.WhenAny(_udpTask, Task.Delay(500));
            if (_fixedUpdateTask is not null) await Task.WhenAny(_fixedUpdateTask, Task.Delay(500));
            _cts.Dispose();
        }
    }
}
