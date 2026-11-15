using System.Numerics;

namespace BeanBot.Behaviors;

public abstract record BotAction;

public sealed record HoldPositionAction(string Reason) : BotAction;

public sealed record MoveToAction(
    Vector2 Destination,
    string Reason,
    float StopDistance = 0.15f) : BotAction;

public sealed record CompleteTaskAction(
    uint TaskId,
    int StepIndex,
    Vector2 Position,
    bool IsFakeTask) : BotAction;

public sealed record KillAction(byte TargetPlayerId, string? PostKillEscapeReason = null) : BotAction;

public sealed record EnterVentAction(int VentId) : BotAction;

public sealed record ExitVentAction(int VentId) : BotAction;

public sealed record ReportBodyAction(byte BodyPlayerId) : BotAction;

public sealed record CastVoteAction(sbyte TargetPlayerId) : BotAction;

public sealed record ClearVoteAction() : BotAction;

/// <summary>
/// Per-bot mutable behavior scratchpad (kept separate from WorldModel and free of protocol IDs).
/// </summary>
public sealed class BotBehaviorState
{
    public bool TasksEnabled { get; set; } = true;
    public byte? FollowTargetPlayerId { get; set; }
    public string? FollowTargetName { get; set; }
    public float FollowDistance { get; set; } = 1.2f;
    public bool FollowKillRangeHold { get; set; }
    public double TooCloseAccumulatedSec { get; set; }
    public double TargetDeathHoldRemainingSec { get; set; }
    public bool WasFollowingTargetAlive { get; set; } = true;

    public byte? CommandedKillTargetPlayerId { get; set; }
    public bool CommandedKillAllowBlind { get; set; }

    public int? CommandedVentId { get; set; }
    public int? PendingVentExitId { get; set; }
    public double? VentEnteredAtMatchSec { get; set; }

    public sbyte? CommandedVoteTargetId { get; set; }
    public bool HasVotedThisMeeting { get; set; }

    public uint? ActiveTaskId { get; set; }
    public double TaskDwellRemainingSec { get; set; }
    public double TaskDwellDurationSec { get; set; } = 2.5;
    public double IntroSettleDurationSec { get; set; } = 4.5;
    public double IntroSettleRemainingSec { get; set; }

    public double MatchElapsedInCurrentMeetingSec { get; set; }

    public void ResetForNewMeeting()
    {
        HasVotedThisMeeting = false;
        CommandedVoteTargetId = null;
        PendingVentExitId = null;
        VentEnteredAtMatchSec = null;
        CommandedVentId = null;
        MatchElapsedInCurrentMeetingSec = 0;
    }

    public void ResetForNewMatch(bool clearFollow = true)
    {
        if (clearFollow)
        {
            FollowTargetPlayerId = null;
            FollowTargetName = null;
            FollowKillRangeHold = false;
        }
        else
        {
            // Force re-resolution against match PlayerState once roles/ids sync.
            FollowTargetPlayerId = null;
        }

        IntroSettleRemainingSec = IntroSettleDurationSec;
        TooCloseAccumulatedSec = 0;
        TargetDeathHoldRemainingSec = 0;
        WasFollowingTargetAlive = true;
        CommandedKillTargetPlayerId = null;
        CommandedKillAllowBlind = false;
        CommandedVentId = null;
        PendingVentExitId = null;
        VentEnteredAtMatchSec = null;
        CommandedVoteTargetId = null;
        HasVotedThisMeeting = false;
        ActiveTaskId = null;
        TaskDwellRemainingSec = 0;
        MatchElapsedInCurrentMeetingSec = 0;
    }
}
