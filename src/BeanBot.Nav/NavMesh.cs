using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BeanBot.Nav;

public sealed record NavPolygon(
    int Id,
    string Name,
    string Room,
    IReadOnlyList<Vector2> Vertices,
    Vector2 Centroid);

public sealed record NavPortal(
    int FromPolyId,
    int ToPolyId,
    Vector2 Left,
    Vector2 Right,
    int? DoorId,
    string? DoorRoom)
{
    public Vector2 Midpoint => (Left + Right) * 0.5f;
}

public sealed record NavVent(
    int Id,
    string Name,
    string Room,
    Vector2 Position,
    IReadOnlyList<int> Links);

public sealed record NavDoor(
    int Id,
    string Room,
    Vector2 Position);

/// <summary>
/// Hand-authored 2D convex polygon navigation mesh with portals, doors, vents, and LOS queries.
/// Ground Rule G1: zero protocol/transport dependencies.
/// </summary>
public sealed class NavMesh
{
    private readonly Dictionary<int, NavPolygon> _polygonsById;
    private readonly Dictionary<int, List<NavPortal>> _adjacency;

    public byte MapId { get; }
    public string MapName { get; }
    public Vector2 InitialSpawnCenter { get; }
    public Vector2 MeetingSpawnCenter { get; }
    public float SpawnRadius { get; }
    public IReadOnlyList<NavPolygon> Polygons { get; }
    public IReadOnlyList<NavPortal> Portals { get; }
    public IReadOnlyList<NavVent> Vents { get; }
    public IReadOnlyList<NavDoor> Doors { get; }

    public NavMesh(
        byte mapId,
        string mapName,
        Vector2 initialSpawnCenter,
        Vector2 meetingSpawnCenter,
        float spawnRadius,
        IReadOnlyList<NavPolygon> polygons,
        IReadOnlyList<NavPortal> portals,
        IReadOnlyList<NavVent> vents,
        IReadOnlyList<NavDoor> doors)
    {
        MapId = mapId;
        MapName = mapName;
        InitialSpawnCenter = initialSpawnCenter;
        MeetingSpawnCenter = meetingSpawnCenter;
        SpawnRadius = spawnRadius;
        Polygons = polygons;
        Portals = portals;
        Vents = vents;
        Doors = doors;

        _polygonsById = polygons.ToDictionary(p => p.Id);
        _adjacency = polygons.ToDictionary(p => p.Id, _ => new List<NavPortal>());

        // Build bidirectional adjacency so A* can traverse portals either way cleanly.
        foreach (var portal in portals)
        {
            if (_adjacency.TryGetValue(portal.FromPolyId, out var fromList))
            {
                fromList.Add(portal);
            }

            if (_adjacency.TryGetValue(portal.ToPolyId, out var toList))
            {
                toList.Add(new NavPortal(
                    portal.ToPolyId,
                    portal.FromPolyId,
                    portal.Right,
                    portal.Left,
                    portal.DoorId,
                    portal.DoorRoom));
            }
        }
    }

    public static NavMesh LoadSkeld()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "BeanBot.Nav.Maps.Skeld.nav";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Skeld navmesh resource '{resourceName}' not found.");
        return LoadFromStream(stream);
    }

    public static NavMesh LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    public static NavMesh LoadFromStream(Stream stream)
    {
        var dto = JsonSerializer.Deserialize<NavMeshDto>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("Failed to deserialize navmesh JSON.");

        var polygons = dto.Polygons.Select(p =>
        {
            var verts = p.Vertices.Select(v => new Vector2(v.X, v.Y)).ToArray();
            var centroid = verts.Aggregate(Vector2.Zero, (acc, v) => acc + v) / verts.Length;
            return new NavPolygon(p.Id, p.Name, p.Room, verts, centroid);
        }).ToArray();

        var portals = dto.Portals.Select(pt => new NavPortal(
            pt.From,
            pt.To,
            new Vector2(pt.Left.X, pt.Left.Y),
            new Vector2(pt.Right.X, pt.Right.Y),
            pt.DoorId,
            pt.DoorRoom)).ToArray();

        var vents = dto.Vents.Select(v => new NavVent(
            v.Id,
            v.Name,
            v.Room,
            new Vector2(v.X, v.Y),
            v.Links)).ToArray();

        var doors = dto.Doors.Select(d => new NavDoor(
            d.Id,
            d.Room,
            new Vector2(d.X, d.Y))).ToArray();

        return new NavMesh(
            dto.MapId,
            dto.MapName,
            new Vector2(dto.InitialSpawnCenter.X, dto.InitialSpawnCenter.Y),
            new Vector2(dto.MeetingSpawnCenter.X, dto.MeetingSpawnCenter.Y),
            dto.SpawnRadius,
            polygons,
            portals,
            vents,
            doors);
    }

    public IReadOnlyList<NavPortal> GetPortalsFrom(int polyId)
        => _adjacency.TryGetValue(polyId, out var list) ? list : Array.Empty<NavPortal>();

    public NavPolygon? GetPolygon(int polyId)
        => _polygonsById.TryGetValue(polyId, out var poly) ? poly : null;

    public NavPolygon? FindContainingPolygon(Vector2 point)
    {
        foreach (var poly in Polygons)
        {
            if (IsPointInConvexPolygon(point, poly.Vertices))
            {
                return poly;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the point if it lies on the navmesh, otherwise snaps to the nearest polygon boundary point (F5).
    /// </summary>
    public (Vector2 SnappedPoint, NavPolygon Polygon, bool WasSnapped) SnapToNavMesh(Vector2 point, ILogger? logger = null)
    {
        var poly = FindContainingPolygon(point);
        if (poly is not null)
        {
            return (point, poly, false);
        }

        NavPolygon? nearest = null;
        var bestPoint = Vector2.Zero;
        var bestDistSq = float.MaxValue;
        foreach (var candidate in Polygons)
        {
            var verts = candidate.Vertices;
            for (var i = 0; i < verts.Count; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % verts.Count];
                var proj = ProjectPointOntoSegment(point, a, b);
                var distSq = Vector2.DistanceSquared(point, proj);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = candidate;
                    bestPoint = proj;
                }
            }
        }

        if (nearest is null)
        {
            throw new InvalidOperationException("NavMesh contains no polygons.");
        }

        // Nudge slightly toward the polygon centroid so boundary-projected points lie strictly inside the polygon.
        var toCentroid = nearest.Centroid - bestPoint;
        var len = toCentroid.Length();
        if (len > 1e-4f)
        {
            var candidatePoint = bestPoint + (toCentroid / len) * MathF.Min(0.05f, len * 0.25f);
            if (IsPointInConvexPolygon(candidatePoint, nearest.Vertices))
            {
                bestPoint = candidatePoint;
            }
        }

        logger?.LogWarning(
            "Point ({X:F2}, {Y:F2}) is outside navmesh; snapped to nearest polygon '{Poly}' at ({SX:F2}, {SY:F2}).",
            point.X, point.Y, nearest.Name, bestPoint.X, bestPoint.Y);

        return (bestPoint, nearest, true);
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

    public string GetRoomAt(Vector2 point)
    {
        var poly = FindContainingPolygon(point);
        if (poly is not null)
        {
            return poly.Room;
        }

        return SnapToNavMesh(point).Polygon.Room;
    }

    /// <summary>
    /// Checks whether the straight segment [a, b] stays within walkable navmesh polygons and crosses no closed doors.
    /// </summary>
    public bool HasLineOfSight(Vector2 a, Vector2 b, IReadOnlySet<string>? closedDoorRooms = null)
    {
        var dist = Vector2.Distance(a, b);
        if (dist < 1e-4f)
        {
            return FindContainingPolygon(a) is not null;
        }

        var steps = Math.Clamp((int)MathF.Ceiling(dist / 0.25f), 2, 200);
        var prevPoly = FindContainingPolygon(a);
        if (prevPoly is null)
        {
            return false;
        }

        for (var i = 1; i <= steps; i++)
        {
            var t = (float)i / steps;
            var p = Vector2.Lerp(a, b, t);
            var curPoly = FindContainingPolygon(p);
            if (curPoly is null)
            {
                return false;
            }

            if (curPoly.Id != prevPoly.Id && closedDoorRooms is { Count: > 0 })
            {
                var portal = _adjacency[prevPoly.Id].FirstOrDefault(pt => pt.ToPolyId == curPoly.Id);
                if (portal is not null && IsPortalBlocked(portal, closedDoorRooms))
                {
                    return false;
                }
            }

            prevPoly = curPoly;
        }

        return true;
    }

    public static bool IsPortalBlocked(NavPortal portal, IReadOnlySet<string>? closedDoorRooms)
    {
        if (closedDoorRooms is null || closedDoorRooms.Count == 0 || string.IsNullOrEmpty(portal.DoorRoom))
        {
            return false;
        }

        return closedDoorRooms.Contains(portal.DoorRoom);
    }

    public NavVent? FindNearestVent(Vector2 position, float maxDistance = float.MaxValue)
    {
        NavVent? best = null;
        var bestDistSq = maxDistance == float.MaxValue ? float.MaxValue : maxDistance * maxDistance;
        foreach (var vent in Vents)
        {
            var d = Vector2.DistanceSquared(position, vent.Position);
            if (d <= bestDistSq)
            {
                bestDistSq = d;
                best = vent;
            }
        }

        return best;
    }

    private static bool IsPointInConvexPolygon(Vector2 point, IReadOnlyList<Vector2> verts)
    {
        if (verts.Count < 3)
        {
            return false;
        }

        const float epsilon = -1e-4f;
        for (var i = 0; i < verts.Count; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % verts.Count];
            var cross = ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));
            if (cross < epsilon)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class NavMeshDto
    {
        [JsonPropertyName("mapId")] public byte MapId { get; set; }
        [JsonPropertyName("mapName")] public string MapName { get; set; } = "";
        [JsonPropertyName("initialSpawnCenter")] public VecDto InitialSpawnCenter { get; set; } = new();
        [JsonPropertyName("meetingSpawnCenter")] public VecDto MeetingSpawnCenter { get; set; } = new();
        [JsonPropertyName("spawnRadius")] public float SpawnRadius { get; set; }
        [JsonPropertyName("polygons")] public List<PolyDto> Polygons { get; set; } = new();
        [JsonPropertyName("portals")] public List<PortalDto> Portals { get; set; } = new();
        [JsonPropertyName("vents")] public List<VentDto> Vents { get; set; } = new();
        [JsonPropertyName("doors")] public List<DoorDto> Doors { get; set; } = new();
    }

    private sealed class VecDto
    {
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
    }

    private sealed class PolyDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("room")] public string Room { get; set; } = "";
        [JsonPropertyName("vertices")] public List<VecDto> Vertices { get; set; } = new();
    }

    private sealed class PortalDto
    {
        [JsonPropertyName("from")] public int From { get; set; }
        [JsonPropertyName("to")] public int To { get; set; }
        [JsonPropertyName("left")] public VecDto Left { get; set; } = new();
        [JsonPropertyName("right")] public VecDto Right { get; set; } = new();
        [JsonPropertyName("doorId")] public int? DoorId { get; set; }
        [JsonPropertyName("doorRoom")] public string? DoorRoom { get; set; }
    }

    private sealed class VentDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("room")] public string Room { get; set; } = "";
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("links")] public List<int> Links { get; set; } = new();
    }

    private sealed class DoorDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("room")] public string Room { get; set; } = "";
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
    }
}
