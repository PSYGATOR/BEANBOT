using BeanBot.World;

namespace BeanBot.Behaviors;

/// <summary>
/// Meeting voting policy (P1.8 / Appendix D): honors commanded vote immediately or evaluates suspicion/skip after discussion.
/// </summary>
public static class Vote
{
    public static IReadOnlyList<BotAction> Evaluate(
        WorldModel world,
        BotBehaviorState state)
    {
        var local = world.LocalPlayer;
        if (world.Phase != GamePhase.Meeting ||
            local is null ||
            !local.IsAlive ||
            world.Meeting.IsVotingComplete ||
            state.HasVotedThisMeeting)
        {
            return Array.Empty<BotAction>();
        }

        if (state.CommandedVoteTargetId.HasValue)
        {
            state.HasVotedThisMeeting = true;
            return new BotAction[] { new CastVoteAction(state.CommandedVoteTargetId.Value) };
        }

        // Wait until discussion period ends before casting autonomous vote.
        if (state.MatchElapsedInCurrentMeetingSec < world.Settings.DiscussionTimeSeconds)
        {
            return Array.Empty<BotAction>();
        }

        var livingCount = world.PlayersByClientId.Values.Count(p => p.IsAlive);
        var tally = new Dictionary<sbyte, int>();
        foreach (var vote in world.Meeting.VotesByVoter.Values)
        {
            if (vote >= 0)
            {
                tally[vote] = tally.GetValueOrDefault(vote) + 1;
            }
        }

        // Default to Skip unless a candidate already has strong plurality (>= half of living players rounded up, >= 2).
        var chosenVote = MeetingState.SkipVoteId;
        var top = tally.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (tally.Count > 0 && top.Value >= Math.Max(2, (livingCount + 1) / 3) && top.Key != (sbyte)local.PlayerId)
        {
            chosenVote = top.Key;
        }

        state.HasVotedThisMeeting = true;
        return new BotAction[] { new CastVoteAction(chosenVote) };
    }
}
