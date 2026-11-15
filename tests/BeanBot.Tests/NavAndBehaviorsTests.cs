using System.Numerics;
using BeanBot.Behaviors;
using BeanBot.Nav;
using BeanBot.Protocol.Maps;
using BeanBot.World;
using Xunit;

namespace BeanBot.Tests;

public sealed class NavAndBehaviorsTests
{
    private readonly NavMesh _skeld = NavMesh.LoadSkeld();

    [Fact]
    public void SkeldNav_CoversAllConsolesVentsDoorsAndSpawns()
    {
        // All 14 vents sit cleanly inside walkable polygons
        foreach (var vent in _skeld.Vents)
        {
            var poly = _skeld.FindContainingPolygon(vent.Position);
            Assert.True(poly is not null, $"Vent {vent.Id} ({vent.Name}) at {vent.Position} is outside Skeld navmesh.");
        }

        // All 13 doors lie inside or on edges of walkable polygons
        foreach (var door in _skeld.Doors)
        {
            var poly = _skeld.FindContainingPolygon(door.Position);
            Assert.True(poly is not null, $"Door {door.Id} ({door.Room}) at {door.Position} is outside Skeld navmesh.");
        }

        // Every representative task console sits inside walkable polygons
        foreach (var kv in SkeldCatalog.Tasks)
        {
            foreach (var step in kv.Value.DefaultSteps)
            {
                var poly = _skeld.FindContainingPolygon(step.Position);
                Assert.True(poly is not null, $"Task {kv.Value.Name} step in {step.Room} at {step.Position} is outside Skeld navmesh.");
            }
        }
    }

    [Fact]
    public void NavMesh_SnapsInvalidSpawnPoint_AndFindsLongPathsAcrossSkeld()
    {
        // Center table obstacle is unwalkable -> F5 snaps to nearest polygon centroid
        var (snapped, poly, wasSnapped) = _skeld.SnapToNavMesh(_skeld.InitialSpawnCenter);
        Assert.True(wasSnapped);
        Assert.NotNull(_skeld.FindContainingPolygon(snapped));
        Assert.StartsWith("Cafe", poly.Name, StringComparison.Ordinal);

        // Path from Cafeteria South to Reactor and Nav finds valid connected corridor waypoints
        var toReactor = AStar.FindPath(_skeld, new Vector2(-1.0f, -2.5f), new Vector2(-21.79f, -5.57f));
        Assert.True(toReactor.Found);
        Assert.True(toReactor.Waypoints.Count >= 3);
        Assert.True(toReactor.TotalDistance > 15f);

        var toNav = AStar.FindPath(_skeld, new Vector2(-21.79f, -5.57f), new Vector2(18.73f, -4.73f));
        Assert.True(toNav.Found);
        Assert.True(toNav.TotalDistance > 30f);
    }

    [Fact]
    public void PathFollower_ReroutesOnClosedDoor_ClampsSpeedG3_AndFreezesInMeetingF6()
    {
        var follower = new PathFollower(_skeld);
        var start = new Vector2(-10.0f, -7.8f); // Inside Electrical
        var goal = new Vector2(-17.5f, -3.5f); // Reactor / West Hall

        // Normally goes Electrical -> LowerSouthHall -> LowerEngine -> WestHall (or via Storage/Cafeteria/UpperEngine)
        Assert.True(follower.SetDestination(start, goal));

        // Closing Storage + Cafeteria doors leaves West corridor open -> still finds path;
        // Closing LowerEngine door forces Electrical -> LowerSouthHall -> Storage -> CenterHall -> Cafeteria -> UpperEngine!
        var closed = new HashSet<string> { "LowerEngine" };
        var step = follower.Step(start, playerSpeedMultiplier: 1.0f, deltaSeconds: 0.1f, freezeMovement: false, closed);
        Assert.True(step.RecomputedPath);

        // G3 speed clamp: 0.1s at 1.0x speed (2.5 u/s) must move <= 0.25 units (+epsilon)
        Assert.True(Vector2.Distance(start, step.Position) <= 0.251f);
        Assert.True(step.Velocity.Length() <= PathFollower.BaseWalkSpeedUnitsPerSec + 1e-3f);

        // F6 meeting freeze: zero velocity, zero displacement
        var frozen = follower.Step(step.Position, playerSpeedMultiplier: 1.0f, deltaSeconds: 0.1f, freezeMovement: true, closed);
        Assert.Equal(step.Position, frozen.Position);
        Assert.Equal(Vector2.Zero, frozen.Velocity);
    }

    [Fact]
    public void TaskLoop_PicksNearestTask_AndImpostorUsesFakeTasksSafely()
    {
        var world = CreateSkeldWorld(PlayerRole.Crewmate, new Vector2(2.0f, -6.5f)); // Inside Admin
        world.LocalTasks.Add(new AssignedTask
        {
            TaskId = 0,
            TaskTypeName = "StartReactor",
            Room = "Reactor",
            StepPositions = new[] { new Vector2(-21.79f, -5.57f) },
        });
        world.LocalTasks.Add(new AssignedTask
        {
            TaskId = 1,
            TaskTypeName = "SwipeCard",
            Room = "Admin",
            StepPositions = new[] { new Vector2(5.63f, -8.63f) },
        });

        var state = new BotBehaviorState { TaskDwellDurationSec = 0.1 };
        var actions = TaskLoop.Evaluate(world, state, _skeld, 0.1);
        var move = Assert.IsType<MoveToAction>(Assert.Single(actions));
        Assert.Contains("SwipeCard", move.Reason);

        // Move right onto SwipeCard console -> dwell -> CompleteTaskAction (real)
        world.UpdatePlayerPosition(world.LocalClientId, new Vector2(5.63f, -8.63f));
        _ = TaskLoop.Evaluate(world, state, _skeld, 0.05); // dwell
        var complete = Assert.IsType<CompleteTaskAction>(Assert.Single(TaskLoop.Evaluate(world, state, _skeld, 0.1)));
        Assert.Equal(1u, complete.TaskId);
        Assert.False(complete.IsFakeTask);

        // Impostor emits FakeTask instead and avoids living crewmates when kill is ready
        world.LocalPlayer!.Role = PlayerRole.Impostor;
        var fake = Assert.IsType<CompleteTaskAction>(Assert.Single(TaskLoop.Evaluate(world, state, _skeld, 0.2)));
        Assert.True(fake.IsFakeTask);
    }

    [Fact]
    public void Follow_TracksTargetAcrossThreeRoomsWithoutClipping_BacksOff_AndHandlesTargetDeath()
    {
        var world = CreateSkeldWorld(PlayerRole.Crewmate, new Vector2(0.0f, 3.0f)); // Cafeteria North
        var target = new PlayerState
        {
            ClientId = 2,
            PlayerId = 1,
            Name = "TargetPlayer",
            Role = PlayerRole.Crewmate,
            IsAlive = true,
            Position = new Vector2(-0.5f, 3.0f),
            Facing = -Vector2.UnitX,
        };
        world.PlayersByClientId[2] = target;

        var state = new BotBehaviorState { FollowTargetPlayerId = 1, FollowDistance = 1.2f };
        var follower = new PathFollower(_skeld);

        // Simulate target walking Cafeteria -> UpperCorridor -> MedBay -> UpperEngine (3+ rooms)
        var route = new[]
        {
            new Vector2(-4.5f, 1.5f),
            new Vector2(-9.1f, 1.0f),
            new Vector2(-14.0f, 1.2f),
            new Vector2(-17.0f, 1.2f),
        };

        foreach (var wp in route)
        {
            world.UpdatePlayerPosition(2, wp);
            for (var tick = 0; tick < 35; tick++)
            {
                var actions = Follow.Evaluate(world, state, _skeld, 0.1);
                var move = actions.OfType<MoveToAction>().FirstOrDefault();
                if (move is not null)
                {
                    follower.SetDestination(world.LocalPlayer!.Position, move.Destination);
                    var step = follower.Step(world.LocalPlayer.Position, 1.5f, 0.1f, false);
                    world.UpdatePlayerPosition(world.LocalClientId, step.Position);
                    // Must NEVER clip outside walkable polygons
                    Assert.NotNull(_skeld.FindContainingPolygon(step.Position));
                }
            }
        }

        Assert.True(Vector2.Distance(world.LocalPlayer!.Position, target.Position) < 3.0f);

        // Anti-stacking backoff when < 0.6 units for >= 1s
        world.UpdatePlayerPosition(world.LocalClientId, target.Position + new Vector2(0.2f, 0f));
        IReadOnlyList<BotAction> backoff = Array.Empty<BotAction>();
        for (var i = 0; i < 11; i++)
        {
            backoff = Follow.Evaluate(world, state, _skeld, 0.1);
        }

        var backMove = Assert.IsType<MoveToAction>(Assert.Single(backoff));
        Assert.Equal("follow-backoff", backMove.Reason);

        // Target death -> holds 3s then clears follow target
        target.IsAlive = false;
        var hold = Assert.IsType<HoldPositionAction>(Assert.Single(Follow.Evaluate(world, state, _skeld, 0.1)));
        Assert.Equal("follow-target-died-hold", hold.Reason);

        var afterHold = Follow.Evaluate(world, state, _skeld, 3.1);
        Assert.Empty(afterHold);
        Assert.Null(state.FollowTargetPlayerId);
    }

    [Fact]
    public void KillAndVentAndReportAndVote_EnforceAllPreconditions()
    {
        var world = CreateSkeldWorld(PlayerRole.Impostor, new Vector2(2.6f, -9.8f)); // Admin near AdminVent (id 0)
        var victim = new PlayerState
        {
            ClientId = 2,
            PlayerId = 1,
            Name = "CrewA",
            Role = PlayerRole.Crewmate,
            IsAlive = true,
            Position = new Vector2(3.2f, -9.8f),
        };
        var witness = new PlayerState
        {
            ClientId = 3,
            PlayerId = 2,
            Name = "Witness",
            Role = PlayerRole.Crewmate,
            IsAlive = true,
            Position = new Vector2(4.0f, -7.0f), // Same AdminRoom with clear LOS
        };
        world.PlayersByClientId[2] = victim;
        world.PlayersByClientId[3] = witness;

        // Witness present -> abort unless --blind
        var blocked = Kill.CheckPreconditions(world, _skeld, victim, allowBlind: false);
        Assert.False(blocked.CanKill);
        Assert.Equal("crewmate-witness-in-los", blocked.Reason);

        // --blind bypasses witness check
        var blind = Kill.CheckPreconditions(world, _skeld, victim, allowBlind: true);
        Assert.True(blind.CanKill);

        // Remove witness -> KillAllowed + Vent escape step generated
        witness.IsAlive = false;
        var state = new BotBehaviorState { CommandedKillTargetPlayerId = 1 };
        var killActions = Kill.EvaluateCommanded(world, state, _skeld);
        Assert.Contains(killActions, a => a is KillAction k && k.TargetPlayerId == 1);

        // Vent sequence: Enter -> 300ms dwell -> Exit linked vent
        state.CommandedVentId = 0; // AdminVent (links: 2, 1)
        var enter = Assert.IsType<EnterVentAction>(Assert.Single(Vent.Evaluate(world, state, _skeld)));
        Assert.Equal(0, enter.VentId);

        world.InVent = true;
        world.CurrentVentId = 0;
        world.MatchTimeSec += 0.1;
        Assert.IsType<HoldPositionAction>(Assert.Single(Vent.Evaluate(world, state, _skeld)));

        world.MatchTimeSec += 0.25; // > 0.30s total dwell
        var exit = Assert.IsType<ExitVentAction>(Assert.Single(Vent.Evaluate(world, state, _skeld)));
        Assert.Equal(2, exit.VentId); // First link of AdminVent

        // Dead body report within 2.0u (crewmate auto-reports)
        world.InVent = false;
        world.CurrentVentId = null;
        world.LocalPlayer!.Role = PlayerRole.Crewmate;
        world.DeadBodies.Add(new DeadBodyState(1, new Vector2(3.1f, -9.8f)));
        var report = Assert.IsType<ReportBodyAction>(Assert.Single(Report.Evaluate(world)));
        Assert.Equal((byte)1, report.BodyPlayerId);

        // Vote policy in Meeting
        world.TransitionPhase(GamePhase.Meeting, "ReportDeadBody", 0x0b);
        state.ResetForNewMeeting();
        state.CommandedVoteTargetId = MeetingState.SkipVoteId;
        var vote = Assert.IsType<CastVoteAction>(Assert.Single(Vote.Evaluate(world, state)));
        Assert.Equal(MeetingState.SkipVoteId, vote.TargetPlayerId);
    }

    private static WorldModel CreateSkeldWorld(PlayerRole role, Vector2 pos)
    {
        var world = new WorldModel();
        world.LobbyCode = "ABCDEF";
        world.LocalClientId = 1;
        world.LocalPlayerId = 0;
        world.MatchTimeSec = 100.0;
        world.Settings = new SyncSettingsModel
        {
            PlayerSpeed = 1.0f,
            KillCooldownSeconds = 15.0f,
            KillDistanceUnits = 1.5f,
            VentCooldownSeconds = 15.0f,
            DiscussionTimeSeconds = 10,
        };
        world.PlayersByClientId[1] = new PlayerState
        {
            ClientId = 1,
            PlayerId = 0,
            NetId = 10,
            TransformNetId = 12,
            Name = "BeanBot",
            Role = role,
            IsAlive = true,
            Position = pos,
        };
        world.TransitionPhase(GamePhase.Tasks, "TestStart", null);
        return world;
    }
}
