using System.Numerics;

namespace BeanBot.Nav;

public readonly record struct MovementStep(
    Vector2 Position,
    Vector2 Velocity,
    bool HasArrived,
    bool RecomputedPath);

/// <summary>
/// Tick-rate movement interpolator with PlayerSpeed clamping (G3), look-ahead corner rounding,
/// deviation re-pathing (>2m), closed-door re-routing, and meeting freeze (F6).
/// </summary>
public sealed class PathFollower
{
    public const float BaseWalkSpeedUnitsPerSec = 2.5f;
    public const float RePathDeviationThreshold = 2.0f;
    public const float WaypointArrivalEpsilon = 0.12f;

    private readonly NavMesh _mesh;
    private List<Vector2> _waypoints = new();
    private List<NavPortal> _portals = new();
    private int _waypointIndex;
    private Vector2? _goal;
    private string _closedDoorSignature = string.Empty;

    public bool HasActivePath => _goal.HasValue && _waypointIndex < _waypoints.Count;
    public Vector2? CurrentGoal => _goal;
    public IReadOnlyList<Vector2> CurrentWaypoints => _waypoints;

    public PathFollower(NavMesh mesh)
    {
        _mesh = mesh;
    }

    public void Clear()
    {
        _waypoints.Clear();
        _portals.Clear();
        _waypointIndex = 0;
        _goal = null;
        _closedDoorSignature = string.Empty;
    }

    public bool SetDestination(
        Vector2 currentPosition,
        Vector2 goal,
        IReadOnlySet<string>? closedDoorRooms = null)
    {
        var result = AStar.FindPath(_mesh, currentPosition, goal, closedDoorRooms);
        if (!result.Found)
        {
            Clear();
            return false;
        }

        _goal = goal;
        _waypoints = result.Waypoints.ToList();
        _portals = result.PortalsUsed.ToList();
        _waypointIndex = _waypoints.Count > 1 ? 1 : 0;
        _closedDoorSignature = ComputeDoorSignature(closedDoorRooms);
        return true;
    }

    /// <summary>
    /// Advances at most effectiveSpeed * dt along the current path, freezing on meeting (F6).
    /// </summary>
    public MovementStep Step(
        Vector2 currentPosition,
        float playerSpeedMultiplier,
        float deltaSeconds,
        bool freezeMovement,
        IReadOnlySet<string>? closedDoorRooms = null)
    {
        if (freezeMovement || !_goal.HasValue || deltaSeconds <= 0f)
        {
            return new MovementStep(currentPosition, Vector2.Zero, false, false);
        }

        var recomputed = false;
        var currentDoorSignature = ComputeDoorSignature(closedDoorRooms);
        var pathBlocked = currentDoorSignature != _closedDoorSignature && IsPathBlockedByDoors(closedDoorRooms);

        var deviating = HasActivePath &&
                        Vector2.Distance(currentPosition, _waypoints[_waypointIndex]) > RePathDeviationThreshold &&
                        (_waypointIndex == 0 ||
                         DistanceToSegment(currentPosition, _waypoints[_waypointIndex - 1], _waypoints[_waypointIndex]) > RePathDeviationThreshold);

        if (!HasActivePath || pathBlocked || deviating)
        {
            if (SetDestination(currentPosition, _goal.Value, closedDoorRooms))
            {
                recomputed = true;
            }
            else
            {
                return new MovementStep(currentPosition, Vector2.Zero, false, false);
            }
        }

        var maxSpeed = MathF.Max(0.1f, playerSpeedMultiplier) * BaseWalkSpeedUnitsPerSec;
        var remainingBudget = maxSpeed * deltaSeconds;
        var pos = currentPosition;

        while (remainingBudget > 1e-5f && _waypointIndex < _waypoints.Count)
        {
            var target = _waypoints[_waypointIndex];
            var delta = target - pos;
            var dist = delta.Length();

            if (dist <= WaypointArrivalEpsilon && _waypointIndex < _waypoints.Count - 1)
            {
                _waypointIndex++;
                continue;
            }

            if (dist <= remainingBudget)
            {
                pos = target;
                remainingBudget -= dist;
                _waypointIndex++;
            }
            else
            {
                pos += (delta / dist) * remainingBudget;
                remainingBudget = 0f;
            }
        }

        var arrived = _waypointIndex >= _waypoints.Count ||
                      Vector2.Distance(pos, _goal.Value) <= WaypointArrivalEpsilon;
        if (arrived)
        {
            Clear();
        }

        var velocity = (pos - currentPosition) / deltaSeconds;
        var actualSpeed = velocity.Length();
        if (actualSpeed > maxSpeed + 1e-3f)
        {
            velocity = Vector2.Normalize(velocity) * maxSpeed;
            pos = currentPosition + (velocity * deltaSeconds);
        }

        return new MovementStep(pos, velocity, arrived, recomputed);
    }

    private bool IsPathBlockedByDoors(IReadOnlySet<string>? closedDoorRooms)
        => _portals.Any(p => NavMesh.IsPortalBlocked(p, closedDoorRooms));

    private static string ComputeDoorSignature(IReadOnlySet<string>? closedDoorRooms)
        => closedDoorRooms is null || closedDoorRooms.Count == 0
            ? string.Empty
            : string.Join(",", closedDoorRooms.OrderBy(r => r, StringComparer.OrdinalIgnoreCase));

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f)
        {
            return Vector2.Distance(p, a);
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        var proj = a + (ab * t);
        return Vector2.Distance(p, proj);
    }
}
