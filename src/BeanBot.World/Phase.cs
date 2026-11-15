using Microsoft.Extensions.Logging;

namespace BeanBot.World;

public enum GamePhase
{
    Lobby,
    Dropship,
    Tasks,
    Meeting,
    End,
}

public sealed record PhaseTransitionRecord(
    GamePhase From,
    GamePhase To,
    string TriggerPacketName,
    byte? TriggerPacketId,
    long Tick);

/// <summary>
/// Match phase machine driven strictly by inbound packet events, never by timers (Section 8).
/// Logs every transition with the triggering packet.
/// </summary>
public sealed class PhaseMachine
{
    private readonly List<PhaseTransitionRecord> _history = new();
    private readonly ILogger<PhaseMachine>? _logger;

    public PhaseMachine(ILogger<PhaseMachine>? logger = null)
    {
        _logger = logger;
    }

    public GamePhase Current { get; private set; } = GamePhase.Lobby;
    public IReadOnlyList<PhaseTransitionRecord> History => _history;

    public bool Transition(GamePhase next, string triggerPacketName, byte? triggerPacketId, long tick)
    {
        if (Current == next)
        {
            return false;
        }

        var record = new PhaseTransitionRecord(Current, next, triggerPacketName, triggerPacketId, tick);
        _history.Add(record);
        Current = next;

        _logger?.LogInformation(
            "Phase transition {From} -> {To} at tick {Tick} triggered by packet {PacketName} (id={PacketId}).",
            record.From, record.To, record.Tick, record.TriggerPacketName,
            record.TriggerPacketId.HasValue ? $"0x{record.TriggerPacketId.Value:x2}" : "none");

        return true;
    }

    public void ResetToLobby(string triggerPacketName, byte? triggerPacketId, long tick)
    {
        Transition(GamePhase.Lobby, triggerPacketName, triggerPacketId, tick);
    }
}
