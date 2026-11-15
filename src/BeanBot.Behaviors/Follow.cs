using System.Numerics;
using BeanBot.Nav;
using BeanBot.World;

namespace BeanBot.Behaviors;

/// <summary>
/// Trailing follow behavior with anti-stacking backoff and target-death fallback (P1.9 / Appendix D).
/// </summary>
public static class Follow
{
    public const float StopThreshold = 0.40f;
    public const float TooCloseThreshold = 0.60f;
    public const double TooCloseDurationTriggerSec = 1.0;
    public const float BackoffDistance = 2.0f;
    public const double DeathHoldDurationSec = 3.0;

    public static IReadOnlyList<BotAction> Evaluate(
        WorldModel world,
        BotBehaviorState state,
        NavMesh mesh,
        double dtSeconds)
    {
        if (world.Phase is GamePhase.Meeting or GamePhase.End || world.InVent)
        {
            return Array.Empty<BotAction>();
        }

        // If target recently died, hold position for 3s then resume normal task loop.
        if (state.TargetDeathHoldRemainingSec > 0)
        {
            state.TargetDeathHoldRemainingSec -= dtSeconds;
            if (state.TargetDeathHoldRemainingSec <= 0)
            {
                state.TargetDeathHoldRemainingSec = 0;
                state.FollowTargetPlayerId = null;
                state.FollowTargetName = null;
                return Array.Empty<BotAction>();
            }

            return new BotAction[] { new HoldPositionAction("follow-target-died-hold") };
        }

        var local = world.LocalPlayer;
        var target = ResolveTarget(world, state);
        if (local is null || target is null)
        {
            return Array.Empty<BotAction>();
        }

        if (!target.IsAlive)
        {
            state.TargetDeathHoldRemainingSec = DeathHoldDurationSec;
            state.WasFollowingTargetAlive = false;
            return new BotAction[] { new HoldPositionAction("follow-target-died-hold") };
        }

        state.WasFollowingTargetAlive = true;

        var desiredDist = state.FollowKillRangeHold && local.IsImpostor
            ? MathF.Max(0.55f, world.Settings.KillDistanceUnits * 0.85f)
            : MathF.Max(0.5f, state.FollowDistance);

        var actualDist = Vector2.Distance(local.Position, target.Position);
        if (actualDist < TooCloseThreshold)
        {
            state.TooCloseAccumulatedSec += dtSeconds;
        }
        else
        {
            state.TooCloseAccumulatedSec = 0;
        }

        var facing = target.Facing.LengthSquared() > 1e-4f
            ? Vector2.Normalize(target.Facing)
            : Vector2.Normalize(target.Position - local.Position + new Vector2(0.01f, 0f));

        var inLobby = world.Phase == GamePhase.Lobby;
        if (state.TooCloseAccumulatedSec >= TooCloseDurationTriggerSec)
        {
            var backoffRaw = target.Position - (facing * BackoffDistance);
            // Dropship (Lobby) uses its own coordinates around (0, 1.5), not The Skeld navmesh.
            var backoffGoal = inLobby ? backoffRaw : mesh.SnapToNavMesh(backoffRaw).SnappedPoint;
            return new BotAction[] { new MoveToAction(backoffGoal, "follow-backoff", StopThreshold) };
        }

        var slotRaw = target.Position - (facing * desiredDist);
        Vector2 slotGoal;
        if (inLobby)
        {
            slotGoal = slotRaw;
        }
        else
        {
            var targetSnap = mesh.SnapToNavMesh(target.Position).SnappedPoint;
            slotGoal = mesh.SnapToNavMesh(slotRaw).SnappedPoint;
            if (!mesh.HasLineOfSight(slotGoal, targetSnap, world.ClosedDoorRooms))
            {
                slotGoal = targetSnap;
            }
        }

        if (Vector2.Distance(local.Position, slotGoal) <= StopThreshold)
        {
            return new BotAction[] { new HoldPositionAction($"following:{target.Name}") };
        }

        return new BotAction[] { new MoveToAction(slotGoal, $"follow:{target.Name}", StopThreshold) };
    }

    private static PlayerState? ResolveTarget(WorldModel world, BotBehaviorState state)
    {
        if (state.FollowTargetPlayerId is { } pid && pid != byte.MaxValue)
        {
            var byId = world.FindPlayerByPlayerId(pid);
            if (byId is not null && byId.ClientId != world.LocalClientId)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(state.FollowTargetName))
        {
            var matched = world.FindPlayerByQuery(state.FollowTargetName, excludeLocal: true);
            if (matched is not null)
            {
                if (matched.PlayerId != byte.MaxValue)
                {
                    state.FollowTargetPlayerId = matched.PlayerId;
                }
                return matched;
            }
        }

        return null;
    }
}
