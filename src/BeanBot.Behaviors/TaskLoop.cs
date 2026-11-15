using System.Numerics;
using BeanBot.Nav;
using BeanBot.World;

namespace BeanBot.Behaviors;

/// <summary>
/// Crewmate real task execution + Impostor fake-task simulation (P1.5 / Appendix D).
/// </summary>
public static class TaskLoop
{
    public const float ConsoleInteractDistance = 1.05f;
    public const float WitnessAvoidRadius = 5.5f;

    public static IReadOnlyList<BotAction> Evaluate(
        WorldModel world,
        BotBehaviorState state,
        NavMesh mesh,
        double dtSeconds)
    {
        if (!state.TasksEnabled || world.Phase != GamePhase.Tasks || world.InVent)
        {
            return Array.Empty<BotAction>();
        }

        var local = world.LocalPlayer;
        if (local is null)
        {
            return Array.Empty<BotAction>();
        }

        var isImpostor = local.IsImpostor;
        var incomplete = world.LocalTasks.Where(t => !t.IsCompleted).ToList();
        if (incomplete.Count == 0)
        {
            return new BotAction[] { new HoldPositionAction("all-tasks-complete") };
        }

        // Keep the active task sticky while walking so bots do not oscillate across polygon boundaries.
        AssignedTask? chosen = null;
        var killReady = isImpostor &&
                        (world.MatchTimeSec - world.LastKillTimestampSec) >= world.Settings.KillCooldownSeconds;

        if (state.ActiveTaskId is { } activeId)
        {
            var sticky = incomplete.FirstOrDefault(t => t.TaskId == activeId);
            if (sticky is not null)
            {
                var stickyPos = sticky.CurrentTargetPosition;
                var avoidSticky = isImpostor && killReady && IsRoomOccupiedByLivingCrewmate(world, mesh, stickyPos, local.PlayerId);
                if (!avoidSticky && AStar.FindPath(mesh, local.Position, stickyPos, world.ClosedDoorRooms).Found)
                {
                    chosen = sticky;
                }
            }
        }

        if (chosen is null)
        {
            var bestCost = float.PositiveInfinity;
            foreach (var task in incomplete)
            {
                var targetPos = task.CurrentTargetPosition;
                if (isImpostor && killReady && IsRoomOccupiedByLivingCrewmate(world, mesh, targetPos, local.PlayerId))
                {
                    // Appendix D: impostors avoid entering rooms with living crewmates when kill is off cooldown.
                    continue;
                }

                var cost = AStar.ComputePathCost(mesh, local.Position, targetPos, world.ClosedDoorRooms);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    chosen = task;
                }
            }
        }

        chosen ??= incomplete[0];
        var consolePos = chosen.CurrentTargetPosition;
        var dist = Vector2.Distance(local.Position, consolePos);

        if (state.ActiveTaskId != chosen.TaskId)
        {
            state.ActiveTaskId = chosen.TaskId;
            state.TaskDwellRemainingSec = state.TaskDwellDurationSec;
        }

        if (dist <= ConsoleInteractDistance)
        {
            state.TaskDwellRemainingSec -= dtSeconds;
            if (state.TaskDwellRemainingSec > 0)
            {
                return new BotAction[] { new HoldPositionAction($"task-dwell:{chosen.TaskTypeName}") };
            }

            state.ActiveTaskId = null;
            return new BotAction[]
            {
                new CompleteTaskAction(chosen.TaskId, chosen.CurrentStep, consolePos, IsFakeTask: isImpostor),
            };
        }

        state.TaskDwellRemainingSec = state.TaskDwellDurationSec;
        return new BotAction[]
        {
            new MoveToAction(consolePos, $"task:{chosen.TaskTypeName}", ConsoleInteractDistance * 0.65f),
        };
    }

    private static bool IsRoomOccupiedByLivingCrewmate(
        WorldModel world,
        NavMesh mesh,
        Vector2 roomPoint,
        byte localPlayerId)
    {
        var room = mesh.GetRoomAt(roomPoint);
        foreach (var p in world.PlayersByClientId.Values)
        {
            if (!p.IsAlive || p.PlayerId == localPlayerId || p.IsImpostor)
            {
                continue;
            }

            if (string.Equals(mesh.GetRoomAt(p.Position), room, StringComparison.OrdinalIgnoreCase) &&
                Vector2.Distance(p.Position, roomPoint) <= WitnessAvoidRadius)
            {
                return true;
            }
        }

        return false;
    }
}
