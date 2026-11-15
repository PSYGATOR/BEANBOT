using System.Numerics;

namespace BeanBot.World;

public enum PlayerRole
{
    Unknown,
    Crewmate,
    Impostor,
    CrewmateGhost,
    ImpostorGhost,
}

public sealed class PlayerState
{
    public required int ClientId { get; init; }
    public byte PlayerId { get; set; } = byte.MaxValue;
    public uint NetId { get; set; }
    public uint TransformNetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ColorName { get; set; } = string.Empty;
    public PlayerRole Role { get; set; } = PlayerRole.Crewmate;
    public bool IsAlive { get; set; } = true;
    public Vector2 Position { get; set; }
    public Vector2 Facing { get; set; } = Vector2.UnitX;
    public long LastSeenTick { get; set; }
    public ushort LastTransformSequenceId { get; set; }
    public string Puid { get; set; } = string.Empty;
    public bool IsHost { get; set; }
    public bool InOnlineScene { get; set; }
    public bool IsReady { get; set; }

    public bool IsImpostor => Role is PlayerRole.Impostor or PlayerRole.ImpostorGhost;
}

public sealed record DeadBodyState(byte PlayerId, Vector2 Position, bool Reported = false);
