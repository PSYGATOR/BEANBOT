using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.RootMessages;
using BeanBot.Protocol.Rpc;

namespace BeanBot.Protocol;

public sealed record RootMessageDescriptor(
    byte Id,
    string Name,
    Func<IMessageReader, IRootMessage> Deserialize);

public sealed record RpcDescriptor(
    byte Id,
    string Name,
    Func<IMessageReader, IRpcMessage> Deserialize);

/// <summary>
/// Central registry for all RootMessages (5.4) and RPCs (5.5 + Impostor additions).
/// Zero magic ids live outside BeanBot.Protocol (G1, P0.2).
/// </summary>
public sealed class ProtocolRegistry
{
    private readonly Dictionary<byte, RootMessageDescriptor> _rootMessages = new();
    private readonly Dictionary<byte, RpcDescriptor> _rpcs = new();

    public static ProtocolRegistry Default { get; } = CreateDefault();

    public IReadOnlyDictionary<byte, RootMessageDescriptor> RootMessages => _rootMessages;
    public IReadOnlyDictionary<byte, RpcDescriptor> Rpcs => _rpcs;

    public static ProtocolRegistry CreateDefault()
    {
        var reg = new ProtocolRegistry();

        // Section 5.4 Root Messages (17/17)
        reg.RegisterRoot(RootMessageId.HostGame, nameof(RootMessageId.HostGame), HostGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.JoinGame, nameof(RootMessageId.JoinGame), JoinGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.StartGame, nameof(RootMessageId.StartGame), StartGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.RemoveGame, nameof(RootMessageId.RemoveGame), RemoveGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.RemovePlayer, nameof(RootMessageId.RemovePlayer), RemovePlayerMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.GameData, nameof(RootMessageId.GameData), GameDataMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.GameDataTo, nameof(RootMessageId.GameDataTo), GameDataToMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.JoinedGame, nameof(RootMessageId.JoinedGame), JoinedGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.EndGame, nameof(RootMessageId.EndGame), EndGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.GetGameList, nameof(RootMessageId.GetGameList), GetGameListMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.AlterGame, nameof(RootMessageId.AlterGame), AlterGameMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.KickPlayer, nameof(RootMessageId.KickPlayer), KickPlayerMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.WaitForHost, nameof(RootMessageId.WaitForHost), WaitForHostMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.Redirect, nameof(RootMessageId.Redirect), RedirectMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.ReselectServer, nameof(RootMessageId.ReselectServer), ReselectServerMessage.Deserialize);
        reg.RegisterRoot(RootMessageId.GetGameListV2, nameof(RootMessageId.GetGameListV2), GetGameListV2Message.Deserialize);
        reg.RegisterRoot(RootMessageId.ReportPlayer, nameof(RootMessageId.ReportPlayer), ReportPlayerMessage.Deserialize);

        // Section 5.5 RPCs (31/31) + Impostor modern RPCs
        reg.RegisterRpc(RpcId.PlayAnimation, nameof(RpcId.PlayAnimation), PlayAnimationRpc.Deserialize);
        reg.RegisterRpc(RpcId.CompleteTask, nameof(RpcId.CompleteTask), CompleteTaskRpc.Deserialize);
        reg.RegisterRpc(RpcId.SyncSettings, nameof(RpcId.SyncSettings), SyncSettingsRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetInfected, nameof(RpcId.SetInfected), SetInfectedRpc.Deserialize);
        reg.RegisterRpc(RpcId.Exiled, nameof(RpcId.Exiled), ExiledRpc.Deserialize);
        reg.RegisterRpc(RpcId.CheckName, nameof(RpcId.CheckName), CheckNameRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetName, nameof(RpcId.SetName), SetNameRpc.Deserialize);
        reg.RegisterRpc(RpcId.CheckColor, nameof(RpcId.CheckColor), CheckColorRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetColor, nameof(RpcId.SetColor), SetColorRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetHat, nameof(RpcId.SetHat), SetHatRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetSkin, nameof(RpcId.SetSkin), SetSkinRpc.Deserialize);
        reg.RegisterRpc(RpcId.ReportDeadBody, nameof(RpcId.ReportDeadBody), ReportDeadBodyRpc.Deserialize);
        reg.RegisterRpc(RpcId.MurderPlayer, nameof(RpcId.MurderPlayer), MurderPlayerRpc.Deserialize);
        reg.RegisterRpc(RpcId.SendChat, nameof(RpcId.SendChat), SendChatRpc.Deserialize);
        reg.RegisterRpc(RpcId.StartMeeting, nameof(RpcId.StartMeeting), StartMeetingRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetScanner, nameof(RpcId.SetScanner), SetScannerRpc.Deserialize);
        reg.RegisterRpc(RpcId.SendChatNote, nameof(RpcId.SendChatNote), SendChatNoteRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetPet, nameof(RpcId.SetPet), SetPetRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetStartCounter, nameof(RpcId.SetStartCounter), SetStartCounterRpc.Deserialize);
        reg.RegisterRpc(RpcId.EnterVent, nameof(RpcId.EnterVent), EnterVentRpc.Deserialize);
        reg.RegisterRpc(RpcId.ExitVent, nameof(RpcId.ExitVent), ExitVentRpc.Deserialize);
        reg.RegisterRpc(RpcId.SnapTo, nameof(RpcId.SnapTo), SnapToRpc.Deserialize);
        reg.RegisterRpc(RpcId.Close, nameof(RpcId.Close), CloseMeetingRpc.Deserialize);
        reg.RegisterRpc(RpcId.VotingComplete, nameof(RpcId.VotingComplete), VotingCompleteRpc.Deserialize);
        reg.RegisterRpc(RpcId.CastVote, nameof(RpcId.CastVote), CastVoteRpc.Deserialize);
        reg.RegisterRpc(RpcId.ClearVote, nameof(RpcId.ClearVote), ClearVoteRpc.Deserialize);
        reg.RegisterRpc(RpcId.AddVote, nameof(RpcId.AddVote), AddVoteRpc.Deserialize);
        reg.RegisterRpc(RpcId.CloseDoorsOfType, nameof(RpcId.CloseDoorsOfType), CloseDoorsOfTypeRpc.Deserialize);
        reg.RegisterRpc(RpcId.RepairSystem, nameof(RpcId.RepairSystem), RepairSystemRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetTasks, nameof(RpcId.SetTasks), SetTasksRpc.Deserialize);
        reg.RegisterRpc(RpcId.ClimbLadder, nameof(RpcId.ClimbLadder), ClimbLadderRpc.Deserialize);
        reg.RegisterRpc(RpcId.UsePlatform, nameof(RpcId.UsePlatform), UsePlatformRpc.Deserialize);

        // Modern additions used by current Among Us builds
        reg.RegisterRpc(RpcId.SendQuickChat, nameof(RpcId.SendQuickChat), SendQuickChatRpc.Deserialize);
        reg.RegisterRpc(RpcId.BootFromVent, nameof(RpcId.BootFromVent), BootFromVentRpc.Deserialize);
        reg.RegisterRpc(RpcId.UpdateSystem, nameof(RpcId.UpdateSystem), UpdateSystemRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetLevel, nameof(RpcId.SetLevel), SetLevelRpc.Deserialize);
        reg.RegisterRpc(RpcId.SetHatStr, nameof(RpcId.SetHatStr), r => SetCosmeticStrRpc.Deserialize(RpcId.SetHatStr, r));
        reg.RegisterRpc(RpcId.SetSkinStr, nameof(RpcId.SetSkinStr), r => SetCosmeticStrRpc.Deserialize(RpcId.SetSkinStr, r));
        reg.RegisterRpc(RpcId.SetPetStr, nameof(RpcId.SetPetStr), r => SetCosmeticStrRpc.Deserialize(RpcId.SetPetStr, r));
        reg.RegisterRpc(RpcId.SetVisorStr, nameof(RpcId.SetVisorStr), r => SetCosmeticStrRpc.Deserialize(RpcId.SetVisorStr, r));
        reg.RegisterRpc(RpcId.SetNamePlateStr, nameof(RpcId.SetNamePlateStr), r => SetCosmeticStrRpc.Deserialize(RpcId.SetNamePlateStr, r));
        reg.RegisterRpc(RpcId.SetRole, nameof(RpcId.SetRole), SetRoleRpc.Deserialize);
        reg.RegisterRpc(RpcId.CheckMurder, nameof(RpcId.CheckMurder), CheckMurderRpc.Deserialize);

        return reg;
    }

    public void RegisterRoot(byte id, string name, Func<IMessageReader, IRootMessage> deserialize)
    {
        if (!_rootMessages.TryAdd(id, new RootMessageDescriptor(id, name, deserialize)))
        {
            throw new InvalidOperationException($"Duplicate RootMessage id 0x{id:x2} ({name}).");
        }
    }

    public void RegisterRpc(byte id, string name, Func<IMessageReader, IRpcMessage> deserialize)
    {
        if (!_rpcs.TryAdd(id, new RpcDescriptor(id, name, deserialize)))
        {
            throw new InvalidOperationException($"Duplicate RPC id 0x{id:x2} ({name}).");
        }
    }

    public string GetRootName(byte id) =>
        _rootMessages.TryGetValue(id, out var desc) ? desc.Name : $"UnknownRoot(0x{id:x2})";

    public string GetRpcName(byte id) =>
        _rpcs.TryGetValue(id, out var desc) ? desc.Name : $"UnknownRpc(0x{id:x2})";

    public byte[] EncodeRoot(IRootMessage message, MessageType sendOption = MessageType.Reliable)
    {
        using var writer = MessageWriter.Get(sendOption);
        writer.StartMessage(message.MessageId);
        message.SerializeBody(writer);
        writer.EndMessage();
        return writer.ToByteArray(includeHeader: false);
    }

    public IRootMessage DecodeRoot(byte[] payload)
    {
        using var outer = MessageReader.Get(payload);
        using var msg = outer.ReadMessage();
        return DecodeRootMessage(msg);
    }

    public IRootMessage DecodeRootMessage(IMessageReader messageReader)
    {
        if (!_rootMessages.TryGetValue(messageReader.Tag, out var desc))
        {
            throw new ProtocolException(messageReader.Tag, messageReader.Buffer.AsSpan(messageReader.Offset, messageReader.Length),
                $"Unknown root message id 0x{messageReader.Tag:x2}.");
        }

        try
        {
            return desc.Deserialize(messageReader);
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var slice = messageReader.Buffer.AsSpan(messageReader.Offset, messageReader.Length);
            throw new ProtocolException(messageReader.Tag, slice,
                $"Failed to decode root message {desc.Name}: {ex.Message}", ex);
        }
    }

    public byte[] EncodeRpc(IRpcMessage rpc)
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        writer.Write(rpc.RpcCallId);
        rpc.SerializeBody(writer);
        return writer.ToByteArray(includeHeader: false);
    }

    public IRpcMessage DecodeRpc(byte[] payload)
    {
        using var reader = MessageReader.Get(payload);
        if (reader.BytesRemaining < 1)
        {
            throw new ProtocolException(null, payload, "Empty RPC payload.");
        }

        var rpcId = reader.ReadByte();
        if (!_rpcs.TryGetValue(rpcId, out var desc))
        {
            throw new ProtocolException(rpcId, payload, $"Unknown RPC id 0x{rpcId:x2}.");
        }

        try
        {
            return desc.Deserialize(reader);
        }
        catch (ProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProtocolException(rpcId, payload, $"Failed to decode RPC {desc.Name}: {ex.Message}", ex);
        }
    }

    public void AssertSpecCoverage()
    {
        var missingRoot = RootMessageId.Spec54Ids.Where(id => !_rootMessages.ContainsKey(id)).ToArray();
        if (missingRoot.Length > 0)
        {
            throw new InvalidOperationException($"Missing 5.4 root message ids: {string.Join(", ", missingRoot.Select(b => $"0x{b:x2}"))}");
        }

        var missingRpc = RpcId.Spec55Ids.Where(id => !_rpcs.ContainsKey(id)).ToArray();
        if (missingRpc.Length > 0)
        {
            throw new InvalidOperationException($"Missing 5.5 RPC ids: {string.Join(", ", missingRpc.Select(b => $"0x{b:x2}"))}");
        }
    }
}
