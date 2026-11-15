using System.Numerics;

namespace BeanBot.Nav;

public sealed record NavPathResult(
    bool Found,
    IReadOnlyList<Vector2> Waypoints,
    IReadOnlyList<NavPortal> PortalsUsed,
    float TotalDistance)
{
    public static NavPathResult Unreachable { get; } = new(false, Array.Empty<Vector2>(), Array.Empty<NavPortal>(), float.PositiveInfinity);
}

/// <summary>
/// Polygon A* search with closed-door filtering and string-pulling (funnel) smoothing.
/// </summary>
public static class AStar
{
    public static NavPathResult FindPath(
        NavMesh mesh,
        Vector2 start,
        Vector2 goal,
        IReadOnlySet<string>? closedDoorRooms = null)
    {
        var (snappedStart, startPoly, _) = mesh.SnapToNavMesh(start);
        var (snappedGoal, goalPoly, _) = mesh.SnapToNavMesh(goal);

        if (startPoly.Id == goalPoly.Id)
        {
            var dist = Vector2.Distance(snappedStart, snappedGoal);
            var direct = dist < 1e-4f
                ? new[] { snappedGoal }
                : new[] { snappedStart, snappedGoal };
            return new NavPathResult(true, direct, Array.Empty<NavPortal>(), dist);
        }

        // Standard A* over polygon nodes using portal midpoints to measure cumulative edge distance accurately.
        var gScore = new Dictionary<int, float> { [startPoly.Id] = 0f };
        var cameFromPortal = new Dictionary<int, NavPortal>();
        var cameFromPos = new Dictionary<int, Vector2> { [startPoly.Id] = snappedStart };
        var open = new PriorityQueue<int, float>();
        var closed = new HashSet<int>();

        open.Enqueue(startPoly.Id, Vector2.Distance(snappedStart, snappedGoal));

        var found = false;
        while (open.TryDequeue(out var currentId, out _))
        {
            if (currentId == goalPoly.Id)
            {
                found = true;
                break;
            }

            if (!closed.Add(currentId))
            {
                continue;
            }

            var currentPos = cameFromPos[currentId];
            var currentG = gScore[currentId];

            foreach (var portal in mesh.GetPortalsFrom(currentId))
            {
                if (closed.Contains(portal.ToPolyId) || NavMesh.IsPortalBlocked(portal, closedDoorRooms))
                {
                    continue;
                }

                var portalPoint = ProjectPointOntoSegment(currentPos, portal.Left, portal.Right);
                var tentativeG = currentG + Vector2.Distance(currentPos, portalPoint);

                if (!gScore.TryGetValue(portal.ToPolyId, out var existingG) || tentativeG < existingG)
                {
                    gScore[portal.ToPolyId] = tentativeG;
                    cameFromPortal[portal.ToPolyId] = portal;
                    cameFromPos[portal.ToPolyId] = portalPoint;
                    var f = tentativeG + Vector2.Distance(portalPoint, snappedGoal);
                    open.Enqueue(portal.ToPolyId, f);
                }
            }
        }

        if (!found)
        {
            return NavPathResult.Unreachable;
        }

        // Reconstruct traversed portals in forward order.
        var portals = new List<NavPortal>();
        var walk = goalPoly.Id;
        while (walk != startPoly.Id)
        {
            var portal = cameFromPortal[walk];
            portals.Add(portal);
            walk = portal.FromPolyId;
        }

        portals.Reverse();

        var waypoints = SmoothWithFunnel(mesh, snappedStart, snappedGoal, portals);
        var totalDistance = ComputePolylineLength(waypoints);
        return new NavPathResult(true, waypoints, portals, totalDistance);
    }

    public static float ComputePathCost(
        NavMesh mesh,
        Vector2 start,
        Vector2 goal,
        IReadOnlySet<string>? closedDoorRooms = null)
        => FindPath(mesh, start, goal, closedDoorRooms).TotalDistance;

    private static IReadOnlyList<Vector2> SmoothWithFunnel(
        NavMesh mesh,
        Vector2 start,
        Vector2 goal,
        IReadOnlyList<NavPortal> portals)
    {
        // Build ordered portal pairs oriented relative to the travel direction,
        // inset slightly from exact polygon vertices so paths clear wall/table corners.
        const float PortalCornerInset = 0.20f;
        var orderedPortals = new List<(Vector2 Left, Vector2 Right)>(portals.Count + 1);
        var prev = start;
        foreach (var portal in portals)
        {
            var mid = portal.Midpoint;
            var fromPoly = mesh.GetPolygon(portal.FromPolyId);
            var toPoly = mesh.GetPolygon(portal.ToPolyId);
            var dir = fromPoly is not null && toPoly is not null
                ? toPoly.Centroid - fromPoly.Centroid
                : mid - prev;
            var pLeft = portal.Left;
            var pRight = portal.Right;
            var span = pRight - pLeft;
            var spanLen = span.Length();
            if (spanLen > PortalCornerInset * 2.5f)
            {
                var unit = span / spanLen;
                pLeft += unit * PortalCornerInset;
                pRight -= unit * PortalCornerInset;
            }

            if (Cross(dir, portal.Right - portal.Left) >= 0)
            {
                orderedPortals.Add((pRight, pLeft));
            }
            else
            {
                orderedPortals.Add((pLeft, pRight));
            }

            prev = mid;
        }

        orderedPortals.Add((goal, goal));

        // Simple Stupid Funnel Algorithm for 2D polygon portals.
        var result = new List<Vector2> { start };
        var apex = start;
        var left = start;
        var right = start;
        var apexIndex = 0;
        var leftIndex = 0;
        var rightIndex = 0;

        for (var i = 0; i < orderedPortals.Count; i++)
        {
            var pLeft = orderedPortals[i].Left;
            var pRight = orderedPortals[i].Right;

            // Update right boundary.
            if (TriArea2(apex, right, pRight) <= 0f)
            {
                if (SamePoint(apex, right) || TriArea2(apex, left, pRight) > 0f)
                {
                    right = pRight;
                    rightIndex = i;
                }
                else
                {
                    if (!SamePoint(result[^1], left))
                    {
                        result.Add(left);
                    }

                    apex = left;
                    apexIndex = leftIndex;
                    left = apex;
                    right = apex;
                    leftIndex = apexIndex;
                    rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }

            // Update left boundary.
            if (TriArea2(apex, left, pLeft) >= 0f)
            {
                if (SamePoint(apex, left) || TriArea2(apex, right, pLeft) < 0f)
                {
                    left = pLeft;
                    leftIndex = i;
                }
                else
                {
                    if (!SamePoint(result[^1], right))
                    {
                        result.Add(right);
                    }

                    apex = right;
                    apexIndex = rightIndex;
                    left = apex;
                    right = apex;
                    leftIndex = apexIndex;
                    rightIndex = apexIndex;
                    i = apexIndex;
                    continue;
                }
            }
        }

        if (!SamePoint(result[^1], goal))
        {
            result.Add(goal);
        }

        // Remove redundant collinear intermediate points.
        if (result.Count <= 2)
        {
            return result;
        }

        var pruned = new List<Vector2>(result.Count) { result[0] };
        for (var i = 1; i < result.Count - 1; i++)
        {
            var d1 = Vector2.Normalize(result[i] - pruned[^1]);
            var d2 = Vector2.Normalize(result[i + 1] - result[i]);
            if (Vector2.Dot(d1, d2) < 0.9995f)
            {
                pruned.Add(result[i]);
            }
        }

        pruned.Add(result[^1]);
        return pruned;
    }

    private static Vector2 ProjectPointOntoSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f)
        {
            return a;
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return a + (ab * t);
    }

    private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    private static float TriArea2(Vector2 a, Vector2 b, Vector2 c)
        => ((c.X - a.X) * (b.Y - a.Y)) - ((b.X - a.X) * (c.Y - a.Y));

    private static bool SamePoint(Vector2 a, Vector2 b)
        => Vector2.DistanceSquared(a, b) < 1e-6f;

    private static float ComputePolylineLength(IReadOnlyList<Vector2> points)
    {
        var total = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            total += Vector2.Distance(points[i - 1], points[i]);
        }

        return total;
    }
}
