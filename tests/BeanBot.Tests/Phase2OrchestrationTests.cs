using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BeanBot.Accounts;
using BeanBot.Commands;
using BeanBot.Host;
using BeanBot.Matchmaker;
using BeanBot.Nav;
using BeanBot.Observability;
using BeanBot.Orchestrator;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.Maps;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using BeanBot.Transport;
using BeanBot.World;
using Xunit;

namespace BeanBot.Tests;

public sealed class Phase2OrchestrationTests
{
    [Fact]
    public async Task P2_0_Roster_15PersistedDeviceIds_AndAbortsOnPuidCollision()
    {
        var dir = CreateTempDir();
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "p2-test-pass");
        using var http = new HttpClient();
        var auth = new DeviceAuthSource(vault, http);
        var profile = ServerProfile.Local();
        var roster = Roster.CreateDefault(14);
        var counters = new Counters();

        // DeviceIds must be persisted BEFORE any login (G6, P2.0).
        var preLogin = roster.EnsurePersistedAccounts(vault);
        Assert.Equal(15, preLogin.Count);
        Assert.Equal(15, preLogin.Select(a => a.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(preLogin, a => Assert.True(Guid.TryParse(a.DeviceId, out _)));

        // Re-opening vault must yield identical DeviceIds (never regenerated).
        var reopened = new Vault(Path.Combine(dir, "accounts.json.age"), "p2-test-pass");
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(preLogin[i].DeviceId, reopened.Get(preLogin[i].AccountId)!.DeviceId);
        }

        // Authenticate all 15 and verify 15 distinct PUIDs.
        var sessions = await roster.AuthenticateAndVerifyDistinctPuidsAsync(
            vault, auth, profile, TimeSpan.Zero, counters);
        Assert.Equal(15, sessions.Count);
        Assert.Equal(15, sessions.Select(s => s.Puid).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, counters.PuidCollisions);

        // Simulate PUID collision from buggy auth source -> must abort loudly & increment counter.
        var collidingAuth = new CollidingDeviceAuthStub(vault, collidingAccountId: "bot-09", forcedPuid: "puid-bot-00");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            roster.AuthenticateAndVerifyDistinctPuidsAsync(vault, collidingAuth, profile, TimeSpan.Zero, counters));
        Assert.True(counters.PuidCollisions >= 1);
    }

    [Fact]
    public void P2_3_EmbeddedPalettes_Assigns15UniqueAppendixAColorsAndNames()
    {
        var palette = ColorPalette.AppendixAOrder;
        Assert.Equal(18, palette.Count);
        Assert.Equal(
            new[]
            {
                ColorType.Red, ColorType.Blue, ColorType.Green, ColorType.Pink, ColorType.Orange, ColorType.Yellow,
                ColorType.Black, ColorType.White, ColorType.Purple, ColorType.Brown, ColorType.Cyan, ColorType.Lime,
                ColorType.Coral, ColorType.Rose, ColorType.Banana, ColorType.Gray, ColorType.Tan, ColorType.Maroon,
            },
            palette);

        var roster = Roster.CreateDefault(14);
        Assert.Equal(15, roster.Seats.Count);
        Assert.True(roster.HostSeat.IsHost);
        Assert.Equal(ColorType.Red, roster.HostSeat.Color);
        Assert.Equal(15, roster.Seats.Select(s => s.Color).Distinct().Count());
        Assert.Equal(15, roster.Seats.Select(s => s.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(15, roster.Seats.Select(s => s.AccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Cap at 14 joiners (15 total).
        Assert.Throws<ArgumentOutOfRangeException>(() => Roster.CreateDefault(15));
    }

    [Fact]
    public async Task P2_1_HostController_Options_PrivateThenPublic_KickUnexpected_AndEndOnAbandonment()
    {
        var world = new WorldModel { LocalClientId = 1, HostClientId = 1 };
        world.PlayersByClientId[1] = new PlayerState { ClientId = 1, PlayerId = 0, Name = "Bean00", Puid = "p-00", IsHost = true, IsReady = true };

        var bridge = new RecordingHostBridge(GameCode.From("TESTAA"), world, gameManagerNetId: 500);
        var counters = new Counters();
        var options = new HostLobbyOptions
        {
            ExpectedJoiners = 2,
            PublicWhenSeated = true,
            AutoStartWhenReady = false,
            AutoEndOnAbandonment = true,
            AbandonmentThreshold = 1,
        };
        var host = new HostController(bridge, options, counters);
        host.SetExpectedRoster(new[]
        {
            new ExpectedRosterSeat("p-00", "Bean00", ColorType.Red, IsHost: true),
            new ExpectedRosterSeat("p-01", "Bean01", ColorType.Blue),
            new ExpectedRosterSeat("p-02", "Bean02", ColorType.Green),
        });

        await host.InitializeLobbyAsync();
        Assert.Equal(HostLifecycleState.PrivateSeating, host.State);
        Assert.Contains(bridge.SentRoots, r => r is AlterGameMessage a && !a.IsPublic);
        Assert.Contains(bridge.SentRpcs, r => r.Rpc is SyncSettingsRpc s && s.Options.Map == MapTypes.Skeld && s.Options.NumImpostors == 1);

        // Unexpected intruder joins while private -> must be kicked.
        world.PlayersByClientId[2] = new PlayerState { ClientId = 2, PlayerId = 1, Name = "Bean01", Puid = "p-01", IsReady = true };
        world.PlayersByClientId[99] = new PlayerState { ClientId = 99, PlayerId = 2, Name = "Stranger", Puid = "p-stranger", IsReady = true };
        await host.TickAsync();

        Assert.Contains(bridge.SentRoots, r => r is KickPlayerMessage k && k.PlayerClientId == 99 && !k.IsBan);
        Assert.Equal(1, counters.Kicks);
        Assert.Equal(HostLifecycleState.PrivateSeating, host.State);

        // Stranger removed, 2nd expected joiner arrives -> 3/3 seats held -> flips public.
        world.PlayersByClientId.TryRemove(99, out _);
        world.PlayersByClientId[3] = new PlayerState { ClientId = 3, PlayerId = 3, Name = "Bean02", Puid = "p-02", NetId = 123, IsReady = true };
        await host.EnforceSeatIdentityAsync(3, 123);
        Assert.Contains(bridge.SentRpcs, r => r.NetId == 123 && r.Rpc is SetNameRpc sn && sn.Name == "Bean02");
        Assert.Contains(bridge.SentRpcs, r => r.NetId == 123 && r.Rpc is SetColorRpc sc && sc.Color == ColorType.Green);

        await host.TickAsync();
        Assert.Equal(HostLifecycleState.PublicOpen, host.State);
        Assert.Contains(bridge.SentRoots, r => r is AlterGameMessage a && a.IsPublic);

        // Start game -> simulate abandonment (only host remains) -> EndGame emitted.
        await host.StartGameAsync();
        Assert.Equal(HostLifecycleState.InMatch, host.State);
        world.TransitionPhase(GamePhase.Tasks, "Test", null);
        world.PlayersByClientId.TryRemove(2, out _);
        world.PlayersByClientId.TryRemove(3, out _);
        await host.TickAsync();

        Assert.Equal(HostLifecycleState.Ended, host.State);
        Assert.Contains(bridge.SentRoots, r => r is EndGameMessage e && e.Reason == GameOverReason.CrewmateDisconnect);
    }

    [Fact]
    public async Task P2_4_StartGate_Requires14ReadyAndSteady3sDwell_ResetsOnDrop()
    {
        var now = DateTimeOffset.UtcNow;
        var world = new WorldModel { LocalClientId = 1, HostClientId = 1 };
        world.PlayersByClientId[1] = new PlayerState { ClientId = 1, PlayerId = 0, Name = "Bean00", Puid = "p-00", IsHost = true, IsReady = true };
        for (var i = 1; i <= 14; i++)
        {
            world.PlayersByClientId[1 + i] = new PlayerState
            {
                ClientId = 1 + i,
                PlayerId = (byte)i,
                Name = $"Bean{i:D2}",
                Puid = $"p-{i:D2}",
                IsReady = true,
            };
        }

        var bridge = new RecordingHostBridge(GameCode.From("GATEOK"), world, gameManagerNetId: 400);
        var host = new HostController(
            bridge,
            new HostLobbyOptions { ExpectedJoiners = 14, StartGateDwell = TimeSpan.FromSeconds(3), AutoStartWhenReady = true },
            utcNow: () => now);
        await host.InitializeLobbyAsync();

        // T=0: 14 ready -> enters StartCountdown, does NOT start yet.
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.StartCountdown, host.State);
        Assert.DoesNotContain(bridge.SentRoots, r => r is StartGameMessage);

        // T=2.5s: still holding, not yet 3s.
        now = now.AddSeconds(2.5);
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.StartCountdown, host.State);
        Assert.DoesNotContain(bridge.SentRoots, r => r is StartGameMessage);

        // One joiner drops -> dwell resets.
        world.PlayersByClientId[15].IsReady = false;
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.PrivateSeating, host.State);
        Assert.Null(host.CountdownStartedAtUtc);

        // Re-readies at T=3.0s -> needs a fresh 3.0s hold (starts at T=6.0s).
        now = now.AddSeconds(0.5);
        world.PlayersByClientId[15].IsReady = true;
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.StartCountdown, host.State);

        now = now.AddSeconds(2.9);
        await host.TickAsync();
        Assert.DoesNotContain(bridge.SentRoots, r => r is StartGameMessage);

        now = now.AddSeconds(0.2);
        await host.TickAsync();
        Assert.Equal(HostLifecycleState.InMatch, host.State);
        Assert.Contains(bridge.SentRoots, r => r is StartGameMessage s && s.GameCode.Code == "GATEOK");
    }

    [Fact]
    public async Task P2_2_SeatFiller_SingleFlight_AndCircuitBreakerAfter3Failures()
    {
        var dir = CreateTempDir();
        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "p2-sf-pass");
        var counters = new Counters();
        var timing = OrchestratorTimingConfig.FastLocal with { ReconnectMaxDelay = TimeSpan.FromSeconds(30) };

        // Bind dead port / failing matchmaker to verify circuit breaker trips after 3 consecutive failures.
        var failHandler = new StubHttp((_) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var mmHttp = new HttpClient(failHandler);
        var auth = new DeviceAuthSource(vault, mmHttp);
        var matchmaker = new MatchmakerClient(mmHttp, maxRetries: 1, delayAsync: (_, _) => Task.CompletedTask);
        var profile = ServerProfile.Private("http://127.0.0.1:19991", useDtls: false, useDeviceAuth: false);

        var roster = Roster.CreateDefault(1);
        roster.EnsurePersistedAccounts(vault);

        var supervisor = new Supervisor(
            vault,
            timing,
            counters,
            Path.Combine(dir, "replays"),
            Path.Combine(dir, "locks"),
            _ => Task.FromResult("NEWLOB"),
            logger: null);

        await using var bot1 = new BotClient(profile, vault, auth, matchmaker);
        var slot = new ManagedBotSlot(roster.JoinerSeats[0], bot1);
        supervisor.RegisterSlot(slot);

        // Launch two concurrent single-flight joins on the same seat -> only 1 executes, the other rejects cleanly (F9).
        var t1 = supervisor.TrySeatSingleFlightAsync(slot, "ABCDEF", rejectIfInFlight: true);
        var t2 = supervisor.TrySeatSingleFlightAsync(slot, "ABCDEF", rejectIfInFlight: true);
        var results = await Task.WhenAll(t1, t2);
        Assert.All(results, r => Assert.False(r)); // Neither succeeded (failing matchmaker), one rejected immediately.
        Assert.True(slot.ConsecutiveFailures is 1 or 2);

        // Drain up to 3 consecutive failures -> circuit breaker opens and account enters Cooling quarantine.
        while (!slot.CircuitOpen)
        {
            await supervisor.TrySeatSingleFlightAsync(slot, "ABCDEF", rejectIfInFlight: false);
        }

        Assert.True(slot.CircuitOpen);
        Assert.Equal(BotSlotHealth.Quarantined, slot.Health);
        Assert.True(slot.ConsecutiveFailures >= Supervisor.CircuitBreakerFailureThreshold);
        Assert.Equal(AccountState.Cooling, vault.Get(slot.Seat.AccountId)!.State);
    }

    [Fact]
    public async Task P2_5_Fleet15Bots_SelfHostedLobby_3BotKillsIsolation_FullMatch_AndHostRebuildUnder60s()
    {
        var dir = CreateTempDir();
        var replayDir = Path.Combine(dir, "replays");
        var locksDir = Path.Combine(dir, "locks");
        Directory.CreateDirectory(replayDir);
        Directory.CreateDirectory(locksDir);

        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "p2-fleet-pass");
        var roster = Roster.CreateDefault(14);
        roster.EnsurePersistedAccounts(vault);

        await using var relay = new MultiClientLoopbackRelay(roster, vault);
        relay.Start();

        using var mmHttp = new HttpClient(relay.MatchmakerHandler);
        var auth = new DeviceAuthSource(vault, mmHttp);
        var matchmaker = new MatchmakerClient(mmHttp, maxRetries: 2, delayAsync: (_, _) => Task.CompletedTask);
        var profile = ServerProfile.Private("http://127.0.0.1:19992", useDtls: false, useDeviceAuth: false);
        var timing = OrchestratorTimingConfig.FastLocal with
        {
            StartGateDwell = TimeSpan.FromMilliseconds(200),
            RetryMinDelay = TimeSpan.FromMilliseconds(20),
            RetryMaxDelay = TimeSpan.FromMilliseconds(50),
            HostRebuildDeadline = TimeSpan.FromSeconds(30),
        };
        var hostOptions = new HostLobbyOptions
        {
            PublicWhenSeated = true,
            AutoStartWhenReady = false, // We trigger start after verifying 15/15 seats & 3-bot kill isolation
            StartGateDwell = timing.StartGateDwell,
            KickUnexpectedClients = true,
        };

        await using var fleet = new Fleet(
            profile,
            vault,
            auth,
            matchmaker,
            roster: roster,
            hostOptions: hostOptions,
            timing: timing,
            replayDirectory: replayDir,
            locksDirectory: locksDir);

        // 1. Start 15-bot self-hosted lobby (1 host + 14 joiners).
        var firstCode = await fleet.StartHostLobbyAsync();
        Assert.False(string.IsNullOrWhiteSpace(firstCode));

        // Wait until Host sees 15 players in World & lobby flips Public (private -> public after seating).
        var hostBot = fleet.ResolveBot("bot-00")!;
        await SpinWaitUntilAsync(() =>
            fleet.GetStatusSnapshot().ConnectedCount == 15 &&
            hostBot.World.PlayersByClientId.Count == 15 &&
            hostBot.World.PlayersByClientId.Values.All(p => !string.IsNullOrWhiteSpace(p.Puid) && !string.IsNullOrWhiteSpace(p.ColorName)) &&
            fleet.HostController?.State == HostLifecycleState.PublicOpen,
            timeoutMs: 12_000,
            describeFailure: () =>
            {
                var snap = fleet.GetStatusSnapshot();
                var hostPlayers = hostBot.World.PlayersByClientId.Values.Select(p => $"{p.ClientId}:{p.Name}:puid={p.Puid}:color={p.ColorName}").ToArray();
                return $"Connected={snap.ConnectedCount}, HostWorldCount={hostPlayers.Length}, HostState={snap.HostState}, Players=[{string.Join("; ", hostPlayers)}]";
            });

        var initialSnap = fleet.GetStatusSnapshot();
        Assert.Equal(15, initialSnap.SeatsHeld);
        Assert.True(initialSnap.HostHealthy);
        Assert.Equal(HostLifecycleState.PublicOpen, initialSnap.HostState);

        // Confirm 15 distinct PUIDs and 15 distinct colours in Host's World / GameData.
        var hostPlayers = hostBot.World.PlayersByClientId.Values.ToArray();
        Assert.Equal(15, hostPlayers.Select(p => p.Puid).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(15, hostPlayers.Select(p => p.ColorName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("Red", hostBot.World.LocalPlayer!.ColorName);

        // Verify Fleet CommandSurface + loopback HttpControlServer work against 15-bot fleet.
        var surface = new CommandSurface(fleet, vault);
        var statusCmd = await surface.ExecuteAsync("status");
        Assert.True(statusCmd.Ok);
        var sayCmd = await surface.ExecuteAsync("say bot-02 QuickChat_Hello");
        Assert.True(sayCmd.Ok);

        // 2. P2.5 Isolation Gate: Kill 3 joiner bots mid-lobby -> host + 12 others stay connected while 3 requeue!
        var killTargets = new[] { "bot-03", "bot-07", "bot-11" };
        await Task.WhenAll(killTargets.Select(id => fleet.Supervisor.KillBotAndRequeueAsync(id)));

        await SpinWaitUntilAsync(() => fleet.GetStatusSnapshot().ConnectedCount == 15, timeoutMs: 8_000);
        foreach (var slot in fleet.Supervisor.Slots)
        {
            if (killTargets.Contains(slot.Seat.AccountId, StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(slot.RestartCount >= 1);
            }
            else
            {
                Assert.Equal(0, slot.RestartCount); // Host + remaining 12 stayed connected untouched!
            }
        }

        // 3. P2.4 + Match Gate: All 14 joiners Ready -> StartGame -> Tasks -> Meeting -> Vote -> EndGame with 0 kicks & 0 host drops.
        await fleet.HostController!.StartGameAsync();
        await SpinWaitUntilAsync(
            () => fleet.Supervisor.Slots.All(s => s.Client.World.Phase == GamePhase.Dropship),
            timeoutMs: 6_000,
            describeFailure: () => string.Join(" | ", fleet.Supervisor.Slots.Select(s => $"{s.Seat.AccountId}:{s.Client.World.Phase}:conn={s.Client.IsConnected}:id={s.Client.World.LocalClientId}:err={s.LastError}")));

        // Broadcast SkeldShipStatus + 1 short task for every bot -> tasks progress -> Meeting -> Vote -> EndGame.
        await relay.BroadcastRoomStartSetupAsync(firstCode);
        await SpinWaitUntilAsync(
            () => fleet.Supervisor.Slots.All(s => s.Client.World.Phase == GamePhase.Tasks),
            timeoutMs: 6_000,
            describeFailure: () => string.Join(", ", fleet.Supervisor.Slots.Select(s => $"{s.Seat.AccountId}:{s.Client.World.Phase}")));

        await Task.Delay(350);
        await relay.BroadcastMeetingAndCompleteAsync(firstCode);
        await fleet.HostController.EndGameAsync(GameOverReason.CrewmatesByTask);
        await SpinWaitUntilAsync(
            () => fleet.Supervisor.Slots.All(s => s.Client.World.Phase == GamePhase.End),
            timeoutMs: 6_000,
            describeFailure: () => string.Join(", ", fleet.Supervisor.Slots.Select(s => $"{s.Seat.AccountId}:{s.Client.World.Phase}")));
        Assert.Equal(0, fleet.Counters.Kicks);

        // 4. P2.5 Host-First Rebuild Gate: kill Host -> host rebuilds new room and reseats 15/15 in < 60s!
        var sw = Stopwatch.StartNew();
        relay.CloseRoom(firstCode);
        await hostBot.StopAsync();
        var rebuiltCode = await fleet.Supervisor.HandleHostLostAndRebuildAsync();
        await SpinWaitUntilAsync(() => fleet.GetStatusSnapshot().ConnectedCount == 15, timeoutMs: 12_000);
        sw.Stop();

        Assert.NotEqual(firstCode, rebuiltCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"Host rebuild took {sw.Elapsed.TotalSeconds:F2}s (must be < 60s).");
        Assert.Equal(15, fleet.GetStatusSnapshot().ConnectedCount);
        Assert.True(fleet.GetStatusSnapshot().HostHealthy);
    }

    [Fact]
    public void P2_6_EmbeddedResources_SkeldNavAndPalettesLoadWithoutExternalFiles()
    {
        var originalCwd = Environment.CurrentDirectory;
        var emptyDir = CreateTempDir();
        try
        {
            Environment.CurrentDirectory = emptyDir;
            var mesh = NavMesh.LoadSkeld();
            Assert.NotNull(mesh);
            Assert.True(mesh.Polygons.Count >= 10);
            Assert.Equal(18, ColorPalette.AppendixAOrder.Count);
            Assert.Equal(15, ColorPalette.DefaultBotNames.Count);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "beanbot-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task SpinWaitUntilAsync(Func<bool> predicate, int timeoutMs = 5000, Func<string>? describeFailure = null)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                var detail = describeFailure is null ? string.Empty : $" State: {describeFailure()}";
                throw new TimeoutException($"Condition not satisfied within {timeoutMs}ms.{detail}");
            }

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task MultiBotJoinFleet_JoinsByRoomCode_AndBroadcastsFollowCommand()
    {
        var dir = CreateTempDir();
        var replayDir = Path.Combine(dir, "replays");
        var locksDir = Path.Combine(dir, "locks");
        Directory.CreateDirectory(replayDir);
        Directory.CreateDirectory(locksDir);

        var vault = new Vault(Path.Combine(dir, "accounts.json.age"), "p2-join-pass");
        var roster = Roster.CreateJoiners(3);
        roster.EnsurePersistedAccounts(vault);
        Assert.Equal(3, roster.Seats.Count);
        Assert.All(roster.Seats, s => Assert.False(s.IsHost));

        await using var relay = new MultiClientLoopbackRelay(roster, vault);
        relay.Start();
        relay.OpenExternalHostRoom("JOINBB", hostName: "Cliffshot", initialPosition: new Vector2(1.6f, 2.1f));

        using var mmHttp = new HttpClient(relay.MatchmakerHandler);
        var auth = new DeviceAuthSource(vault, mmHttp);
        var matchmaker = new MatchmakerClient(mmHttp, maxRetries: 2, delayAsync: (_, _) => Task.CompletedTask);
        var profile = ServerProfile.Private("http://127.0.0.1:19993", useDtls: false, useDeviceAuth: false);
        var timing = OrchestratorTimingConfig.FastLocal with
        {
            RetryMinDelay = TimeSpan.FromMilliseconds(20),
            RetryMaxDelay = TimeSpan.FromMilliseconds(50),
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

        var joinedCode = await fleet.StartJoinLobbyAsync("JOINBB");
        Assert.Equal("JOINBB", joinedCode);

        // Only the first bot asks the matchmaker; the other 2 reuse its resolved relay.
        Assert.Equal(1, relay.JoinLookupCount);

        var snap = fleet.GetStatusSnapshot();
        Assert.Equal("JOINBB", snap.LobbyCode);
        Assert.Equal(3, snap.TargetSeats);
        Assert.Equal(3, snap.ConnectedCount);
        Assert.Equal(3, snap.SeatsHeld);
        Assert.Equal(3, fleet.GetBots().Count);

        await SpinWaitUntilAsync(() =>
            fleet.GetBots().All(b =>
                b.World.LocalPlayer is not null &&
                b.World.PlayersByClientId.Values.Any(p => p.Name == "Cliffshot")),
            timeoutMs: 5_000);

        var firstBot = fleet.GetBots()[0];
        var before = firstBot.World.LocalPlayer!.Position;

        var surface = new CommandSurface(fleet, vault);
        var followRes = await surface.ExecuteAsync("follow Cliff");
        Assert.True(followRes.Ok);
        Assert.Contains("3/3", followRes.Message);
        Assert.Contains("Cliff", followRes.Message);

        // Wait until firstBot steps toward Cliffshot at (1.6, 2.1) in Lobby.
        await SpinWaitUntilAsync(() =>
        {
            var after = firstBot.World.LocalPlayer!.Position;
            return after.X > before.X + 0.1f && after.Y > before.Y + 0.05f;
        }, timeoutMs: 3_000);

        var clearRes = await surface.ExecuteAsync("follow all off");
        Assert.True(clearRes.Ok);
        Assert.Contains("Follow cleared on 3 bots", clearRes.Message);

        await fleet.StopAllAsync();
    }

    [Fact]
    public async Task MultiBotJoinFleet_FailsFastOnRoomNotFound_WithoutRetryingAllBots()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"beanbot-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var lookupCalls = 0;
            var handler = new StubHttp(req =>
            {
                var path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Equals("/api/user", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref lookupCalls);
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"Status\":3}", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:22999", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(tempDir, "accounts.json.age"), "test-key");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);
            var roster = Roster.CreateJoiners(5);

            await using var fleet = new Fleet(
                profile,
                vault,
                auth,
                matchmaker,
                roster: roster,
                timing: OrchestratorTimingConfig.FastLocal,
                replayDirectory: Path.Combine(tempDir, "replays"),
                locksDirectory: Path.Combine(tempDir, "locks"));

            var ex = await Assert.ThrowsAsync<RoomLookupException>(() =>
                fleet.StartJoinLobbyAsync("ZZZZZZ", deterministicManualTick: true));
            Assert.Equal(JoinLookupStatus.NotFound, ex.Status);
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Only bot-00 in wave 1 should perform a single lookup before aborting the ramp cleanly.
            Assert.Equal(1, lookupCalls);

            // Also verify invalid room code format rejects immediately before any HTTP lookup.
            var invalidEx = await Assert.ThrowsAsync<RoomLookupException>(() =>
                fleet.StartJoinLobbyAsync("BAD123", deterministicManualTick: true));
            Assert.Equal(JoinLookupStatus.Error, invalidEx.Status);
            Assert.Equal(1, lookupCalls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MultiBotJoinFleet_RateLimitedLookup_RetriesOnceThenFailsWithClearMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"beanbot-ratelimit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var lookupCalls = 0;
            var handler = new StubHttp(req =>
            {
                var path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Equals("/api/user", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("\"mm-tok\"", Encoding.UTF8, "application/json"),
                    };
                }

                if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref lookupCalls);
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"Errors\":[{\"Reason\":116}]}", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var profile = ServerProfile.Private("http://127.0.0.1:22998", useDtls: false, useDeviceAuth: false);
            var vault = new Vault(Path.Combine(tempDir, "accounts.json.age"), "test-key");
            using var http = new HttpClient(handler);
            var auth = new DeviceAuthSource(vault, http);
            var matchmaker = new MatchmakerClient(http);

            await using var fleet = new Fleet(
                profile,
                vault,
                auth,
                matchmaker,
                roster: Roster.CreateJoiners(5),
                timing: OrchestratorTimingConfig.FastLocal,
                replayDirectory: Path.Combine(tempDir, "replays"),
                locksDirectory: Path.Combine(tempDir, "locks"));

            var ex = await Assert.ThrowsAsync<RoomLookupException>(() =>
                fleet.StartJoinLobbyAsync("PLIHRG", deterministicManualTick: true));
            Assert.Equal(JoinLookupStatus.RateLimited, ex.Status);
            Assert.Contains("rate-limiting", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TooManyRequests (116)", ex.Message);

            // bot-00 does 1 lookup + 1 polite retry, then the ramp aborts so the other 4 bots never pile on.
            Assert.Equal(2, lookupCalls);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private sealed class CollidingDeviceAuthStub(Vault vault, string collidingAccountId, string forcedPuid) : IDeviceAuthSource
    {
        public Task<AuthSessionResult> AuthenticateAsync(AccountRecord account, ServerProfile profile, CancellationToken ct = default)
        {
            var puid = account.AccountId == collidingAccountId ? forcedPuid : $"puid-{account.AccountId}";
            account.Puid = puid;
            account.State = AccountState.LoggedIn;
            vault.SaveRecord(account);
            return Task.FromResult(new AuthSessionResult(account, "tok", puid, account.DisplayName, string.Empty, "idtok"));
        }
    }

    private sealed class RecordingHostBridge(GameCode code, WorldModel world, uint? gameManagerNetId) : IHostTransportBridge
    {
        public GameCode GameCode => code;
        public WorldModel World => world;
        public uint? NormalGameManagerNetId => gameManagerNetId;
        public List<IRootMessage> SentRoots { get; } = new();
        public List<(uint NetId, IRpcMessage Rpc)> SentRpcs { get; } = new();

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

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(fn(request));
    }

    /// <summary>
    /// Full in-memory multi-client Hazel/AmongUs UDP relay harness for deterministic 15-bot fleet verification.
    /// </summary>
    private sealed class MultiClientLoopbackRelay : IAsyncDisposable
    {
        private sealed class ClientSession
        {
            public required IPEndPoint EndPoint { get; init; }
            public int ClientId { get; init; }
            public string Name { get; set; } = string.Empty;
            public string Puid { get; set; } = string.Empty;
            public string RoomCode { get; set; } = string.Empty;
            public byte PlayerId { get; set; }
            public uint BaseNetId { get; init; }
            public ushort ReliableSeq { get; set; } = 1;
            public bool IsSyntheticHost { get; init; }
            public Vector2 InitialPosition { get; init; } = Vector2.Zero;
        }

        private sealed class RoomState
        {
            public required GameCode Code { get; init; }
            public int HostClientId { get; set; }
            public bool IsPublic { get; set; }
            public bool IsClosed { get; set; }
            public byte NextPlayerId { get; set; }
            public List<ClientSession> Members { get; } = new();
        }

        private readonly Roster _roster;
        private readonly Vault _vault;
        private readonly UdpClient _udp;
        private readonly IPEndPoint _ep;
        private readonly ConcurrentDictionary<string, ClientSession> _byEndpoint = new();
        private readonly ConcurrentDictionary<string, RoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private int _nextClientId = 100;
        private uint _nextNetId = 1000;
        private Task? _loop;
        private int _joinLookupCount;

        public int JoinLookupCount => Volatile.Read(ref _joinLookupCount);

        public MultiClientLoopbackRelay(Roster roster, Vault vault)
        {
            _roster = roster;
            _vault = vault;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            if (OperatingSystem.IsWindows())
            {
                const int SioUdpConnReset = -1744830452;
                _udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            _ep = (IPEndPoint)_udp.Client.LocalEndPoint!;
        }

        public HttpMessageHandler MatchmakerHandler => new StubHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var ipUint = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            if (path.EndsWith("/api/user", StringComparison.OrdinalIgnoreCase))
            {
                return JsonOk(new { token = "mm-tok" });
            }

            if (path.Equals("/api/games", StringComparison.OrdinalIgnoreCase) && req.Method == HttpMethod.Post)
            {
                return JsonOk(new { Ip = ipUint, Port = _ep.Port });
            }

            string? lookupCode = null;
            if (path.StartsWith("/api/games/", StringComparison.OrdinalIgnoreCase))
            {
                var segment = path["/api/games/".Length..].Trim('/');
                lookupCode = int.TryParse(segment, out var intCode) ? GameCode.From(intCode).Code : segment;
            }
            else if (req.RequestUri.Query.Contains("gameId=", StringComparison.OrdinalIgnoreCase))
            {
                var q = req.RequestUri.Query;
                lookupCode = q.Split("gameId=", StringSplitOptions.RemoveEmptyEntries).Last().Split('&')[0];
            }

            if (lookupCode is not null)
            {
                Interlocked.Increment(ref _joinLookupCount);
                lock (_gate)
                {
                    if (_rooms.TryGetValue(lookupCode, out var room) && !room.IsClosed)
                    {
                        return JsonOk(new { Status = 1, Ip = ipUint, Port = _ep.Port });
                    }
                }

                return JsonOk(new { Status = 3 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        public void Start() => _loop = Task.Run(RunAsync);

        public void OpenExternalHostRoom(string code, string hostName = "Cliffshot", Vector2 initialPosition = default)
        {
            lock (_gate)
            {
                var room = new RoomState
                {
                    Code = GameCode.From(code),
                    HostClientId = 1,
                    IsPublic = true,
                    NextPlayerId = 1,
                };
                room.Members.Add(new ClientSession
                {
                    EndPoint = _ep,
                    ClientId = 1,
                    Name = hostName,
                    Puid = "external-host-puid",
                    RoomCode = room.Code.Code,
                    PlayerId = 0,
                    BaseNetId = 500,
                    IsSyntheticHost = true,
                    InitialPosition = initialPosition,
                });
                _rooms[room.Code.Code] = room;
            }
        }

        public void CloseRoom(string code)
        {
            lock (_gate)
            {
                if (_rooms.TryGetValue(code, out var room))
                {
                    room.IsClosed = true;
                }
            }
        }

        public async Task BroadcastRoomStartSetupAsync(string code)
        {
            ClientSession[] members;
            lock (_gate)
            {
                if (!_rooms.TryGetValue(code, out var room))
                {
                    return;
                }

                members = room.Members.ToArray();
            }

            var shipSub = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.SkeldShipStatus,
                OwnerClientId: -2,
                Flags: 0,
                Components: new[] { new SpawnComponentEntry(9900, Array.Empty<byte>()) }));
            await BroadcastRootToMembersAsync(members, new GameDataMessage(GameCode.From(code), GameDataSubMessage.SerializeAll(new[] { shipSub })));

            foreach (var member in members)
            {
                var tasksRpc = GameDataSubMessage.FromRpc(member.BaseNetId, new SetTasksRpc(new byte[] { 0 }));
                var roleRpc = GameDataSubMessage.FromRpc(member.BaseNetId, new SetRoleRpc(RoleTypes.Crewmate));
                await BroadcastRootToMembersAsync(members, new GameDataMessage(GameCode.From(code), GameDataSubMessage.SerializeAll(new[] { roleRpc, tasksRpc })));
            }
        }

        public async Task BroadcastMeetingAndCompleteAsync(string code)
        {
            ClientSession[] members;
            lock (_gate)
            {
                if (!_rooms.TryGetValue(code, out var room) || room.Members.Count == 0)
                {
                    return;
                }

                members = room.Members.ToArray();
            }

            var caller = members[0];
            var meetingHudSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.MeetingHud,
                OwnerClientId: -2,
                Flags: 0,
                Components: new[] { new SpawnComponentEntry(9950, Array.Empty<byte>()) }));
            await BroadcastRootToMembersAsync(members, new GameDataMessage(GameCode.From(code), GameDataSubMessage.SerializeAll(new[] { meetingHudSpawn })));
            await Task.Delay(150);
            var closeSub = GameDataSubMessage.FromRpc(9950, new CloseMeetingRpc());
            await BroadcastRootToMembersAsync(members, new GameDataMessage(GameCode.From(code), GameDataSubMessage.SerializeAll(new[] { closeSub })));
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try
                {
                    res = await _udp.ReceiveAsync(_cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (res.Buffer.Length == 0)
                {
                    continue;
                }

                var epKey = res.RemoteEndPoint.ToString();
                var data = res.Buffer;
                var opt = (UdpSendOption)data[0];

                if (opt == UdpSendOption.Hello && data.Length >= 7)
                {
                    var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                    await _udp.SendAsync(ack, ack.Length, res.RemoteEndPoint);

                    // Hello payload layout: [0x08][seqHi][seqLo][0x00][clientVer:4][platformByte?...]
                    // Using MessageReader on bytes after clientVersion + platform tag is straightforward:
                    var name = ExtractNameFromHello(data);
                    var matchingSeat = _roster.Seats.FirstOrDefault(s => s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var puid = matchingSeat is not null ? (_vault.Get(matchingSeat.AccountId)?.Puid ?? $"p-{matchingSeat.AccountId}") : $"p-{epKey}";

                    var session = new ClientSession
                    {
                        EndPoint = res.RemoteEndPoint,
                        ClientId = Interlocked.Increment(ref _nextClientId),
                        Name = name,
                        Puid = puid,
                        BaseNetId = (uint)Interlocked.Add(ref _nextNetId, 10),
                    };
                    _byEndpoint[epKey] = session;
                }
                else if (opt == UdpSendOption.Ping && data.Length >= 3)
                {
                    var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                    await _udp.SendAsync(ack, ack.Length, res.RemoteEndPoint);
                }
                else if (opt == UdpSendOption.Reliable && data.Length >= 3)
                {
                    var ack = new byte[] { (byte)UdpSendOption.Acknowledgement, data[1], data[2], 0xff };
                    await _udp.SendAsync(ack, ack.Length, res.RemoteEndPoint);

                    if (!_byEndpoint.TryGetValue(epKey, out var session))
                    {
                        continue;
                    }

                    var root = ProtocolRegistry.Default.DecodeRoot(data.AsSpan(3).ToArray());
                    await HandleReliableRootAsync(session, root);
                }
                else if (opt == UdpSendOption.Disconnect)
                {
                    if (_byEndpoint.TryRemove(epKey, out var leaving))
                    {
                        await RemoveFromRoomAsync(leaving);
                    }
                }
            }
        }

        private async Task HandleReliableRootAsync(ClientSession sender, IRootMessage root)
        {
            switch (root)
            {
                case HostGameMessage:
                {
                    var code = GameCode.Create();
                    lock (_gate)
                    {
                        _rooms[code.Code] = new RoomState
                        {
                            Code = code,
                            HostClientId = sender.ClientId,
                        };
                    }

                    await SendToClientAsync(sender, new HostGameMessage(GameOptionsOrCode.FromResponse(code)));
                    break;
                }

                case JoinGameMessage join:
                {
                    RoomState? room;
                    ClientSession[] snapshot;
                    byte assignedPlayerId;
                    lock (_gate)
                    {
                        if (!_rooms.TryGetValue(join.GameCode.Code, out room) || room.IsClosed)
                        {
                            room = null;
                            snapshot = Array.Empty<ClientSession>();
                            assignedPlayerId = 0;
                        }
                        else
                        {
                            assignedPlayerId = room.NextPlayerId++;
                            sender.RoomCode = room.Code.Code;
                            sender.PlayerId = assignedPlayerId;
                            snapshot = room.Members.ToArray();
                            room.Members.Add(sender);
                        }
                    }

                    if (room is null)
                    {
                        await SendDisconnectAsync(sender, DisconnectReason.GameNotFound);
                        return;
                    }

                    var others = snapshot.Select(m => new JoinedPlayerEntry(
                        m.ClientId,
                        m.Name,
                        new PlatformSpecificData(Platforms.Android, m.Name),
                        1,
                        m.Puid,
                        string.Empty)).ToArray();

                    await SendToClientAsync(sender, new JoinedGameMessage(room.Code, sender.ClientId, room.HostClientId, others));

                    // Also notify already-connected members (especially Host!) by spawning the new player & manager.
                    var gmSpawn = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                        SpawnObjectId.NormalGameManager,
                        -2,
                        0,
                        new[] { new SpawnComponentEntry(8800, ComponentDataCodec.SerializeGameManagerOptionsDelta(new NormalGameOptions())) }));
                    await SendToClientAsync(sender, new GameDataMessage(room.Code, GameDataSubMessage.SerializeAll(new[] { gmSpawn })));

                    foreach (var existing in snapshot)
                    {
                        await SendToClientAsync(sender, BuildPlayerSpawnMessage(room.Code, existing));
                        if (!existing.IsSyntheticHost)
                        {
                            await SendToClientAsync(existing, new JoinedGameMessage(
                                room.Code,
                                existing.ClientId,
                                room.HostClientId,
                                new[]
                                {
                                    new JoinedPlayerEntry(sender.ClientId, sender.Name, new PlatformSpecificData(Platforms.Android, sender.Name), 1, sender.Puid, string.Empty),
                                }));
                            await SendToClientAsync(existing, BuildPlayerSpawnMessage(room.Code, sender));
                        }
                    }

                    await SendToClientAsync(sender, BuildPlayerSpawnMessage(room.Code, sender));
                    break;
                }

                case AlterGameMessage alter:
                {
                    ClientSession[] members;
                    lock (_gate)
                    {
                        if (!_rooms.TryGetValue(alter.GameCode.Code, out var room)) return;
                        room.IsPublic = alter.IsPublic;
                        members = room.Members.ToArray();
                    }

                    await BroadcastRootToMembersAsync(members, alter);
                    break;
                }

                case StartGameMessage or EndGameMessage or KickPlayerMessage or GameDataMessage:
                {
                    ClientSession[] members;
                    lock (_gate)
                    {
                        if (!_rooms.TryGetValue(sender.RoomCode, out var room)) return;
                        members = room.Members.ToArray();
                    }

                    await BroadcastRootToMembersAsync(members, root);
                    break;
                }
            }
        }

        private async Task RemoveFromRoomAsync(ClientSession leaving)
        {
            ClientSession[] remaining;
            int hostId;
            GameCode code;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(leaving.RoomCode) || !_rooms.TryGetValue(leaving.RoomCode, out var room))
                {
                    return;
                }

                room.Members.Remove(leaving);
                remaining = room.Members.ToArray();
                hostId = room.HostClientId;
                code = room.Code;
            }

            await BroadcastRootToMembersAsync(remaining, new RemovePlayerMessage(code, leaving.ClientId, hostId, DisconnectReason.ExitGame));
        }

        private static GameDataMessage BuildPlayerSpawnMessage(GameCode code, ClientSession ply)
        {
            using var xw = MessageWriter.Get(MessageType.Reliable);
            new CustomNetworkTransformUpdate(1, new[] { ply.InitialPosition }).Serialize(xw, initialState: true);
            var sub = GameDataSubMessage.FromSpawn(new SpawnObjectPacket(
                SpawnObjectId.PlayerControl,
                OwnerClientId: ply.ClientId,
                Flags: 1,
                Components: new[]
                {
                    new SpawnComponentEntry(ply.BaseNetId, ComponentDataCodec.SerializePlayerControlInitial(ply.PlayerId)),
                    new SpawnComponentEntry(ply.BaseNetId + 1, Array.Empty<byte>()),
                    new SpawnComponentEntry(ply.BaseNetId + 2, xw.ToByteArray(includeHeader: false)),
                }));
            return new GameDataMessage(code, GameDataSubMessage.SerializeAll(new[] { sub }));
        }

        private async Task BroadcastRootToMembersAsync(IEnumerable<ClientSession> members, IRootMessage msg)
        {
            foreach (var member in members)
            {
                await SendToClientAsync(member, msg);
            }
        }

        private async Task SendToClientAsync(ClientSession target, IRootMessage msg)
        {
            var payload = ProtocolRegistry.Default.EncodeRoot(msg, MessageType.Reliable);
            var pkt = new byte[3 + payload.Length];
            ushort seq;
            lock (target)
            {
                seq = target.ReliableSeq++;
            }
            pkt[0] = (byte)UdpSendOption.Reliable;
            pkt[1] = (byte)(seq >> 8);
            pkt[2] = (byte)(seq & 0xff);
            Buffer.BlockCopy(payload, 0, pkt, 3, payload.Length);
            try
            {
                await _udp.SendAsync(pkt, pkt.Length, target.EndPoint);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task SendDisconnectAsync(ClientSession target, DisconnectReason reason)
        {
            using var w = MessageWriter.Get(MessageType.Unreliable);
            new DisconnectMessage(true, reason, null).Serialize(w);
            var body = w.ToByteArray(includeHeader: false);
            var pkt = new byte[1 + body.Length];
            pkt[0] = (byte)UdpSendOption.Disconnect;
            Buffer.BlockCopy(body, 0, pkt, 1, body.Length);
            await _udp.SendAsync(pkt, pkt.Length, target.EndPoint);
        }

        private static string ExtractNameFromHello(byte[] datagram)
        {
            // Hello: [0x08][seqHi][seqLo][0x00][ClientVer:4][String:Username]...
            if (datagram.Length <= 8)
            {
                return "BeanBot";
            }

            using var reader = MessageReader.Get(datagram[8..]);
            return reader.BytesRemaining > 0 ? reader.ReadString() : "BeanBot";
        }

        private static HttpResponseMessage JsonOk(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _udp.Dispose();
            if (_loop is not null)
            {
                try { await _loop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
