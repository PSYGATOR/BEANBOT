using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BeanBot.World;

public sealed record SyncSettingsModel
{
    public float PlayerSpeed { get; init; } = 1.0f;
    public float KillCooldownSeconds { get; init; } = 15.0f;
    public float KillDistanceUnits { get; init; } = 1.5f; // Short=1.0, Normal=1.5, Long=2.5
    public float VentCooldownSeconds { get; init; } = 15.0f;
    public string MapName { get; init; } = "Skeld";
    public byte MaxPlayers { get; init; } = 15;
    public int NumImpostors { get; init; } = 1;
    public int DiscussionTimeSeconds { get; init; } = 15;
    public int VotingTimeSeconds { get; init; } = 120;
    public bool ConfirmEjects { get; init; } = true;
    public bool VisualTasks { get; init; } = false;
    public bool AnonymousVotes { get; init; } = false;
}

public sealed class MeetingState
{
    public const sbyte SkipVoteId = -1;
    public const sbyte NotVotedId = -2;

    public byte CallerPlayerId { get; set; } = byte.MaxValue;
    public byte? ReportedBodyId { get; set; }
    public Dictionary<byte, sbyte> VotesByVoter { get; } = new();
    public bool IsVotingComplete { get; set; }
    public byte? ExiledPlayerId { get; set; }
    public bool IsTie { get; set; }

    public void Reset(byte callerPlayerId, byte? reportedBodyId)
    {
        CallerPlayerId = callerPlayerId;
        ReportedBodyId = reportedBodyId;
        VotesByVoter.Clear();
        IsVotingComplete = false;
        ExiledPlayerId = null;
        IsTie = false;
    }
}

/// <summary>
/// Authoritative per-match mutable world state (Section 8).
/// Contains zero protocol/wire constants (G1).
/// </summary>
public sealed class WorldModel
{
    private readonly PhaseMachine _phaseMachine;
    private ushort _transformSequenceId;

    public WorldModel(ILogger<PhaseMachine>? phaseLogger = null)
    {
        _phaseMachine = new PhaseMachine(phaseLogger);
    }

    public string LobbyCode { get; set; } = string.Empty;
    public bool IsPublicLobby { get; set; }
    public int LocalClientId { get; set; }
    public byte LocalPlayerId { get; set; } = byte.MaxValue;
    public int HostClientId { get; set; }
    public GamePhase Phase => _phaseMachine.Current;
    public IReadOnlyList<PhaseTransitionRecord> PhaseHistory => _phaseMachine.History;

    public ConcurrentDictionary<int, PlayerState> PlayersByClientId { get; } = new();
    public List<DeadBodyState> DeadBodies { get; } = new();
    public List<AssignedTask> LocalTasks { get; } = new();
    public HashSet<string> ClosedDoorRooms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? ActiveSabotageSystem { get; set; }
    public Vector2? ActiveSabotagePosition { get; set; }
    public SyncSettingsModel Settings { get; set; } = new();
    public MeetingState Meeting { get; } = new();

    public long CurrentTick { get; set; }
    public double TickRateHz { get; set; } = 10.0;
    public double LastKillTimestampSec { get; set; } = -1000.0;
    public double LastVentTimestampSec { get; set; } = -1000.0;
    public double MatchTimeSec { get; set; }
    public bool InVent { get; set; }
    public int? CurrentVentId { get; set; }
    public int LastStartCounter { get; set; } = -1;

    public PlayerState? LocalPlayer =>
        PlayersByClientId.TryGetValue(LocalClientId, out var p) ? p : null;

    public PlayerRole LocalRole => LocalPlayer?.Role ?? PlayerRole.Crewmate;

    public bool IsLocalAlive => LocalPlayer?.IsAlive ?? false;

    public PlayerState? FindPlayerByPlayerId(byte playerId) =>
        PlayersByClientId.Values.FirstOrDefault(p => p.PlayerId == playerId);

    public PlayerState? FindPlayerByName(string name) =>
        PlayersByClientId.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a player target by exact name, <c>#playerId</c>/<c>playerId</c>, or unambiguous prefix/substring,
    /// optionally excluding the local player.
    /// </summary>
    public PlayerState? FindPlayerByQuery(string query, bool excludeLocal = true)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        var pool = PlayersByClientId.Values
            .Where(p => p.ClientId > 0 && (!excludeLocal || p.ClientId != LocalClientId))
            .ToArray();
        var initialized = pool.Where(p => p.PlayerId != byte.MaxValue).ToArray();
        var candidates = initialized.Length > 0 ? initialized : pool;

        var exact = candidates.FirstOrDefault(p => p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var idText = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        if (byte.TryParse(idText, out var pid) && pid != byte.MaxValue)
        {
            var byPid = candidates.FirstOrDefault(p => p.PlayerId == pid);
            if (byPid is not null)
            {
                return byPid;
            }
        }

        var prefixMatches = candidates.Where(p => p.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefixMatches.Length == 1)
        {
            return prefixMatches[0];
        }

        var containsMatches = candidates.Where(p => p.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToArray();
        return containsMatches.Length == 1 ? containsMatches[0] : null;
    }

    public ushort NextTransformSequenceId() => ++_transformSequenceId;

    public ushort AdvanceTransformSequenceBy(ushort delta)
    {
        _transformSequenceId = unchecked((ushort)(_transformSequenceId + delta));
        return _transformSequenceId;
    }

    public void SyncTransformSequenceId(ushort remoteSequenceId)
    {
        if (IsSequenceNewer(remoteSequenceId, _transformSequenceId))
        {
            _transformSequenceId = remoteSequenceId;
        }
    }

    public static bool IsSequenceNewer(ushort candidate, ushort current)
    {
        var threshold = (ushort)(current + 32767);
        return current < threshold
            ? candidate > current && candidate <= threshold
            : candidate > current || candidate <= threshold;
    }

    public bool TransitionPhase(GamePhase next, string triggerPacketName, byte? triggerPacketId)
    {
        return _phaseMachine.Transition(next, triggerPacketName, triggerPacketId, CurrentTick);
    }

    public void UpdatePlayerPosition(int clientId, Vector2 pos, ushort? sequenceId = null)
    {
        if (!PlayersByClientId.TryGetValue(clientId, out var player))
        {
            return;
        }

        var delta = pos - player.Position;
        if (delta.LengthSquared() > 0.0001f)
        {
            player.Facing = Vector2.Normalize(delta);
        }

        player.Position = pos;
        player.LastSeenTick = CurrentTick;
        if (sequenceId.HasValue)
        {
            player.LastTransformSequenceId = sequenceId.Value;
        }
    }

    public double TaskCompletionRatio
    {
        get
        {
            if (LocalTasks.Count == 0) return 1.0;
            return LocalTasks.Count(t => t.IsCompleted) / (double)LocalTasks.Count;
        }
    }

    public string ComputeStateHash()
    {
        var sb = new StringBuilder();
        sb.Append(Phase).Append('|').Append(LobbyCode).Append('|').Append(CurrentTick).Append('|');
        foreach (var p in PlayersByClientId.Values.OrderBy(p => p.ClientId))
        {
            sb.Append(p.ClientId).Append(':').Append(p.PlayerId).Append(':')
              .Append(p.IsAlive ? 'A' : 'D').Append(':')
              .Append(MathF.Round(p.Position.X, 2)).Append(',').Append(MathF.Round(p.Position.Y, 2)).Append(';');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}
