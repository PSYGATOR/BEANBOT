using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using BeanBot.World;
using Xunit;

namespace BeanBot.Tests;

public sealed class Phase1IntegrationTests
{
    [Fact]
    public async Task EndToEndLocalHarness_CompletesFullMatch_TasksKillVentReportVote_HttpControl_AndReplayAudit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beanbot-p1-e2e-" + Guid.NewGuid().ToString("N"));
        var replayDir = Path.Combine(dir, "replays");
        var locksDir = Path.Combine(dir, "locks");
        Directory.CreateDirectory(replayDir);
        Directory.CreateDirectory(locksDir);

        // Bind a real loopback UDP relay server simulating an Among Us relay node.
        using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
        var receivedPackets = new ConcurrentQueue<(UdpSendOption Opt, IRootMessage? Root)>();
        var serverReceivedRpcs = new ConcurrentQueue<(uint NetId, IRpcMessage Rpc)>();
        IPEndPoint? clientEp = null;
        ushort serverReliableSeq = 1;

        using var serverCts = new CancellationTokenSource();
        var serverLoop = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try
                {
                    res = await serverUdp.ReceiveAsync(serverCts.Token);
                }
                catch
                {
                    break;
                }

                clientEp = res.RemoteEndPoint;
                var data = res.Buffer;
                if (data.Length == 0) continue;
                var opt = (UdpSendOption)data[0];

                if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping && data.Length >= 3)
                {
                    var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                    await serverUdp.SendAsync(ack, ack.Length, clientEp);

                    if (opt == UdpSendOption.Reliable)
                    {
                        var payload = data.AsSpan(3).ToArray();
                        var root = ProtocolRegistry.Default.DecodeRoot(payload);
                        receivedPackets.Enqueue((opt, root));

                        if (root is GameDataMessage gdm)
                        {
                            foreach (var sub in GameDataSubMessage.ParseAll(gdm.RawSubMessages))
                            {
                                if (sub.Kind == GameDataKind.Rpc &&
                                    sub.TryParseRpc(ProtocolRegistry.Default, out var targetNetId, out var rpc) &&
                                    rpc is not null)
                                {
                                    serverReceivedRpcs.Enqueue((targetNetId, rpc));
                                }
                            }
                        }
                    }
                    else
                    {
                        receivedPackets.Enqueue((opt, null));
                    }
                }
                else if (opt == UdpSendOption.Normal && data.Length > 1)
                {
                    var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(1).ToArray());
                    receivedPackets.Enqueue((opt, root));
                }
            }
        });

        async Task SendToBotAsync(IRootMessage root)
        {
            Assert.NotNull(clientEp);
            var payload = ProtocolRegistry.Default.EncodeRoot(root, MessageType.Reliable);
            var pkt = new byte[3 + payload.Length];
            var seq = serverReliableSeq++;
            pkt[0] = (byte)UdpSendOption.Reliable;
            pkt[1] = (byte)(seq >> 8);
            pkt[2] = (byte)(seq & 0xff);
            Buffer.BlockCopy(payload, 0, pkt, 3, payload.Length);
            await serverUdp.SendAsync(pkt, pkt.Length, clientEp!);
        }

        var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
        var httpStub = new StubMatchmakerHttp(ipUint, (ushort)serverEp.Port);
        var profile = ServerProfile.Private("http://127.0.0.1:19999", useDtls: false, useDeviceAuth: false);
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
        using var mmHttp = new HttpClient(httpStub);
        var auth = new DeviceAuthSource(vault, mmHttp);
        var matchmaker = new MatchmakerClient(mmHttp);

        await using var bot = new BotClient(profile, vault, auth, matchmaker);
        bot.DeterministicManualTick = true; // Deterministic stepping for exact assertions
        bot.BehaviorState.TaskDwellDurationSec = 0.05;
        bot.BehaviorState.IntroSettleDurationSec = 0;

        // 1. P1.1/P1.2: Auth -> Matchmaker -> Hello (CLIENT_VERSION) + JoinGame over real loopback UDP
        await bot.JoinAsync("ABCDEF", "bot-00", "BeanZero", replayDir, locksDir);
        Assert.True(bot.IsConnected);

        // Wait briefly for Hello + JoinGame frames to arrive on UDP server
        await SpinWaitUntilAsync(() => receivedPackets.Any(p => p.Root is JoinGameMessage));
        Assert.Contains(receivedPackets, p => p.Opt == UdpSendOption.Hello);
        Assert.Contains(receivedPackets, p => p.Root is JoinGameMessage j && j.GameCode.Code == "ABCDEF");

        // 2. Server replies with JoinedGame -> bot emits SceneChange("OnlineGame")
        var code = GameCode.From("ABCDEF");
        await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(
            code,
            PlayerClientId: 10,
            HostClientId: 5,
            OtherPlayers: new[]
            {
                new JoinedPlayerEntry(5, "HostOne", new PlatformSpecificData(Platforms.StandaloneSteamPC, "HostOne"), 1, "p-host", "host#1"),
            }));

        Assert.Equal(GamePhase.Lobby, bot.World.Phase);
        Assert.Equal(10, bot.World.LocalClientId);

        // 3. Spawn bot PlayerControl + CustomNetworkTransform + NormalGameManager options + StartGame + ShipStatus + Tasks
        var playerSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.PlayerControl,
            OwnerClientId: 10,
            Flags: 1,
            Components: new[]
            {
                new SpawnComponentEntry(100, ComponentDataCodec.SerializePlayerControlInitial(playerId: 1)),
                new SpawnComponentEntry(101, Array.Empty<byte>()),
                new SpawnComponentEntry(102, BuildTransformInitial(1, new Vector2(1.0f, 3.0f))),
            }));
        var victimSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.PlayerControl,
            OwnerClientId: 5,
            Flags: 0,
            Components: new[]
            {
                new SpawnComponentEntry(200, ComponentDataCodec.SerializePlayerControlInitial(playerId: 0)),
                new SpawnComponentEntry(201, Array.Empty<byte>()),
                new SpawnComponentEntry(202, BuildTransformInitial(1, new Vector2(2.6f, -9.8f))),
            }));
        var gameManagerSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.NormalGameManager,
            OwnerClientId: -2,
            Flags: 0,
            Components: new[]
            {
                new SpawnComponentEntry(300, ComponentDataCodec.SerializeGameManagerOptionsDelta(new NormalGameOptions
                {
                    PlayerSpeedMod = 1.5f,
                    KillCooldown = 10.0f,
                    KillDistance = KillDistances.Long,
                    DiscussionTime = 0,
                })),
            }));
        var shipSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
            SpawnObjectId.SkeldShipStatus,
            OwnerClientId: -2,
            Flags: 0,
            Components: new[] { new SpawnComponentEntry(400, Array.Empty<byte>()) }));

        await bot.ProcessInboundRootMessageAsync(new StartGameMessage(code));
        Assert.Equal(GamePhase.Dropship, bot.World.Phase);

        await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[]
        {
            playerSpawn,
            victimSpawn,
            gameManagerSpawn,
            shipSpawn,
            GameDataSubMessage.FromRpc(100, new SetRoleRpc(RoleTypes.Crewmate)),
            GameDataSubMessage.FromRpc(100, new SetTasksRpc(new byte[] { 0 })), // Skeld task 0 = SwipeCard in Admin
        })));

        Assert.Equal(GamePhase.Tasks, bot.World.Phase);
        Assert.Equal(1.5f, bot.World.Settings.PlayerSpeed);
        Assert.Single(bot.World.LocalTasks);

        // 4. P1.5: Step 10Hz ticks until SwipeCard task reaches 100% and CompleteTaskRpc hits the UDP server
        for (var i = 0; i < 120 && bot.World.TaskCompletionRatio < 1.0; i++)
        {
            await bot.RunMatchTickAsync(0.1);
        }

        Assert.Equal(1.0, bot.World.TaskCompletionRatio);
        await SpinWaitUntilAsync(() => serverReceivedRpcs.Any(r => r.Rpc is CompleteTaskRpc));
        Assert.True(bot.Counters.TaskCompletions >= 1);

        // 5. P1.7/P1.8: Switch to Impostor -> Kill + Vent -> Report -> Vote -> EndGame
        bot.World.LastKillTimestampSec = -100;
        bot.World.LastVentTimestampSec = -100;
        await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[]
        {
            GameDataSubMessage.FromRpc(100, new SetRoleRpc(RoleTypes.Impostor)),
        })));

        // Position near HostOne (2.6, -9.8) & AdminVent (id 0 at 2.544, -9.955)
        bot.World.UpdatePlayerPosition(10, new Vector2(2.6f, -9.8f));
        var (killOk, _) = bot.CommandKill("HostOne");
        Assert.True(killOk);
        await bot.RunMatchTickAsync(0.1);
        await SpinWaitUntilAsync(() => serverReceivedRpcs.Any(r => r.Rpc is CheckMurderRpc));

        // Simulate host confirming MurderPlayer
        await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[]
        {
            GameDataSubMessage.FromRpc(100, new MurderPlayerRpc(200, MurderResultFlags.Succeeded)),
        })));
        Assert.Equal(1, bot.Counters.Kills);
        Assert.Single(bot.World.DeadBodies);

        // Vent sequence (>=300ms dwell -> ExitVent + SnapTo)
        var (ventOk, _) = bot.CommandVent(0);
        Assert.True(ventOk);
        await bot.RunMatchTickAsync(0.1); // EnterVent
        Assert.True(bot.World.InVent);
        await bot.RunMatchTickAsync(0.35); // Dwell & ExitVent + SnapTo
        Assert.False(bot.World.InVent);
        await SpinWaitUntilAsync(() => serverReceivedRpcs.Any(r => r.Rpc is EnterVentRpc) && serverReceivedRpcs.Any(r => r.Rpc is ExitVentRpc));

        // Report + Vote sequence (Crewmate auto-reports dead body within 2m -> Meeting -> Vote)
        await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[]
        {
            GameDataSubMessage.FromRpc(100, new SetRoleRpc(RoleTypes.Crewmate)),
        })));
        bot.World.UpdatePlayerPosition(10, new Vector2(2.6f, -9.8f));
        await bot.RunMatchTickAsync(0.1); // ReportDeadBody within 2m
        await SpinWaitUntilAsync(() => serverReceivedRpcs.Any(r => r.Rpc is ReportDeadBodyRpc));

        await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[]
        {
            GameDataSubMessage.FromRpc(100, new StartMeetingRpc(0)),
        })));
        Assert.Equal(GamePhase.Meeting, bot.World.Phase);

        var (voteOk, _) = bot.CommandVote("skip");
        Assert.True(voteOk);
        await bot.RunMatchTickAsync(0.1);
        await SpinWaitUntilAsync(() => serverReceivedRpcs.Any(r => r.Rpc is CastVoteRpc cv && cv.SuspectPlayerId == -1));

        // 6. P1.10/P1.11: Loopback HTTP Control Server + Replay Log + Counters check
        var freePort = GetFreeTcpPort();
        var surface = new CommandSurface(bot, vault);
        await using var httpControl = new HttpControlServer(surface, bot, freePort);
        httpControl.Start();

        using var cliHttp = new HttpClient();
        var statusResp = await cliHttp.GetStringAsync($"{httpControl.BaseUri}status");
        Assert.Contains("ABCDEF", statusResp);

        var cmdResp = await cliHttp.PostAsync(
            $"{httpControl.BaseUri}command",
            new StringContent("{\"command\":\"say \\\"Where?\\\"\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, cmdResp.StatusCode);

        await bot.ProcessInboundRootMessageAsync(new EndGameMessage(code, GameOverReason.CrewmatesByTask));
        Assert.Equal(GamePhase.End, bot.World.Phase);

        // Ensure ReplayLogger has recorded monotonic actions with state hashes
        Assert.NotNull(bot.ReplayLogPath);
        await bot.StopAsync();
        var viewer = ReplayViewer.Load(bot.ReplayLogPath!);
        Assert.True(viewer.Count >= 5);
        for (var i = 1; i < viewer.Entries.Count; i++)
        {
            Assert.True(viewer.Entries[i].MonotonicMs >= viewer.Entries[i - 1].MonotonicMs);
            Assert.False(string.IsNullOrWhiteSpace(viewer.Entries[i].StateHash));
        }

        // Trigger a clean UDP reliable packet from the fake server to confirm client receive path works when pumping.
        await SendToBotAsync(new WaitForHostMessage(code, 5));
        serverCts.Cancel();
        await serverLoop;
    }

    [Fact, Trait("Category", "Live")]
    public async Task LiveSmokeTest_JoinsConfiguredRoomWithRealEosAndDtls()
    {
        var roomCode = Environment.GetEnvironmentVariable("BEANBOT_LIVE_TEST_ROOM");
        if (string.IsNullOrWhiteSpace(roomCode))
        {
            // Explicit opt-in required for hitting official Innersloth servers.
            return;
        }

        var region = Environment.GetEnvironmentVariable("BEANBOT_LIVE_REGION") ?? "na";
        var dir = Path.Combine(Path.GetTempPath(), "beanbot-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var profile = ServerProfile.Live(region, certsDirectory: Path.Combine(dir, "certs"), acceptNewPin: true);
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "live-smoke-pass");
        using var http = new HttpClient();
        var auth = new DeviceAuthSource(vault, http);
        var matchmaker = new MatchmakerClient(http);

        if (string.Equals(roomCode, "host", StringComparison.OrdinalIgnoreCase))
        {
            var logs = new ConcurrentQueue<string>();
            using var lf = new ListLoggerProvider(logs);

            var account = vault.GetOrCreate("live-smoke-00", "BeanSmoke");
            var session = await auth.AuthenticateAsync(account, profile);
            var mmToken = await matchmaker.GetMatchmakerTokenAsync(
                profile,
                session.Puid,
                session.DisplayName,
                Language.English,
                session.IdToken ?? session.AccessToken);
            var relayEndPoint = await matchmaker.HostRoomAsync(profile, mmToken);
            logs.Enqueue($"Matchmaker assigned relay={relayEndPoint}");

            uint authNonce;
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var dtlsAuth = profile.CreateHazelDtlsAuthClient(lf);
                authNonce = await dtlsAuth.AuthenticateAsync(
                    relayEndPoint,
                    ProtocolVersion.CLIENT_VERSION.Value,
                    Platforms.Android,
                    mmToken,
                    friendCode: string.Empty,
                    authCts.Token);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Hazel DTLS Auth to {HazelDtlsAuthClient.GetAuthEndPoint(relayEndPoint)} failed: {ex.Message}\nLogs:\n{string.Join('\n', logs)}");
                return;
            }

            Assert.True(File.Exists(Path.Combine(dir, "certs", $"{region.ToLowerInvariant()}.der")));

            await using var transport = profile.CreateRelayGameTransport(lf);
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await transport.ConnectAsync(relayEndPoint, connectCts.Token);
            Assert.True(transport.IsConnected);

            var registry = ProtocolRegistry.Default;
            var framer = new HazelFramer(transport, registry);
            await framer.SendHelloAsync(
                session.DisplayName,
                session.Account.DeviceId,
                Language.English,
                QuickChatModes.QuickChatOnly,
                CrossplayFlags.None,
                authNonce: authNonce,
                ct: connectCts.Token);

            // Wait for Hello ACK before sending HostGame so the server has registered the session.
            var helloAcked = false;
            using (var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                while (!helloAcked && !ackCts.IsCancellationRequested)
                {
                    var ackFrame = await framer.ReceiveFrameAsync(ackCts.Token);
                    if (ackFrame?.Disconnect is { } helloDc)
                    {
                        Assert.Fail($"Relay {relayEndPoint} disconnected after Hello: {helloDc.Reason} ({helloDc.CustomMessage})");
                    }

                    if (ackFrame?.SendOption == UdpSendOption.Acknowledgement)
                    {
                        helloAcked = true;
                    }
                }
            }

            Assert.True(helloAcked, $"Relay {relayEndPoint} did not ACK Hello.");

            await framer.SendReliableRootAsync(new HostGameMessage(GameOptionsOrCode.FromRequest(
                new NormalGameOptions(),
                CrossplayFlags.All,
                GameFilterOptions.CreateDefault())));

            HostGameMessage? hosted = null;
            var frames = new List<InboundFrame>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested && hosted is null)
            {
                await framer.PumpRetransmitsAsync(cts.Token);
                using var sliceCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                sliceCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                InboundFrame? frame;
                try
                {
                    frame = await framer.ReceiveFrameAsync(sliceCts.Token);
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    continue;
                }

                if (frame is null)
                {
                    break;
                }

                frames.Add(frame);
                if (frame.Disconnect is not null)
                {
                    break;
                }

                hosted = frame.Messages.OfType<HostGameMessage>().FirstOrDefault(h => h.Payload.GameCode is not null);
            }

            var disconnect = frames.Select(f => f.Disconnect).FirstOrDefault(d => d is not null);
            Assert.True(
                hosted?.Payload.GameCode is not null,
                disconnect is null
                    ? $"Relay {relayEndPoint} DTLS Auth + plain UDP connected (nonce={authNonce}), pinned cert OK, then returned {frames.Count} frame(s) ({string.Join(',', frames.Select(f => f.SendOption))}) with no HostGame GameCode."
                    : $"Relay {relayEndPoint} disconnected: Reason={disconnect.Reason}, Message={disconnect.CustomMessage}");

            // Also verify BotClient.JoinAsync("host") hosts and joins end-to-end.
            await using var hostBot = new BotClient(profile, vault, auth, matchmaker);
            await hostBot.JoinAsync(
                "host",
                accountId: "live-smoke-host",
                displayName: "BeanHost",
                replayDirectory: Path.Combine(dir, "replays"),
                locksDirectory: Path.Combine(dir, "locks"));
            Assert.True(hostBot.IsConnected);
            Assert.False(string.IsNullOrWhiteSpace(hostBot.World.LobbyCode));
            await hostBot.StopAsync();
            return;
        }

        await using var bot = new BotClient(profile, vault, auth, matchmaker);
        await bot.JoinAsync(
            roomCode,
            accountId: "live-smoke-00",
            displayName: "BeanSmoke",
            replayDirectory: Path.Combine(dir, "replays"),
            locksDirectory: Path.Combine(dir, "locks"));
        Assert.True(bot.IsConnected);
        await bot.StopAsync();
    }

    private static byte[] BuildTransformInitial(ushort seqId, Vector2 pos)
    {
        using var w = MessageWriter.Get(MessageType.Reliable);
        new CustomNetworkTransformUpdate(seqId, new[] { pos }).Serialize(w, initialState: true);
        return w.ToByteArray(includeHeader: false);
    }

    private static async Task SpinWaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(predicate(), $"Condition not met within {timeoutMs}ms.");
    }

    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class StubMatchmakerHttp(uint ipUint, ushort port) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/user")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("\"local-mm-token\"", Encoding.UTF8, "application/json"),
                });
            }

            if (path.StartsWith("/api/games/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = port }), Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<string> logs) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(logs);
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class ListLogger(ConcurrentQueue<string> logs) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => logs.Enqueue(formatter(state, exception));
        }
    }
}
