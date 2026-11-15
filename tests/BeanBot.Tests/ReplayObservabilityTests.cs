using BeanBot.Observability;
using BeanBot.Protocol;
using Xunit;

namespace BeanBot.Tests;

public sealed class ReplayObservabilityTests
{
    [Fact]
    public void ReplayLogger_And_Viewer_StepAndFilterCleanly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"beanbot-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var counters = new Counters();
            counters.IncrementJoinAttempt();
            counters.IncrementJoinSuccess();
            counters.SetSeatsHeld(15);

            using (var logger = new ReplayLogger(tempFile, ProtocolRegistry.Default, counters))
            {
                logger.LogAction("bot-00", "HostCreate", RootMessageId.HostGame, ReplayLogger.ComputeStateHash("phase=Lobby;code=ABCDEF"), "created room");
                logger.LogAction("bot-01", "JoinSeat", RootMessageId.JoinedGame, ReplayLogger.ComputeStateHash("phase=Lobby;seats=2"));
                logger.LogAction("bot-01", "CompleteTask", RootMessageId.GameData, ReplayLogger.ComputeStateHash("phase=Tasks;task=3"), "taskId=3");
            }

            Assert.Equal(3, counters.ReplaysWritten);
            Assert.Equal(15, counters.SeatsHeld);

            var viewer = ReplayViewer.Load(tempFile);
            Assert.Equal(3, viewer.Count);
            Assert.Equal("HostCreate", viewer.Current!.Action);
            Assert.Equal("HostGame", viewer.Current.CausePacketName);

            Assert.Equal("JoinSeat", viewer.Next()!.Action);
            Assert.Equal("CompleteTask", viewer.Next()!.Action);
            Assert.Equal("JoinSeat", viewer.Prev()!.Action);
            Assert.Equal("HostCreate", viewer.Goto(0)!.Action);

            var matches = viewer.Filter("GameData");
            Assert.Single(matches);
            Assert.Equal("CompleteTask", matches[0].Action);

            // Interactive stepper smoke test
            using var input = new StringReader("n\nf JoinSeat\nq\n");
            using var output = new StringWriter();
            viewer.RunInteractive(input, output);
            var text = output.ToString();
            Assert.Contains("Loaded 3 replay entries", text);
            Assert.Contains("Matches (1):", text);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
