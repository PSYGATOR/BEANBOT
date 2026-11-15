using System.Numerics;
using BeanBot.Nav;
using BeanBot.World;

namespace BeanBot.Behaviors;

/// <summary>
/// Vent entry -> 300ms dwell -> linked exit behavior (P1.7 / Appendix D).
/// </summary>
public static class Vent
{
    public const float VentUseRadius = 1.5f;
    public const double VentDwellSeconds = 0.30;

    public static IReadOnlyList<BotAction> Evaluate(
        WorldModel world,
        BotBehaviorState state,
        NavMesh mesh)
    {
        var local = world.LocalPlayer;
        if (world.Phase != GamePhase.Tasks || local is null || !local.IsAlive || !local.IsImpostor)
        {
            return Array.Empty<BotAction>();
        }

        // If already inside a vent, complete the >= 300ms dwell and exit via linked vent.
        if (world.InVent && world.CurrentVentId.HasValue)
        {
            state.VentEnteredAtMatchSec ??= world.MatchTimeSec;
            if ((world.MatchTimeSec - state.VentEnteredAtMatchSec.Value) < VentDwellSeconds)
            {
                return new BotAction[] { new HoldPositionAction("vent-dwell") };
            }

            var currentVent = mesh.Vents.FirstOrDefault(v => v.Id == world.CurrentVentId.Value);
            var exitId = state.PendingVentExitId
                ?? currentVent?.Links.FirstOrDefault()
                ?? world.CurrentVentId.Value;

            state.PendingVentExitId = null;
            state.CommandedVentId = null;
            state.VentEnteredAtMatchSec = null;
            return new BotAction[] { new ExitVentAction(exitId) };
        }

        if (!state.CommandedVentId.HasValue)
        {
            return Array.Empty<BotAction>();
        }

        var targetVent = mesh.Vents.FirstOrDefault(v => v.Id == state.CommandedVentId.Value);
        if (targetVent is null)
        {
            state.CommandedVentId = null;
            return Array.Empty<BotAction>();
        }

        var dist = Vector2.Distance(local.Position, targetVent.Position);
        if (dist > VentUseRadius)
        {
            return new BotAction[] { new MoveToAction(targetVent.Position, $"approach-vent:{targetVent.Name}", VentUseRadius * 0.7f) };
        }

        if ((world.MatchTimeSec - world.LastVentTimestampSec) < world.Settings.VentCooldownSeconds)
        {
            return new BotAction[] { new HoldPositionAction("vent-cooldown") };
        }

        state.VentEnteredAtMatchSec = world.MatchTimeSec;
        state.PendingVentExitId ??= targetVent.Links.Count > 0 ? targetVent.Links[0] : targetVent.Id;
        return new BotAction[] { new EnterVentAction(targetVent.Id) };
    }
}
