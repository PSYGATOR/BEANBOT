using System.Numerics;
using BeanBot.World;

namespace BeanBot.Behaviors;

/// <summary>
/// Body report behavior: report any unreported dead body within 2.0 units (P1.8 / Appendix D).
/// </summary>
public static class Report
{
    public const float ReportDistance = 2.0f;

    public static IReadOnlyList<BotAction> Evaluate(WorldModel world)
    {
        var local = world.LocalPlayer;
        if (world.Phase != GamePhase.Tasks || local is null || !local.IsAlive || local.IsImpostor || world.InVent)
        {
            return Array.Empty<BotAction>();
        }

        DeadBodyState? nearest = null;
        var bestDist = ReportDistance;

        foreach (var body in world.DeadBodies)
        {
            if (body.Reported || body.PlayerId == local.PlayerId)
            {
                continue;
            }

            var dist = Vector2.Distance(local.Position, body.Position);
            if (dist <= bestDist)
            {
                bestDist = dist;
                nearest = body;
            }
        }

        return nearest is null
            ? Array.Empty<BotAction>()
            : new BotAction[] { new ReportBodyAction(nearest.PlayerId) };
    }
}
