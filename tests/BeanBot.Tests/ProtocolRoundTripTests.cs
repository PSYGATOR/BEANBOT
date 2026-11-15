using System.Net;
using System.Numerics;
using BeanBot.Protocol;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.InnerNetObjects;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;
using Xunit;

namespace BeanBot.Tests;

public sealed class ProtocolRoundTripTests
{
    private static readonly ProtocolRegistry Registry = ProtocolRegistry.Default;

    [Fact]
    public void Registry_CoversAll54RootMessagesAnd55Rpcs()
    {
        Registry.AssertSpecCoverage();
        Assert.Equal(17, RootMessageId.Spec54Ids.Length);
        Assert.Equal(31, RpcId.Spec55Ids.Length);
    }

    public static IEnumerable<object[]> AllRootMessages()
    {
        var code = GameCode.From("ABCDEF");
        var options = new NormalGameOptions
        {
            MaxPlayers = 15,
            Map = MapTypes.Skeld,
            NumImpostors = 1,
            PlayerSpeedMod = 1.0f,
            ConfirmImpostor = true,
            VisualTasks = false,
            KillDistance = KillDistances.Normal,
        };
        var filters = GameFilterOptions.CreateDefault();
        var subPayload = GameDataSubMessage.SerializeAll([
            GameDataSubMessage.FromRpc(42, new CompleteTaskRpc(3)),
            GameDataSubMessage.FromTransform(43, new CustomNetworkTransformUpdate(12, [new Vector2(1.5f, -2.25f)])),
        ]);

        yield return [new HostGameMessage(GameOptionsOrCode.FromRequest(options, CrossplayFlags.All, filters))];
        yield return [new HostGameMessage(GameOptionsOrCode.FromResponse(code))];
        yield return [new JoinGameMessage(code, NoCrossplay: false)];
        yield return [new StartGameMessage(code)];
        yield return [new RemoveGameMessage(code)];
        yield return [new RemovePlayerMessage(code, 7, 1, DisconnectReason.Kicked)];
        yield return [new GameDataMessage(code, subPayload)];
        yield return [new GameDataToMessage(code, 9, subPayload)];
        yield return [new JoinedGameMessage(code, 5, 1, [
            new JoinedPlayerEntry(1, "HostBot", PlatformRecord.Build("HostBot", "11111111-1111-4111-8111-111111111111"), 10, "puid-host", "host#0001"),
            new JoinedPlayerEntry(2, "Bot02", PlatformRecord.Build("Bot02", "22222222-2222-4222-8222-222222222222"), 3, "puid-2", "bot2#0002"),
        ])];
        yield return [new EndGameMessage(code, GameOverReason.CrewmatesByTask, ShowAd: false)];
        yield return [new GetGameListMessage(true, options)];
        yield return [new AlterGameMessage(code, AlterGameTags.ChangePrivacy, IsPublic: true)];
        yield return [new KickPlayerMessage(code, 4, IsBan: false)];
        yield return [new WaitForHostMessage(code, 5)];
        yield return [new RedirectMessage(IPAddress.Parse("198.51.100.42"), 22026)];
        yield return [new ReselectServerMessage([0x01, 0x02, 0x03, 0x04])];
        yield return [new GetGameListV2Message(2, options, QuickChatModes.QuickChatOnly, CrossplayFlags.All, filters)];
        yield return [new ReportPlayerMessage(code, 8, 2)];
    }

    [Theory]
    [MemberData(nameof(AllRootMessages))]
    public void RootMessage_RoundTrips_Identically(IRootMessage original)
    {
        var bytes = Registry.EncodeRoot(original);
        var decoded = Registry.DecodeRoot(bytes);
        Assert.Equal(original, decoded);

        var reEncoded = Registry.EncodeRoot(decoded);
        Assert.Equal(bytes, reEncoded);
    }

    public static IEnumerable<object[]> AllSpecRpcs()
    {
        var options = new NormalGameOptions { MaxPlayers = 15, Map = MapTypes.Skeld, VisualTasks = false };

        yield return [new PlayAnimationRpc(TaskTypes.SubmitScan)];
        yield return [new CompleteTaskRpc(4)];
        yield return [new SyncSettingsRpc(options)];
        yield return [new SetInfectedRpc([0, 3])];
        yield return [new ExiledRpc()];
        yield return [new CheckNameRpc("BeanBot01")];
        yield return [new SetNameRpc(101u, "BeanBot01")];
        yield return [new CheckColorRpc(ColorType.Coral)];
        yield return [new SetColorRpc(101u, ColorType.Red)];
        yield return [new SetHatRpc(12u)];
        yield return [new SetSkinRpc(5u)];
        yield return [new ReportDeadBodyRpc(2)];
        yield return [new MurderPlayerRpc(104u, MurderResultFlags.Succeeded | MurderResultFlags.DecisionByHost)];
        yield return [new SendChatRpc("Skip")];
        yield return [new StartMeetingRpc(byte.MaxValue)];
        yield return [new SetScannerRpc(true, 3)];
        yield return [new SendChatNoteRpc(1, ChatNoteType.DidVote)];
        yield return [new SetPetRpc(2u)];
        yield return [new SetStartCounterRpc(9, 3)];
        yield return [new EnterVentRpc(4)];
        yield return [new ExitVentRpc(7)];
        yield return [new SnapToRpc(new Vector2(-4.25f, 3.75f), 42)];
        yield return [new CloseMeetingRpc()];
        yield return [new VotingCompleteRpc([0x01, 0x02], 255, true, false, 0)];
        yield return [new CastVoteRpc(1, -1)];
        yield return [new ClearVoteRpc()];
        yield return [new AddVoteRpc(10, 12)];
        yield return [new CloseDoorsOfTypeRpc(SystemTypes.Cafeteria)];
        yield return [new RepairSystemRpc(SystemTypes.Reactor, 101u, 64)];
        yield return [new SetTasksRpc([1, 5, 8, 12])];
        yield return [new ClimbLadderRpc(2, 7)];
        yield return [new UsePlatformRpc()];
        yield return [new UpdateSystemRpc(SystemTypes.Electrical, 101u, 55, 1, 0)];
        yield return [new SetCosmeticStrRpc(RpcId.SetHatStr, "hat_astronaut", 1)];
        yield return [new SetCosmeticStrRpc(RpcId.SetSkinStr, "skin_astronaut", 2)];
        yield return [new SetCosmeticStrRpc(RpcId.SetPetStr, "pet_alien", 3)];
        yield return [new SetRoleRpc(RoleTypes.Impostor, true)];
        yield return [new CheckMurderRpc(108u)];
    }

    [Theory]
    [MemberData(nameof(AllSpecRpcs))]
    public void Rpc_RoundTrips_Identically(IRpcMessage original)
    {
        var bytes = Registry.EncodeRpc(original);
        var decoded = Registry.DecodeRpc(bytes);

        if (original is SnapToRpc expectedSnap && decoded is SnapToRpc actualSnap)
        {
            Assert.Equal(expectedSnap.MinSequenceId, actualSnap.MinSequenceId);
            Assert.InRange(Vector2.Distance(expectedSnap.Position, actualSnap.Position), 0f, 0.005f);
            return;
        }

        Assert.Equal(original, decoded);
        Assert.Equal(bytes, Registry.EncodeRpc(decoded));
    }

    [Fact]
    public void DisconnectAndHandshake_RoundTrip()
    {
        using var dcWriter = MessageWriter.Get(MessageType.Unreliable);
        var dc = new DisconnectMessage(true, DisconnectReason.Custom, "Version mismatch");
        dc.Serialize(dcWriter);
        var dcBytes = dcWriter.ToByteArray(includeHeader: false);
        using var dcReader = MessageReader.Get(dcBytes);
        Assert.Equal(dc, DisconnectMessage.Deserialize(dcReader));

        using var helloWriter = MessageWriter.Get(MessageType.Unreliable);
        PlatformRecord.WriteHandshake(helloWriter, "Bean00", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var helloBytes = helloWriter.ToByteArray(includeHeader: false);
        using var helloReader = MessageReader.Get(helloBytes);
        Assert.Equal(ProtocolVersion.CLIENT_VERSION.Value, helloReader.ReadInt32());
        Assert.Equal("Bean00", helloReader.ReadString());
        Assert.Equal(0u, helloReader.ReadUInt32());
        Assert.Equal((uint)Language.English, helloReader.ReadUInt32());
        Assert.Equal((byte)QuickChatModes.QuickChatOnly, helloReader.ReadByte());
        using var platReader = helloReader.ReadMessage();
        var plat = PlatformSpecificData.Deserialize(platReader);
        Assert.Equal(Platforms.Android, plat.Platform);
        Assert.Equal("Bean00", plat.PlatformName);
    }

    [Fact]
    public void VersionCompatibilityTable_MapsCurrentAndOlderVersions()
    {
        Assert.True(ProtocolVersion.AreCompatible(ProtocolVersion.CLIENT_VERSION, new GameVersion(2026, 7, 20)));
        Assert.True(ProtocolVersion.AreCompatible(ProtocolVersion.CLIENT_VERSION, new GameVersion(2026, 7, 21, 25)));
        Assert.False(ProtocolVersion.AreCompatible(ProtocolVersion.CLIENT_VERSION, new GameVersion(2026, 7, 16)));
    }

    [Fact]
    public void InnerNet_CustomNetworkTransform_And_GameDataSubMessages_RoundTrip()
    {
        var transform = new CustomNetworkTransformUpdate(42, [new Vector2(1.2f, -3.4f), new Vector2(1.5f, -3.1f)]);
        var sub = GameDataSubMessage.FromTransform(99u, transform);
        var packed = GameDataSubMessage.SerializeAll([sub]);
        var unpacked = GameDataSubMessage.ParseAll(packed);

        Assert.Single(unpacked);
        Assert.Equal(GameDataTag.Data, unpacked[0].Tag);

        using var r = MessageReader.Get(unpacked[0].Payload);
        Assert.Equal(99u, r.ReadPackedUInt32());
        var parsed = CustomNetworkTransformUpdate.Deserialize(r);
        Assert.Equal(transform.SequenceId, parsed.SequenceId);
        Assert.Equal(transform.Positions.Count, parsed.Positions.Count);
        Assert.InRange(Vector2.Distance(transform.Positions[0], parsed.Positions[0]), 0f, 0.005f);
        Assert.InRange(Vector2.Distance(transform.Positions[1], parsed.Positions[1]), 0f, 0.005f);
    }
}
