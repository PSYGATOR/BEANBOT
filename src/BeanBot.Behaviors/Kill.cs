using System.Numerics;
using BeanBot.Nav;
using BeanBot.World;

namespace BeanBot.Behaviors;

public readonly record struct KillPreconditionResult(bool CanKill, string Reason);

/// <summary>
/// Commanded kill behavior (Phase 1 default per Appendix E) with distance, cooldown, LOS, and witness guards.
/// </summary>
public static class Kill
{
    public const float WitnessVisionRadius = 6.0f;
    public const float EscapeVentSearchRadius = 3.0f;

    public static KillPreconditionResult CheckPreconditions(
        WorldModel world,
        NavMesh mesh,
        PlayerState target,
        bool allowBlind = false)
    {
        if (world.Phase != GamePhase.Tasks)
        {
            return new(false, $"phase-is-{world.Phase}");
        }

        var local = world.LocalPlayer;
        if (local is null || !local.IsAlive || !local.IsImpostor)
        {
            return new(false, "not-living-impostor");
        }

        if (world.InVent)
        {
            return new(false, "currently-in-vent");
        }

        if (target.PlayerId == local.PlayerId || !target.IsAlive || target.IsImpostor)
        {
            return new(false, "invalid-target");
        }

        var elapsedSinceKill = world.MatchTimeSec - world.LastKillTimestampSec;
        if (elapsedSinceKill < world.Settings.KillCooldownSeconds)
        {
            return new(false, $"kill-cooldown-remaining:{world.Settings.KillCooldownSeconds - elapsedSinceKill:F1}s");
        }

        var distance = Vector2.Distance(local.Position, target.Position);
        if (distance > world.Settings.KillDistanceUnits)
        {
            return new(false, $"out-of-range:{distance:F2}>{world.Settings.KillDistanceUnits:F2}");
        }

        if (!mesh.HasLineOfSight(local.Position, target.Position, world.ClosedDoorRooms))
        {
            return new(false, "no-line-of-sight");
        }

        if (!allowBlind && HasCrewmateWitness(world, mesh, local, target))
        {
            return new(false, "crewmate-witness-in-los");
        }

        return new(true, "ok");
    }

    public static IReadOnlyList<BotAction> EvaluateCommanded(
        WorldModel world,
        BotBehaviorState state,
        NavMesh mesh)
    {
        if (!state.CommandedKillTargetPlayerId.HasValue || world.Phase != GamePhase.Tasks)
        {
            return Array.Empty<BotAction>();
        }

        var target = world.FindPlayerByPlayerId(state.CommandedKillTargetPlayerId.Value);
        if (target is null || !target.IsAlive || target.IsImpostor)
        {
            state.CommandedKillTargetPlayerId = null;
            return Array.Empty<BotAction>();
        }

        var check = CheckPreconditions(world, mesh, target, state.CommandedKillAllowBlind);
        if (!check.CanKill)
        {
            if (check.Reason.StartsWith("out-of-range:", StringComparison.Ordinal))
            {
                return new BotAction[]
                {
                    new MoveToAction(target.Position, $"kill-approach:{target.Name}", world.Settings.KillDistanceUnits * 0.75f),
                };
            }

            return Array.Empty<BotAction>();
        }

        state.CommandedKillTargetPlayerId = null;
        var actions = new List<BotAction> { new KillAction(target.PlayerId) };

        // Step toward a nearby vent within 3 units if present for natural post-kill positioning.
        var escapeVent = mesh.FindNearestVent(world.LocalPlayer!.Position, EscapeVentSearchRadius);
        if (escapeVent is not null)
        {
            actions.Add(new MoveToAction(escapeVent.Position, $"post-kill-vent-edge:{escapeVent.Name}", 0.5f));
        }

        return actions;
    }

    private static bool HasCrewmateWitness(
        WorldModel world,
        NavMesh mesh,
        PlayerState killer,
        PlayerState target)
    {
        foreach (var other in world.PlayersByClientId.Values)
        {
            if (!other.IsAlive || other.IsImpostor || other.PlayerId == killer.PlayerId || other.PlayerId == target.PlayerId)
            {
                continue;
            }

            if (Vector2.Distance(other.Position, killer.Position) <= WitnessVisionRadius &&
                mesh.HasLineOfSight(other.Position, killer.Position, world.ClosedDoorRooms))
            {
                return true;
            }

            if (Vector2.Distance(other.Position, target.Position) <= WitnessVisionRadius &&
                mesh.HasLineOfSight(other.Position, target.Position, world.ClosedDoorRooms))
            {
                return true;
            }
        }

        return false;
    }
}
