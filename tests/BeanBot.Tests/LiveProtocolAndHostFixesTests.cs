using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Host;
using BeanBot.Matchmaker;
using BeanBot.Observability;
using BeanBot.Orchestrator;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using BeanBot.World;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeanBot.Tests;

public sealed class LiveProtocolAndHostFixesTests
{
    private static readonly ProtocolRegistry Registry = ProtocolRegistry.Default;

    public static IEnumerable<object[]> LiveObservedRpcs()
    {
        yield return [new BootFromVentRpc(3)];
        yield return [new SetLevelRpc(42u)];
        yield return [new SetCosmeticStrRpc(RpcId.SetVisorStr, "visor_EmptyVisor", 1)];
        yield return [new SetCosmeticStrRpc(RpcId.SetNamePlateStr, "nameplate_NoPlate", 2)];
    }

    [Theory]
    [MemberData(nameof(LiveObservedRpcs))]
    public void LiveObservedRpcs_RoundTrip(IRpcMessage original)
    {
        var encoded = Registry.EncodeRpc(original);
        var decoded = Registry.DecodeRpc(encoded);
        Assert.Equal(original, decoded);
        Assert.Equal(encoded, Registry.EncodeRpc(decoded));
    }

    [Fact]
    public void ServerJoinGameNotification_RoundTrips()
    {
        var code = GameCode.From("KLFZWG");
        var entry = new JoinedPlayerEntry(
            42,
            "HumanPlayer",
            PlatformRecord.Build("HumanPlayer", "33333333-3333-4333-8333-333333333333"),
            10u,
            "puid-human",
            "human#0001");
        var original = new JoinGameMessage(code, ServerJoinedPlayer: entry, ServerHostClientId: 7);

        var bytes = Registry.EncodeRoot(original);
        var decoded = Assert.IsType<JoinGameMessage>(Registry.DecodeRoot(bytes));

        Assert.Equal(code, decoded.GameCode);
        Assert.Equal(7, decoded.ServerHostClientId);
        Assert.NotNull(decoded.ServerJoinedPlayer);
        Assert.Equal(42, decoded.ServerJoinedPlayer!.ClientId);
        Assert.Equal("HumanPlayer", decoded.ServerJoinedPlayer.Name);
        Assert.Equal("puid-human", decoded.ServerJoinedPlayer.Puid);
    }

    [Fact]
    public void PlayerInfoInitial_And_NormalGameManagerInitial_RoundTrip()
    {
        var piBytes = ComponentDataCodec.SerializePlayerInfoInitial(
            playerId: 2,
            clientId: 19,
            name: "BeanHost",
            color: ColorType.Cyan,
            playerLevel: 5,
            friendCode: "bean#1234",
            puid: "puid-19");

        Assert.True(ComponentDataCodec.TryParsePlayerInfoInitial(piBytes, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal((byte)2, parsed!.PlayerId);
        Assert.Equal(19, parsed.ClientId);
        Assert.Equal("BeanHost", parsed.PlayerName);
        Assert.Equal((int)ColorType.Cyan, parsed.ColorId);

        var normal = new NormalGameOptions
        {
            Map = MapTypes.Skeld,
            MaxPlayers = 10,
            NumImpostors = 2,
            PlayerSpeedMod = 1.5f,
            KillCooldown = 25.0f,
        };
        var gmBytes = ComponentDataCodec.SerializeNormalGameManagerInitial(normal);
        Assert.True(ComponentDataCodec.TryExtractGameManagerOptions(gmBytes, out var extracted));
        var parsedOpts = Assert.IsType<NormalGameOptions>(extracted);
        Assert.Equal(MapTypes.Skeld, parsedOpts.Map);
        Assert.Equal(10, parsedOpts.MaxPlayers);
        Assert.Equal(2, parsedOpts.NumImpostors);
        Assert.Equal(1.5f, parsedOpts.PlayerSpeedMod);
        Assert.Equal(25.0f, parsedOpts.KillCooldown);
    }

    [Fact]
    public async Task Framer_IgnoreUnknownRootMessages_SkipsUnknownTags_OnlyWhenEnabled()
    {
        using var w = MessageWriter.Get(MessageType.Reliable);
        w.StartMessage(0xFD); // unknown root tag
        w.Write(0x12345678);
        w.EndMessage();
        w.StartMessage(RootMessageId.AlterGame);
        new AlterGameMessage(GameCode.From("ABCDEF"), AlterGameTags.ChangePrivacy, IsPublic: true).SerializeBody(w);
        w.EndMessage();
        var datagram = w.ToByteArray(includeHeader: true);
        datagram[2] = 1; // reliable sequence id = 1

        await Assert.ThrowsAsync<ProtocolException>(async () =>
        {
            var strictFramer = new HazelFramer(new FakeTransport(datagram), Registry);
            await strictFramer.ReceiveFrameAsync();
        });

        var tolerantFramer = new HazelFramer(new FakeTransport(datagram), Registry)
        {
            IgnoreUnknownRootMessages = true,
        };

        var frame = await tolerantFramer.ReceiveFrameAsync();
        Assert.NotNull(frame);
        var alter = Assert.Single(frame!.Messages);
        var parsedAlter = Assert.IsType<AlterGameMessage>(alter);
        Assert.True(parsedAlter.IsPublic);
    }

    [Fact]
    public async Task HostController_AllowsHumanCheckNameAndCheckColor_WhenNotKickingStrangers()
    {
        var bridge = new RecordingHostBridge(GameCode.From("LIVE01"));
        bridge.World.LocalClientId = 1;
        bridge.World.HostClientId = 1;
        bridge.World.PlayersByClientId[1] = new PlayerState
        {
            ClientId = 1,
            PlayerId = 0,
            NetId = 10,
            Name = "HostBot",
            ColorName = ColorType.Red.ToString(),
            Puid = "puid-host",
            IsHost = true,
        };
        bridge.World.PlayersByClientId[2] = new PlayerState
        {
            ClientId = 2,
            PlayerId = 1,
            NetId = 20,
            Name = string.Empty,
            Puid = "puid-human",
        };
        bridge.PlayerInfoNetIds[2] = 19;

        var counters = new Counters();
        var host = new HostController(
            bridge,
            new HostLobbyOptions { KickUnexpectedClients = false, PublicWhenSeated = false },
            counters);
        host.SetExpectedRoster([new ExpectedRosterSeat("puid-host", "HostBot", ColorType.Red, IsHost: true)]);
        await host.InitializeLobbyAsync();

        // Request colliding name & color -> host deduplicates name, picks free color, and stamps PlayerInfo NetId on SetColorRpc.
        await host.HandleCheckNameAsync(2, 20u, "HostBot");
        await host.HandleCheckColorAsync(2, 20u, ColorType.Red.ToString());

        var setName = Assert.Single(bridge.SentRpcs, r => r.TargetNetId == 20u && r.Rpc is SetNameRpc);
        Assert.Equal("HostBot 1", ((SetNameRpc)setName.Rpc).Name);

        var setColor = Assert.Single(bridge.SentRpcs, r => r.TargetNetId == 20u && r.Rpc is SetColorRpc);
        var colorRpc = (SetColorRpc)setColor.Rpc;
        Assert.Equal(19u, colorRpc.NetId);
        Assert.NotEqual(ColorType.Red, colorRpc.Color);
    }

    [Fact]
    public void NormalGameOptions_DefaultsMatchCapturedLiveV12Format_AndRoundTrip()
    {
        var opts = new NormalGameOptions();
        Assert.Equal(RulesPresets.Custom, opts.RulesPreset);
        Assert.Equal((byte)100, (byte)opts.RulesPreset);
        Assert.Equal((byte)2, (byte)RulesPresets.Flashlight);
        Assert.Equal(99, opts.RawRoleOptionsTail.Length);
        Assert.Equal(0x0B, opts.RawRoleOptionsTail[0]);

        using var writer = MessageWriter.Get(MessageType.Reliable);
        GameOptionsFactory.Serialize(writer, opts);
        var bytes = writer.ToByteArray(includeHeader: false);

        using var reader = MessageReader.Get(bytes);
        var decoded = Assert.IsType<NormalGameOptions>(GameOptionsFactory.Deserialize(reader));
        Assert.Equal(opts, decoded);
        Assert.Equal(0, reader.BytesRemaining);

        var liveCapturedBytes = Convert.FromBase64String(
            "DJQAAAEAAg8AAQAAAAAAgD8AAIA/AADAPwAAcEEBAQIBAAAAAQEPAAAAeAAAAAEPAQAAAAELBQAAAAMAAAAKCAIAAAACAAAPBQQAAAADAAA8CgADAAAAAgAAHg8JAAAAAgAADx4KAAAAAwAADx4BCAAAAAIAAAoBDAAAAAEAAAMSAAAAAQAADxMAAAABAAAyFQAAAAEAABQ=");
        using var liveReader = MessageReader.Get(liveCapturedBytes);
        var liveVersion = liveReader.ReadByte();
        Assert.Equal((byte)12, liveVersion);
        using var liveBodyReader = liveReader.ReadMessage();
        Assert.Equal((byte)GameModes.Normal, liveBodyReader.ReadByte());
        var liveOpts = NormalGameOptions.Deserialize(liveBodyReader, liveVersion);
        Assert.Equal(0, liveBodyReader.BytesRemaining);
        Assert.Equal(liveOpts.RawRoleOptionsTail, opts.RawRoleOptionsTail);
    }

    [Fact]
    public async Task HostController_DoesNotKickJoiners_WhenPublicInCountdown()
    {
        var bridge = new RecordingHostBridge(GameCode.From("PUBOKK"))
        {
            World = { LocalClientId = 1, HostClientId = 1, IsPublicLobby = true },
        };
        bridge.World.PlayersByClientId[1] = new PlayerState { ClientId = 1, PlayerId = 0, Name = "Bean00", Puid = "p-00", IsHost = true, IsReady = true };
        bridge.World.PlayersByClientId[2] = new PlayerState { ClientId = 2, PlayerId = 1, Name = "Bean01", Puid = "p-01", IsReady = true };

        var host = new HostController(
            bridge,
            new HostLobbyOptions
            {
                ExpectedJoiners = 1,
                PublicWhenSeated = true,
                KickUnexpectedClients = true,
                AutoStartWhenReady = true,
                StartGateDwell = TimeSpan.FromSeconds(10),
            });
        host.SetExpectedRoster(
        [
            new ExpectedRosterSeat("p-00", "Bean00", ColorType.Red, IsHost: true),
            new ExpectedRosterSeat("p-01", "Bean01", ColorType.Blue),
        ]);
        await host.InitializeLobbyAsync();
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.StartCountdown, host.State);

        // Stranger joins while public lobby happens to be in StartCountdown -> must NOT be kicked.
        bridge.World.PlayersByClientId[99] = new PlayerState { ClientId = 99, PlayerId = 2, Name = "Human", Puid = "p-human", IsReady = true };
        await host.KickUnexpectedClientsAsync();
        Assert.DoesNotContain(bridge.SentRoots, r => r is KickPlayerMessage);

        // CheckName and CheckColor should still be served in public countdown too.
        bridge.PlayerInfoNetIds[99] = 199u;
        await host.HandleCheckNameAsync(99, 200u, "Human");
        await host.HandleCheckColorAsync(99, 200u, ColorType.Green.ToString());
        Assert.Contains(bridge.SentRpcs, r => r.Rpc is SetNameRpc sn && sn.Name == "Human");
        Assert.Contains(bridge.SentRpcs, r => r.Rpc is SetColorRpc sc && sc.NetId == 199u && sc.Color == ColorType.Green);
    }

    [Fact]
    public async Task MatchmakerClient_StopsImmediatelyOnLongRetryAfter429()
    {
        var userCalls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            userCalls++;
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"Reason\":116}", Encoding.UTF8, "application/json"),
            };
            resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(300));
            return resp;
        });

        var client = new MatchmakerClient(new HttpClient(handler), maxRetries: 4, delayAsync: (_, _) => Task.CompletedTask);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetMatchmakerTokenAsync(ServerProfile.Private("https://mm.test"), "puid-1", "Bot"));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(1, userCalls);
    }

    [Fact]
    public async Task BotClient_CachesMatchmakerToken_RefreshesOn401_AndEmitsPositiveStartCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-livefix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            IPEndPoint? clientEp = null;
            var hostGameCount = 0;
            var sentSubMessages = new List<GameDataSubMessage>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    clientEp = recv.RemoteEndPoint;
                    var data = recv.Buffer;
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping && data.Length >= 3)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, clientEp);

                        if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is HostGameMessage)
                            {
                                hostGameCount++;
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new HostGameMessage(GameOptionsOrCode.FromResponse(GameCode.From("LIVEOK"))),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, clientEp);
                            }
                            else if (root is GameDataMessage gdm)
                            {
                                lock (sentSubMessages)
                                {
                                    sentSubMessages.AddRange(GameDataSubMessage.ParseAll(gdm.RawSubMessages));
                                }
                            }
                        }
                    }
                }
            });

            var userEndpointCalls = 0;
            var failFirstHostWith401 = false;
            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    userEndpointCalls++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"\"mm-tok-{userEndpointCalls}\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path == "/api/games")
                {
                    if (failFirstHostWith401)
                    {
                        failFirstHostWith401 = false;
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    }

                    var json = JsonSerializer.Serialize(new { Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19997", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var bot = new BotClient(profile, vault, auth, matchmaker);
            bot.DeterministicManualTick = true;

            // Join #1 fetches /api/user once and caches token on AccountRecord.
            await bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            Assert.Equal(1, userEndpointCalls);
            Assert.Equal(1, hostGameCount);

            // Host initializes + spawns a joining client -> SetStartCounterRpc must have positive SequenceId >= 1.
            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("LIVEOK"), 1, 1, Array.Empty<JoinedPlayerEntry>()));
            var humanJoinerSub = GameDataSubMessage.FromSceneChange(2, "OnlineGame");
            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(GameCode.From("LIVEOK"), GameDataSubMessage.SerializeAll([humanJoinerSub])));

            var rpcStartCounters = new List<SetStartCounterRpc>();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (sentSubMessages)
                {
                    rpcStartCounters = sentSubMessages
                        .Select(s => s.TryParseRpc(ProtocolRegistry.Default, out _, out var rpc) ? rpc : null)
                        .OfType<SetStartCounterRpc>()
                        .ToList();
                }

                if (rpcStartCounters.Count > 0)
                {
                    break;
                }

                await Task.Delay(20);
            }

            Assert.NotEmpty(rpcStartCounters);
            Assert.All(rpcStartCounters, sc => Assert.True(sc.SequenceId >= 1));

            // Join #2 reuses cached matchmaker token (no second /api/user call).
            await bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            Assert.Equal(1, userEndpointCalls);

            // Join #3 simulates stale cached token (401 from /api/games) -> invalidates and refreshes /api/user once.
            failFirstHostWith401 = true;
            await bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            Assert.Equal(2, userEndpointCalls);

            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BotClient_ThrowsDescriptiveTimeoutException_WhenHostGameResponseTimesOut()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-hosttimeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ackLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length >= 3 && (UdpSendOption)data[0] is UdpSendOption.Hello or UdpSendOption.Reliable)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-timeout\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path == "/api/games")
                {
                    var json = JsonSerializer.Serialize(new { Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19996", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var bot = new BotClient(profile, vault, auth, matchmaker)
            {
                DeterministicManualTick = true,
                HostGameResponseTimeout = TimeSpan.FromMilliseconds(200),
            };

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks")));
            Assert.Contains("HostGame response", ex.Message);

            cts.Cancel();
            await ackLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    [Fact]
    public void JoinGameMessage_DecodesCompactServerHostNotification_WithoutTrailingProfile()
    {
        var raw = Convert.FromHexString("1A70FA8CBE290400BE290400");
        using var reader = MessageReader.Get(raw);
        var decoded = JoinGameMessage.Deserialize(reader);

        Assert.Null(decoded.ServerJoinedPlayer);
        Assert.Equal(272830, decoded.ServerHostClientId);
    }

    [Fact]
    public void SendQuickChatRpc_EncodesQuickChatPhraseTags_AndRoundTrips()
    {
        var simple = SendQuickChatRpc.Simple(1979);
        var simpleBytes = Registry.EncodeRpc(simple);
        Assert.Equal([RpcId.SendQuickChat, (byte)QuickChatPhraseType.SimplePhrase, 0xBB, 0x07], simpleBytes);
        var decodedSimple = Assert.IsType<SendQuickChatRpc>(Registry.DecodeRpc(simpleBytes));
        Assert.Equal(simple, decodedSimple);

        var playerRef = SendQuickChatRpc.ForPlayer(3);
        var playerBytes = Registry.EncodeRpc(playerRef);
        Assert.Equal((byte)QuickChatPhraseType.PlayerId, playerBytes[1]);
        Assert.Equal((byte)3, playerBytes[2]);
        var decodedPlayer = Assert.IsType<SendQuickChatRpc>(Registry.DecodeRpc(playerBytes));
        Assert.Equal(playerRef, decodedPlayer);

        var viaPrefix = new SendQuickChatRpc("qc:1234");
        var prefixBytes = Registry.EncodeRpc(viaPrefix);
        Assert.Equal([RpcId.SendQuickChat, (byte)QuickChatPhraseType.SimplePhrase, 0xD2, 0x04], prefixBytes);
        var decodedPrefix = Assert.IsType<SendQuickChatRpc>(Registry.DecodeRpc(prefixBytes));
        Assert.Equal(1234, decodedPrefix.StringNameId);

        Assert.True(SendQuickChatRpc.TryResolvePhrase("hello", out var helloId, out var helloKey));
        Assert.Equal(1979, helloId);
        Assert.Equal("QCHello", helloKey);
        Assert.True(SendQuickChatRpc.TryResolvePhrase("yes", out var yesId, out _));
        Assert.Equal(2022, yesId);
        Assert.False(SendQuickChatRpc.TryResolvePhrase("beesmart", out _, out _));
    }

    [Fact]
    public void CosmeticDefaults_UseNonZeroSequenceIds_AndNoMissingOutfitPlaceholders()
    {
        var piBytes = ComponentDataCodec.SerializePlayerInfoInitial(0, 10, "BeanHost", ColorType.Red);
        Assert.DoesNotContain("missing", Encoding.ASCII.GetString(piBytes));

        var cosmetics = new[]
        {
            SetCosmeticStrRpc.Hat(),
            SetCosmeticStrRpc.Skin(),
            SetCosmeticStrRpc.Pet(),
            SetCosmeticStrRpc.Visor(),
            SetCosmeticStrRpc.NamePlate(),
        };
        Assert.All(cosmetics, c => Assert.True(c.NextRpcSequenceId >= 1));
        Assert.DoesNotContain(cosmetics, c => string.Equals(c.Value, "hat_NoHat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BotClient_HostDefaultsToFreeChat_EmitsValidCosmeticSeqs_AndRoutesSayCommand()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-hostchat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            byte? observedChatMode = null;
            var sentSubMessages = new List<GameDataSubMessage>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 3)
                    {
                        continue;
                    }

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Hello && data.Length > 4)
                        {
                            using var hr = MessageReader.Get(data.AsSpan(4).ToArray());
                            _ = hr.ReadInt32(); // version
                            _ = hr.ReadString(); // name
                            _ = hr.ReadUInt32(); // nonce
                            _ = hr.ReadUInt32(); // language
                            observedChatMode = hr.ReadByte();
                        }
                        else if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is HostGameMessage)
                            {
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new HostGameMessage(GameOptionsOrCode.FromResponse(GameCode.From("CHATOK"))),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                            else if (root is GameDataMessage gdm)
                            {
                                lock (sentSubMessages)
                                {
                                    sentSubMessages.AddRange(GameDataSubMessage.ParseAll(gdm.RawSubMessages));
                                }
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-chat\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path == "/api/games")
                {
                    var json = JsonSerializer.Serialize(new { Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19995", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var bot = new BotClient(profile, vault, auth, matchmaker)
            {
                DeterministicManualTick = true,
            };

            await bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            Assert.Equal((byte)QuickChatModes.FreeChatOrQuickChat, observedChatMode);

            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("CHATOK"), 1, 1, Array.Empty<JoinedPlayerEntry>()));
            Assert.NotNull(bot.World.LocalPlayer);
            Assert.Equal(0f, bot.World.LocalPlayer!.Position.X);
            Assert.Equal(1.5f, bot.World.LocalPlayer.Position.Y);

            var surface = new CommandSurface(bot, vault);
            var sayRes = await surface.ExecuteAsync("say beesmart");
            Assert.True(sayRes.Ok);
            var qcRes = await surface.ExecuteAsync("say qc:42");
            Assert.True(qcRes.Ok);

            List<IRpcMessage> rpcs = [];
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (sentSubMessages)
                {
                    rpcs = sentSubMessages
                        .Select(s => s.TryParseRpc(Registry, out _, out var rpc) ? rpc : null)
                        .OfType<IRpcMessage>()
                        .ToList();
                }

                if (rpcs.Any(r => r is SendChatRpc) && rpcs.Any(r => r is SendQuickChatRpc))
                {
                    break;
                }

                await Task.Delay(20);
            }

            var cosmeticRpcs = rpcs.OfType<SetCosmeticStrRpc>().ToList();
            Assert.Equal(5, cosmeticRpcs.Count);
            Assert.All(cosmeticRpcs, c => Assert.True(c.NextRpcSequenceId >= 1));

            var chat = Assert.Single(rpcs.OfType<SendChatRpc>());
            Assert.Equal("beesmart", chat.Message);

            var quickChat = Assert.Single(rpcs.OfType<SendQuickChatRpc>());
            Assert.Equal(QuickChatPhraseType.SimplePhrase, quickChat.PhraseType);
            Assert.Equal(42, quickChat.StringNameId);

            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeviceAuthProfile_ForcesQuickChatOnly_AndMapsQuickChatAliases()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-qc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            byte? observedChatMode = null;
            var sentSubMessages = new List<GameDataSubMessage>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 3)
                    {
                        continue;
                    }

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Hello && data.Length > 4)
                        {
                            using var hr = MessageReader.Get(data.AsSpan(4).ToArray());
                            _ = hr.ReadInt32();
                            _ = hr.ReadString();
                            _ = hr.ReadUInt32();
                            _ = hr.ReadUInt32();
                            observedChatMode = hr.ReadByte();
                        }
                        else if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is HostGameMessage)
                            {
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new HostGameMessage(GameOptionsOrCode.FromResponse(GameCode.From("QCTEST"))),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                            else if (root is GameDataMessage gdm)
                            {
                                lock (sentSubMessages)
                                {
                                    sentSubMessages.AddRange(GameDataSubMessage.ParseAll(gdm.RawSubMessages));
                                }
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-qc\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path == "/api/games")
                {
                    var json = JsonSerializer.Serialize(new { Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            var liveProfile = ServerProfile.Live();
            await using var liveBot = new BotClient(liveProfile, vault, auth, matchmaker)
            {
                ChatMode = QuickChatModes.FreeChatOrQuickChat,
            };
            Assert.Equal(QuickChatModes.QuickChatOnly, liveBot.EffectiveChatMode);

            var deviceAuthProfile = ServerProfile.Private("http://127.0.0.1:19995", useDtls: false, useDeviceAuth: true);
            await using var daBot = new BotClient(deviceAuthProfile, vault, auth, matchmaker)
            {
                ChatMode = QuickChatModes.FreeChatOrQuickChat,
            };
            Assert.Equal(QuickChatModes.QuickChatOnly, daBot.EffectiveChatMode);

            var profile = ServerProfile.Private("http://127.0.0.1:19995", useDtls: false, useDeviceAuth: false);
            await using var bot = new BotClient(profile, vault, auth, matchmaker)
            {
                DeterministicManualTick = true,
                ChatMode = QuickChatModes.QuickChatOnly,
            };

            await bot.JoinAsync("host", "bot-host", "HostBean", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            Assert.Equal((byte)QuickChatModes.QuickChatOnly, observedChatMode);

            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("QCTEST"), 1, 1, Array.Empty<JoinedPlayerEntry>()));
            var surface = new CommandSurface(bot, vault);
            var badRes = await surface.ExecuteAsync("say beesmart");
            Assert.False(badRes.Ok);
            Assert.Contains("Quick Chat Only", badRes.Message);

            var okRes = await surface.ExecuteAsync("say hello");
            Assert.True(okRes.Ok);
            Assert.Contains("QCHello", okRes.Message);

            SendQuickChatRpc? quickChat = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && quickChat is null)
            {
                lock (sentSubMessages)
                {
                    quickChat = sentSubMessages
                        .Select(s => s.TryParseRpc(Registry, out _, out var rpc) ? rpc as SendQuickChatRpc : null)
                        .FirstOrDefault(r => r is not null);
                }

                await Task.Delay(20);
            }

            Assert.NotNull(quickChat);
            Assert.Equal(QuickChatPhraseType.SimplePhrase, quickChat!.PhraseType);
            Assert.Equal(1979, quickChat.StringNameId);

            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Follow_InLobbyDropship_MovesDirectlyWithoutSkeldTableSnap_AndResolvesPartialNameAndDataPlayerInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-follow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            var sentSubMessages = new List<GameDataSubMessage>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 3)
                    {
                        continue;
                    }

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is JoinGameMessage jg)
                            {
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new JoinedGameMessage(jg.GameCode, 1, 2, Array.Empty<JoinedPlayerEntry>()),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                            else if (root is GameDataMessage gdm)
                            {
                                lock (sentSubMessages)
                                {
                                    sentSubMessages.AddRange(GameDataSubMessage.ParseAll(gdm.RawSubMessages));
                                }
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-follow\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19996", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var bot = new BotClient(profile, vault, auth, matchmaker)
            {
                DeterministicManualTick = true,
            };

            await bot.JoinAsync("LOBBYA", "bot-follow", "BeanBot", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("LOBBYA"), 1, 2, Array.Empty<JoinedPlayerEntry>()));
            Assert.Equal(GamePhase.Lobby, bot.World.Phase);
            Assert.NotNull(bot.World.LocalPlayer);

            // Spawn our own PlayerControl in Dropship at (0, 1.5), plus uninitialized 255 PlayerInfo placeholder
            // and target PlayerControl at (1.2, 2.2), plus PlayerInfo that initially has PlayerId=255 and then updates via dirty Data.
            using var localTransformWriter = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, new[] { new Vector2(0f, 1.5f) }).Serialize(localTransformWriter, initialState: true);
            var spawnLocalPc = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerControl,
                OwnerClientId: 1,
                Flags: 1,
                Components: new[]
                {
                    new SpawnComponentEntry(101, ComponentDataCodec.SerializePlayerControlInitial(1)),
                    new SpawnComponentEntry(102, Array.Empty<byte>()),
                    new SpawnComponentEntry(103, localTransformWriter.ToByteArray(includeHeader: false)),
                }));

            using var targetTransformWriter = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, new[] { new Vector2(1.2f, 2.2f) }).Serialize(targetTransformWriter, initialState: true);

            var spawnTargetPc = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerControl,
                OwnerClientId: 2,
                Flags: 1,
                Components: new[]
                {
                    new SpawnComponentEntry(201, ComponentDataCodec.SerializePlayerControlInitial(0)),
                    new SpawnComponentEntry(202, Array.Empty<byte>()),
                    new SpawnComponentEntry(203, targetTransformWriter.ToByteArray(includeHeader: false)),
                }));
            var spawnPlaceholderPi = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerInfo,
                OwnerClientId: -2,
                Flags: 0,
                Components: new[]
                {
                    new SpawnComponentEntry(301, ComponentDataCodec.SerializePlayerInfoInitial(byte.MaxValue, -1, "CliffPlaceholder", ColorType.Red)),
                }));
            var spawnTargetPi = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerInfo,
                OwnerClientId: -2,
                Flags: 0,
                Components: new[]
                {
                    new SpawnComponentEntry(302, ComponentDataCodec.SerializePlayerInfoInitial(byte.MaxValue, 2, "Player2", ColorType.Blue)),
                }));

            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(
                GameCode.From("LOBBYA"),
                GameDataSubMessage.SerializeAll(new[] { spawnLocalPc, spawnTargetPc, spawnPlaceholderPi, spawnTargetPi })));
            Assert.True(Vector2.Distance(new Vector2(0f, 1.5f), bot.World.LocalPlayer!.Position) < 0.01f);

            // Deliver dirty GameDataTag.Data PlayerInfo update assigning real PlayerId=0 and multi-word name "Cliff Shot".
            using var dirtyPiWriter = MessageWriter.Get(MessageType.Reliable);
            dirtyPiWriter.WritePacked(302u);
            dirtyPiWriter.Write(ComponentDataCodec.SerializePlayerInfoInitial(0, 2, "Cliff Shot", ColorType.Blue));
            var dirtyPiSub = new GameDataSubMessage(GameDataTag.Data, dirtyPiWriter.ToByteArray(includeHeader: false));

            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(
                GameCode.From("LOBBYA"),
                GameDataSubMessage.SerializeAll(new[] { dirtyPiSub })));

            Assert.True(bot.World.PlayersByClientId.TryGetValue(2, out var target));
            Assert.Equal("Cliff Shot", target!.Name);
            Assert.Equal((byte)0, target.PlayerId);
            Assert.True(Vector2.Distance(new Vector2(1.2f, 2.2f), target.Position) < 0.01f);

            // CommandSurface partial name match ("Cliff") should resolve to ClientId 2 (ignoring CliffPlaceholder with ClientId=-1 / PlayerId=255).
            var surface = new CommandSurface(bot, vault);
            var followRes = await surface.ExecuteAsync("follow Cliff");
            Assert.True(followRes.Ok);
            Assert.Contains("Cliff Shot", followRes.Message);

            // Clear prior movement snapshots and step once in Lobby: must move straight toward target's follow slot without Skeld navmesh snap.
            var beforePos = bot.World.LocalPlayer.Position;
            var distBefore = Vector2.Distance(beforePos, target.Position);
            lock (sentSubMessages)
            {
                sentSubMessages.Clear();
            }

            await bot.RunMatchTickAsync(0.25);
            var afterPos = bot.World.LocalPlayer.Position;
            var distAfter = Vector2.Distance(afterPos, target.Position);
            Assert.True(distAfter < distBefore, $"Expected direct movement closer to target in Lobby, before={beforePos}, after={afterPos}, distBefore={distBefore:F3}, distAfter={distAfter:F3}");
            Assert.True(afterPos.X > 0.1f && afterPos.Y is > 0.5f and < 2.5f, $"Expected Dropship movement without Skeld snap, got {afterPos}");

            await bot.StopAsync();
            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Roster_AuthenticateAndVerifyDistinctPuids_SkipsAuthSpacingForCachedTokens()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-cached-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            var roster = Roster.CreateJoiners(3);
            foreach (var seat in roster.Seats)
            {
                var rec = vault.GetOrCreate(seat.AccountId, seat.DisplayName);
                rec.CachedToken = $"tok-{seat.SeatIndex}";
                rec.TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
                rec.Puid = $"puid-{seat.SeatIndex:D2}";
                vault.SaveRecord(rec);
            }

            var stubAuth = new CachedAuthStub();
            var sw = Stopwatch.StartNew();
            var results = await roster.AuthenticateAndVerifyDistinctPuidsAsync(
                vault,
                stubAuth,
                ServerProfile.Live("na"),
                TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.Equal(3, results.Count);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Expected zero spacing for cached tokens, elapsed={sw.Elapsed}.");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CommandSurface_GetPlayersSnapshot_OrdersExternalPlayersFirst_AndExcludesPlaceholder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-players-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            await using var bot = new BotClient(
                ServerProfile.Local(),
                vault,
                new DeviceAuthSource(vault, http),
                new MatchmakerClient(http));
            bot.World.LocalClientId = 10;
            bot.World.PlayersByClientId[10] = new PlayerState
            {
                ClientId = 10,
                PlayerId = 1,
                Name = "BotZero",
                ColorName = ColorType.Blue.ToString(),
            };
            bot.World.PlayersByClientId[5] = new PlayerState
            {
                ClientId = 5,
                PlayerId = 0,
                Name = "Alice",
                ColorName = ColorType.Red.ToString(),
                IsHost = true,
            };
            bot.World.PlayersByClientId[-1] = new PlayerState
            {
                ClientId = -1,
                PlayerId = 255,
                Name = "Placeholder",
            };

            var surface = new CommandSurface(bot, vault);
            var players = surface.GetPlayersSnapshot();

            Assert.Equal(2, players.Count);
            Assert.Equal("Alice", players[0].Name);
            Assert.False(players[0].IsBot);
            Assert.True(players[0].IsHost);
            Assert.Equal("BotZero", players[1].Name);
            Assert.True(players[1].IsBot);

            var bots = surface.GetBotsSnapshot();
            Assert.Single(bots);
            Assert.Equal(10, bots[0].LocalClientId);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FollowPersistsAcrossStartGame_RespectsIntroSettle_AndSendsUnreliableSyncedTransforms()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-xform-follow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            var unreliableTransforms = new List<CustomNetworkTransformUpdate>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 1) continue;

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        if (data.Length < 3) continue;
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is JoinGameMessage jg)
                            {
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new JoinedGameMessage(jg.GameCode, 1, 2, Array.Empty<JoinedPlayerEntry>()),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                        }
                    }
                    else if (opt == UdpSendOption.Normal && data.Length > 1)
                    {
                        var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(1).ToArray());
                        if (root is GameDataMessage gdm)
                        {
                            foreach (var sub in GameDataSubMessage.ParseAll(gdm.RawSubMessages))
                            {
                                if (sub.Kind == GameDataKind.Data &&
                                    sub.TryParseData(out var targetNetId, out var compData) &&
                                    targetNetId == 103u)
                                {
                                    using var r = MessageReader.Get(compData);
                                    var upd = CustomNetworkTransformUpdate.Deserialize(r, initialState: false);
                                    lock (unreliableTransforms)
                                    {
                                        unreliableTransforms.Add(upd);
                                    }
                                }
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-xform\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19995", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            await using var bot = new BotClient(profile, vault, new DeviceAuthSource(vault, http), new MatchmakerClient(http))
            {
                DeterministicManualTick = true,
            };

            await bot.JoinAsync("FOLLOW", "bot-follow-sync", "BeanBot", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("FOLLOW"), 1, 2, Array.Empty<JoinedPlayerEntry>()));

            using var localXformInit = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, [new Vector2(0f, 1.5f)]).Serialize(localXformInit, initialState: true);
            using var targetXformInit = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, [new Vector2(2.5f, 1.5f)]).Serialize(targetXformInit, initialState: true);

            var code = GameCode.From("FOLLOW");
            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll([
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.PlayerControl, 1, 1, [
                    new SpawnComponentEntry(101, ComponentDataCodec.SerializePlayerControlInitial(1)),
                    new SpawnComponentEntry(102, Array.Empty<byte>()),
                    new SpawnComponentEntry(103, localXformInit.ToByteArray(includeHeader: false)),
                ])),
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.PlayerControl, 2, 1, [
                    new SpawnComponentEntry(201, ComponentDataCodec.SerializePlayerControlInitial(0)),
                    new SpawnComponentEntry(202, Array.Empty<byte>()),
                    new SpawnComponentEntry(203, targetXformInit.ToByteArray(includeHeader: false)),
                ])),
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.PlayerInfo, -2, 0, [
                    new SpawnComponentEntry(302, ComponentDataCodec.SerializePlayerInfoInitial(0, 2, "Beesmart", ColorType.Blue)),
                ])),
            ])));

            // Lobby follow -> emits unreliable (UdpSendOption.Normal) TransformUpdate with seq > spawn SequenceId(1).
            var surface = new CommandSurface(bot, vault);
            var followRes = await surface.ExecuteAsync("follow Beesmart");
            Assert.True(followRes.Ok);
            await bot.RunMatchTickAsync(0.2);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (unreliableTransforms)
                {
                    if (unreliableTransforms.Count > 0) break;
                }
                await Task.Delay(20);
            }

            lock (unreliableTransforms)
            {
                Assert.NotEmpty(unreliableTransforms);
                Assert.True(unreliableTransforms[0].SequenceId > 1, $"Expected seq > spawn sid(1), got {unreliableTransforms[0].SequenceId}");
                unreliableTransforms.Clear();
            }

            // StartGame + SkeldShipStatus + SnapToRpc(minSequenceId = 15) -> follow target MUST persist & wait out intro settle!
            await bot.ProcessInboundRootMessageAsync(new StartGameMessage(code));
            Assert.Equal("Beesmart", bot.BehaviorState.FollowTargetName);

            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll([
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.SkeldShipStatus, -2, 0, [
                    new SpawnComponentEntry(400, Array.Empty<byte>()),
                ])),
                GameDataSubMessage.FromRpc(103, new SnapToRpc(new Vector2(-2.0f, -2.5f), 15)),
                GameDataSubMessage.FromRpc(203, new SnapToRpc(new Vector2(2.0f, -2.5f), 15)),
            ])));

            // During default intro settle (4.5s), bot holds position.
            var posBeforeSettle = bot.World.LocalPlayer!.Position;
            await bot.RunMatchTickAsync(1.0);
            Assert.Equal(posBeforeSettle, bot.World.LocalPlayer.Position);

            // Finish settling + step 0.6s -> moves toward Beesmart and emits unreliable transform with seq > 15.
            await bot.RunMatchTickAsync(4.0);
            for (var i = 0; i < 6; i++)
            {
                await bot.RunMatchTickAsync(0.1);
            }

            deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (unreliableTransforms)
                {
                    if (unreliableTransforms.Count > 0) break;
                }
                await Task.Delay(20);
            }

            Assert.True(Vector2.Distance(bot.World.LocalPlayer.Position, new Vector2(2.0f, -2.5f)) <
                        Vector2.Distance(posBeforeSettle, new Vector2(2.0f, -2.5f)));
            lock (unreliableTransforms)
            {
                Assert.NotEmpty(unreliableTransforms);
                Assert.All(unreliableTransforms, u => Assert.True(u.SequenceId > 15, $"Expected seq > SnapTo minSid(15), got {u.SequenceId}"));
            }

            await bot.StopAsync();
            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MultiStepTask_RespectsDefault2500msDwell_AndSendsCompleteTaskRpcOnceOnFinalStep()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-multitask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            var completeTaskRpcs = new List<CompleteTaskRpc>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 3) continue;

                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Reliable)
                        {
                            var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is JoinGameMessage jg)
                            {
                                var respPayload = ProtocolRegistry.Default.EncodeRoot(
                                    new JoinedGameMessage(jg.GameCode, 1, 2, Array.Empty<JoinedPlayerEntry>()),
                                    MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                            else if (root is GameDataMessage gdm)
                            {
                                foreach (var sub in GameDataSubMessage.ParseAll(gdm.RawSubMessages))
                                {
                                    if (sub.Kind == GameDataKind.Rpc &&
                                        sub.TryParseRpc(ProtocolRegistry.Default, out _, out var rpc) &&
                                        rpc is CompleteTaskRpc ctr)
                                    {
                                        lock (completeTaskRpcs)
                                        {
                                            completeTaskRpcs.Add(ctr);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-task\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19994", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            await using var bot = new BotClient(profile, vault, new DeviceAuthSource(vault, http), new MatchmakerClient(http))
            {
                DeterministicManualTick = true,
            };

            Assert.Equal(2.5, bot.BehaviorState.TaskDwellDurationSec);
            Assert.Equal(4.5, bot.BehaviorState.IntroSettleDurationSec);
            bot.BehaviorState.IntroSettleDurationSec = 0;

            await bot.JoinAsync("TASKAB", "bot-multitask", "BeanBot", Path.Combine(dir, "replays"), Path.Combine(dir, "locks"));
            await bot.ProcessInboundRootMessageAsync(new JoinedGameMessage(GameCode.From("TASKAB"), 1, 2, Array.Empty<JoinedPlayerEntry>()));
            var code = GameCode.From("TASKAB");
            await bot.ProcessInboundRootMessageAsync(new StartGameMessage(code));
            await bot.ProcessInboundRootMessageAsync(new GameDataMessage(code, GameDataSubMessage.SerializeAll([
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.PlayerControl, 1, 1, [
                    new SpawnComponentEntry(101, ComponentDataCodec.SerializePlayerControlInitial(1)),
                    new SpawnComponentEntry(102, Array.Empty<byte>()),
                    new SpawnComponentEntry(103, Array.Empty<byte>()),
                ])),
                GameDataSubMessage.FromSpawn(new SpawnObjectPacket(SpawnObjectId.SkeldShipStatus, -2, 0, [
                    new SpawnComponentEntry(400, Array.Empty<byte>()),
                ])),
                GameDataSubMessage.FromRpc(101, new SetRoleRpc(RoleTypes.Crewmate)),
            ])));

            // Two-step task inside Cafeteria South: Step 0 right at bot position, Step 1 nearby.
            var step0 = new Vector2(-2.0f, -2.5f);
            var step1 = new Vector2(1.5f, -2.5f);
            bot.World.UpdatePlayerPosition(1, step0);
            bot.World.LocalTasks.Clear();
            bot.World.LocalTasks.Add(new BeanBot.World.AssignedTask
            {
                TaskId = 7,
                TaskTypeName = "FuelEngines",
                Room = "Cafeteria",
                StepPositions = [step0, step1],
            });

            // Tick 1 starts 2.5s dwell; 1.0s < 2.5s -> step 0 NOT finished yet.
            await bot.RunMatchTickAsync(0.1);
            await bot.RunMatchTickAsync(1.0);
            Assert.Equal(0, bot.World.LocalTasks[0].CurrentStep);
            Assert.Equal(0, bot.Counters.TaskCompletions);

            // Finish remaining 1.6s dwell on step 0 -> advances to CurrentStep=1 WITHOUT sending CompleteTaskRpc!
            await bot.RunMatchTickAsync(1.6);
            Assert.Equal(1, bot.World.LocalTasks[0].CurrentStep);
            Assert.False(bot.World.LocalTasks[0].IsCompleted);
            Assert.Equal(0, bot.Counters.TaskCompletions);
            await Task.Delay(100);
            lock (completeTaskRpcs)
            {
                Assert.Empty(completeTaskRpcs);
            }

            // Walk to step 1 and dwell 2.5s -> completes task AND emits CompleteTaskRpc(7) exactly once!
            for (var i = 0; i < 60 && !bot.World.LocalTasks[0].IsCompleted; i++)
            {
                await bot.RunMatchTickAsync(0.2);
            }

            Assert.True(bot.World.LocalTasks[0].IsCompleted);
            Assert.Equal(1, bot.Counters.TaskCompletions);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (completeTaskRpcs)
                {
                    if (completeTaskRpcs.Count > 0) break;
                }
                await Task.Delay(20);
            }

            lock (completeTaskRpcs)
            {
                var rpc = Assert.Single(completeTaskRpcs);
                Assert.Equal(7u, rpc.TaskId);
            }

            await bot.StopAsync();
            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void AStar_SmoothWithFunnel_TakesInnerCornerInCafeteria_WithoutWallDetour()
    {
        var mesh = BeanBot.Nav.NavMesh.LoadSkeld();

        // Reproduce ARLJFG bot-04 spawn (-2.23, -0.20) in CafeSouth walking to Beesmart at (2.44, 2.82) in CafeEast.
        var path1 = BeanBot.Nav.AStar.FindPath(mesh, new Vector2(-2.23f, -0.20f), new Vector2(2.44f, 2.82f));
        Assert.True(path1.Found);
        Assert.All(path1.Waypoints, wp =>
        {
            Assert.True(wp.X > -3.0f, $"Funnel detoured to outer west wall: ({wp.X:F2}, {wp.Y:F2})");
            Assert.True(wp.X < 3.0f, $"Funnel detoured to outer east wall: ({wp.X:F2}, {wp.Y:F2})");
        });

        // Reproduce ARLJFG bot-00 spawn (2.67, 0.68) in CafeEast walking to Beesmart at (2.44, 2.82) in CafeEast.
        var path2 = BeanBot.Nav.AStar.FindPath(mesh, new Vector2(2.67f, 0.68f), new Vector2(2.44f, 2.82f));
        Assert.True(path2.Found);
        Assert.Equal(2, path2.Waypoints.Count);
    }

    [Fact]
    public void NavMesh_SnapToNavMesh_ProjectsToNearestBoundaryEdge_NotDistantCentroid()
    {
        var mesh = BeanBot.Nav.NavMesh.LoadSkeld();

        // Point inside the central Cafeteria table hole near its top edge (-0.5, 1.7) should snap to y ~= 1.85,
        // not to CafeNorth's centroid (-0.85, 3.23).
        var (snapped, poly, wasSnapped) = mesh.SnapToNavMesh(new Vector2(-0.5f, 1.7f));
        Assert.True(wasSnapped);
        Assert.NotNull(mesh.FindContainingPolygon(snapped));
        Assert.True(Vector2.Distance(snapped, new Vector2(-0.5f, 1.8f)) < 0.25f,
            $"Expected snap near (-0.5, 1.8), got ({snapped.X:F2}, {snapped.Y:F2}) in {poly.Name}");
    }

    [Fact]
    public void TaskLoop_StickyActiveTask_DoesNotOscillateAcrossPolygonBoundaries()
    {
        var mesh = BeanBot.Nav.NavMesh.LoadSkeld();
        var world = new WorldModel { LocalClientId = 1, LocalPlayerId = 0 };
        world.TransitionPhase(GamePhase.Dropship, "Start", null);
        world.TransitionPhase(GamePhase.Tasks, "ShipStatus", null);
        world.PlayersByClientId[1] = new PlayerState
        {
            ClientId = 1,
            PlayerId = 0,
            Name = "bot-00",
            IsAlive = true,
            Position = new Vector2(-2.1f, -0.5f),
        };
        world.LocalTasks.Add(new AssignedTask
        {
            TaskId = 1,
            TaskTypeName = "FixWiring",
            Room = "Electrical",
            StepPositions = [new Vector2(-7.5f, -8.5f)],
        });
        world.LocalTasks.Add(new AssignedTask
        {
            TaskId = 2,
            TaskTypeName = "UploadData",
            Room = "Weapons",
            StepPositions = [new Vector2(8.8f, 3.2f)],
        });

        var state = new BeanBot.Behaviors.BotBehaviorState();
        var actions1 = BeanBot.Behaviors.TaskLoop.Evaluate(world, state, mesh, 0.1);
        var move1 = Assert.IsType<BeanBot.Behaviors.MoveToAction>(Assert.Single(actions1));
        var chosenTaskId = state.ActiveTaskId;
        Assert.NotNull(chosenTaskId);

        // Move slightly toward the other task so raw cost would flip if not sticky.
        world.PlayersByClientId[1].Position = new Vector2(1.5f, 1.9f);
        var actions2 = BeanBot.Behaviors.TaskLoop.Evaluate(world, state, mesh, 0.1);
        var move2 = Assert.IsType<BeanBot.Behaviors.MoveToAction>(Assert.Single(actions2));
        Assert.Equal(chosenTaskId, state.ActiveTaskId);
        Assert.Equal(move1.Destination, move2.Destination);
    }

    [Fact]
    public async Task JoinGameMessage_ServerDisconnectReason_RoundTrips_And_FleetFailsFastWithoutReconnectStorm()
    {
        var customReject = new JoinGameMessage(
            new GameCode((int)DisconnectReason.Custom),
            ServerDisconnectReason: DisconnectReason.Custom,
            ServerCustomMessage: "Free Chat lobbies require permission.");
        var encoded = Registry.EncodeRoot(customReject);
        var decoded = Assert.IsType<JoinGameMessage>(Registry.DecodeRoot(encoded));
        Assert.Equal(DisconnectReason.Custom, decoded.ServerDisconnectReason);
        Assert.Equal("Free Chat lobbies require permission.", decoded.ServerCustomMessage);

        var dir = Path.Combine(Path.GetTempPath(), $"beanbot-joinreject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var serverUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var serverEp = (IPEndPoint)serverUdp.Client.LocalEndPoint!;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            ushort serverReliableSeq = 1;
            var joinGameAttempts = 0;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var serverLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult recv;
                    try
                    {
                        recv = await serverUdp.ReceiveAsync(cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    var data = recv.Buffer;
                    if (data.Length < 3) continue;
                    var opt = (UdpSendOption)data[0];
                    if (opt is UdpSendOption.Hello or UdpSendOption.Reliable or UdpSendOption.Ping)
                    {
                        var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                        await serverUdp.SendAsync(ack, ack.Length, recv.RemoteEndPoint);

                        if (opt == UdpSendOption.Reliable)
                        {
                            var root = Registry.DecodeRoot(data.AsSpan(3).ToArray());
                            if (root is JoinGameMessage)
                            {
                                Interlocked.Increment(ref joinGameAttempts);
                                var respPayload = Registry.EncodeRoot(customReject, MessageType.Reliable);
                                var pkt = new byte[3 + respPayload.Length];
                                var seq = serverReliableSeq++;
                                pkt[0] = (byte)UdpSendOption.Reliable;
                                pkt[1] = (byte)(seq >> 8);
                                pkt[2] = (byte)(seq & 0xff);
                                Buffer.BlockCopy(respPayload, 0, pkt, 3, respPayload.Length);
                                await serverUdp.SendAsync(pkt, pkt.Length, recv.RemoteEndPoint);
                            }
                        }
                    }
                }
            });

            var handler = new StubHttpHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/api/user")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok-reject\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games", StringComparison.OrdinalIgnoreCase))
                {
                    var json = JsonSerializer.Serialize(new { Status = 1, Ip = ipUint, Port = (ushort)serverEp.Port });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:19991", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "test-pass");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var fleet = new Fleet(
                profile,
                vault,
                auth,
                matchmaker,
                roster: Roster.CreateJoiners(3),
                timing: OrchestratorTimingConfig.FastLocal,
                replayDirectory: Path.Combine(dir, "replays"),
                locksDirectory: Path.Combine(dir, "locks"));

            var ex = await Assert.ThrowsAsync<RoomLookupException>(() =>
                fleet.StartJoinLobbyAsync("RWBPAF", deterministicManualTick: false));
            Assert.Contains("Free Chat lobbies require permission", ex.Message);
            Assert.Equal(1, joinGameAttempts);

            cts.Cancel();
            await serverLoop;
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class CachedAuthStub : IDeviceAuthSource
    {
        public Task<AuthSessionResult> AuthenticateAsync(
            AccountRecord account,
            ServerProfile profile,
            CancellationToken ct = default) =>
            Task.FromResult(new AuthSessionResult(
                account,
                account.CachedToken ?? "tok",
                account.Puid ?? $"puid-{account.AccountId}",
                account.DisplayName,
                account.FriendCode ?? string.Empty));
    }

    private sealed class RecordingHostBridge(GameCode gameCode) : IHostTransportBridge
    {
        public GameCode GameCode { get; } = gameCode;
        public WorldModel World { get; } = new();
        public uint? NormalGameManagerNetId => 99u;
        public Dictionary<int, uint> PlayerInfoNetIds { get; } = [];
        public List<(uint TargetNetId, IRpcMessage Rpc)> SentRpcs { get; } = [];
        public List<IRootMessage> SentRoots { get; } = [];

        public uint? GetPlayerInfoNetId(int clientId) =>
            PlayerInfoNetIds.TryGetValue(clientId, out var id) ? id : null;

        public Task SendRootAsync(IRootMessage root, CancellationToken ct = default)
        {
            SentRoots.Add(root);
            return Task.CompletedTask;
        }

        public Task SendRpcAsync(uint targetNetId, IRpcMessage rpc, CancellationToken ct = default)
        {
            SentRpcs.Add((targetNetId, rpc));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTransport(byte[] inboundDatagram) : ITransport
    {
        private bool _sentOnce;
        public bool IsConnected => true;
        public bool UsesDtls => false;
        public System.Net.IPEndPoint? RemoteEndPoint => null;
        public Task ConnectAsync(System.Net.IPEndPoint endPoint, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ReceiveDatagramAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sentOnce)
            {
                return Task.FromResult(0);
            }

            _sentOnce = true;
            inboundDatagram.CopyTo(buffer.Span);
            return Task.FromResult(inboundDatagram.Length);
        }
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
