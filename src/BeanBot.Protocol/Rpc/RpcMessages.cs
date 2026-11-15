using System.Numerics;
using BeanBot.Protocol.Hazel;

namespace BeanBot.Protocol.Rpc;

public interface IRpcMessage
{
    byte RpcCallId { get; }
    void SerializeBody(IMessageWriter writer);
}

public sealed record PlayAnimationRpc(TaskTypes Task) : IRpcMessage
{
    public byte RpcCallId => RpcId.PlayAnimation;
    public void SerializeBody(IMessageWriter writer) => writer.Write((byte)Task);
    public static PlayAnimationRpc Deserialize(IMessageReader reader) => new((TaskTypes)reader.ReadByte());
}

public sealed record CompleteTaskRpc(uint TaskId) : IRpcMessage
{
    public byte RpcCallId => RpcId.CompleteTask;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(TaskId);
    public static CompleteTaskRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record SyncSettingsRpc(IGameOptions Options) : IRpcMessage
{
    public byte RpcCallId => RpcId.SyncSettings;
    public void SerializeBody(IMessageWriter writer) => GameOptionsFactory.Serialize(writer, Options);
    public static SyncSettingsRpc Deserialize(IMessageReader reader) => new(GameOptionsFactory.Deserialize(reader));
}

public sealed record SetInfectedRpc(byte[] InfectedIds) : IRpcMessage, IEquatable<SetInfectedRpc>
{
    public byte RpcCallId => RpcId.SetInfected;
    public void SerializeBody(IMessageWriter writer) => writer.WriteBytesAndSize(InfectedIds);
    public static SetInfectedRpc Deserialize(IMessageReader reader) => new(reader.ReadBytesAndSize().ToArray());
    public bool Equals(SetInfectedRpc? other) => other is not null && InfectedIds.SequenceEqual(other.InfectedIds);
    public override int GetHashCode() => InfectedIds.Length;
}

public sealed record ExiledRpc() : IRpcMessage
{
    public byte RpcCallId => RpcId.Exiled;
    public void SerializeBody(IMessageWriter writer) { }
    public static ExiledRpc Deserialize(IMessageReader reader) => new();
}

public sealed record CheckNameRpc(string Name) : IRpcMessage
{
    public byte RpcCallId => RpcId.CheckName;
    public void SerializeBody(IMessageWriter writer) => writer.Write(Name);
    public static CheckNameRpc Deserialize(IMessageReader reader) => new(reader.ReadString());
}

public sealed record SetNameRpc(uint NetId, string Name) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetName;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(NetId);
        writer.Write(Name);
    }

    public static SetNameRpc Deserialize(IMessageReader reader) => new(reader.ReadUInt32(), reader.ReadString());
}

public sealed record CheckColorRpc(ColorType Color) : IRpcMessage
{
    public byte RpcCallId => RpcId.CheckColor;
    public void SerializeBody(IMessageWriter writer) => writer.Write((byte)Color);
    public static CheckColorRpc Deserialize(IMessageReader reader) => new((ColorType)reader.ReadByte());
}

public sealed record SetColorRpc(uint NetId, ColorType Color) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetColor;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(NetId);
        writer.Write((byte)Color);
    }

    public static SetColorRpc Deserialize(IMessageReader reader) => new(reader.ReadUInt32(), (ColorType)reader.ReadByte());
}

public sealed record SetHatRpc(uint HatId) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetHat;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(HatId);
    public static SetHatRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record SetSkinRpc(uint SkinId) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetSkin;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(SkinId);
    public static SetSkinRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record ReportDeadBodyRpc(byte TargetPlayerId) : IRpcMessage
{
    public byte RpcCallId => RpcId.ReportDeadBody;
    public void SerializeBody(IMessageWriter writer) => writer.Write(TargetPlayerId);
    public static ReportDeadBodyRpc Deserialize(IMessageReader reader) => new(reader.ReadByte());
}

public sealed record MurderPlayerRpc(uint TargetNetId, MurderResultFlags Result) : IRpcMessage
{
    public byte RpcCallId => RpcId.MurderPlayer;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.WritePacked(TargetNetId);
        writer.Write((int)Result);
    }

    public static MurderPlayerRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadPackedUInt32(), (MurderResultFlags)reader.ReadInt32());
}

public sealed record SendChatRpc(string Message) : IRpcMessage
{
    public byte RpcCallId => RpcId.SendChat;
    public void SerializeBody(IMessageWriter writer) => writer.Write(Message);
    public static SendChatRpc Deserialize(IMessageReader reader) => new(reader.ReadString());
}

public sealed record StartMeetingRpc(byte TargetPlayerId) : IRpcMessage
{
    public byte RpcCallId => RpcId.StartMeeting;
    public void SerializeBody(IMessageWriter writer) => writer.Write(TargetPlayerId);
    public static StartMeetingRpc Deserialize(IMessageReader reader) => new(reader.ReadByte());
}

public sealed record SetScannerRpc(bool On, byte ScannerCount) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetScanner;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(On);
        writer.Write(ScannerCount);
    }

    public static SetScannerRpc Deserialize(IMessageReader reader) => new(reader.ReadBoolean(), reader.ReadByte());
}

public sealed record SendChatNoteRpc(byte PlayerId, ChatNoteType NoteType) : IRpcMessage
{
    public byte RpcCallId => RpcId.SendChatNote;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(PlayerId);
        writer.Write((byte)NoteType);
    }

    public static SendChatNoteRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadByte(), (ChatNoteType)reader.ReadByte());
}

public sealed record SetPetRpc(uint PetId) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetPet;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(PetId);
    public static SetPetRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record SetStartCounterRpc(int SequenceId, sbyte StartCounter) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetStartCounter;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.WritePacked(SequenceId);
        writer.Write(StartCounter);
    }

    public static SetStartCounterRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadPackedInt32(), reader.ReadSByte());
}

public sealed record EnterVentRpc(int VentId) : IRpcMessage
{
    public byte RpcCallId => RpcId.EnterVent;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(VentId);
    public static EnterVentRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedInt32());
}

public sealed record ExitVentRpc(int VentId) : IRpcMessage
{
    public byte RpcCallId => RpcId.ExitVent;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(VentId);
    public static ExitVentRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedInt32());
}

public sealed record SnapToRpc(Vector2 Position, ushort MinSequenceId) : IRpcMessage
{
    public byte RpcCallId => RpcId.SnapTo;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.WriteVector2(Position);
        writer.Write(MinSequenceId);
    }

    public static SnapToRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadVector2(), reader.ReadUInt16());
}

public sealed record CloseMeetingRpc() : IRpcMessage
{
    public byte RpcCallId => RpcId.Close;
    public void SerializeBody(IMessageWriter writer) { }
    public static CloseMeetingRpc Deserialize(IMessageReader reader) => new();
}

public sealed record VotingCompleteRpc(
    byte[] States,
    byte ExiledPlayerId,
    bool IsTie,
    bool WasOverruled = false,
    ushort OverrideId = 0) : IRpcMessage, IEquatable<VotingCompleteRpc>
{
    public byte RpcCallId => RpcId.VotingComplete;

    public void SerializeBody(IMessageWriter writer)
    {
        writer.WriteBytesAndSize(States);
        writer.Write(ExiledPlayerId);
        writer.Write(IsTie);
        writer.Write(WasOverruled);
        writer.Write(OverrideId);
    }

    public static VotingCompleteRpc Deserialize(IMessageReader reader)
    {
        var states = reader.ReadBytesAndSize().ToArray();
        var exiledId = reader.ReadByte();
        var tie = reader.ReadBoolean();
        var wasOverruled = reader.BytesRemaining >= 3 && reader.ReadBoolean();
        var overrideId = reader.BytesRemaining >= 2 ? reader.ReadUInt16() : (ushort)0;
        return new VotingCompleteRpc(states, exiledId, tie, wasOverruled, overrideId);
    }

    public bool Equals(VotingCompleteRpc? other) =>
        other is not null &&
        States.SequenceEqual(other.States) &&
        ExiledPlayerId == other.ExiledPlayerId &&
        IsTie == other.IsTie &&
        WasOverruled == other.WasOverruled &&
        OverrideId == other.OverrideId;

    public override int GetHashCode() => HashCode.Combine(States.Length, ExiledPlayerId, IsTie, WasOverruled, OverrideId);
}

public sealed record CastVoteRpc(byte VoterPlayerId, sbyte SuspectPlayerId) : IRpcMessage
{
    public byte RpcCallId => RpcId.CastVote;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(VoterPlayerId);
        writer.Write(SuspectPlayerId);
    }

    public static CastVoteRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadByte(), reader.ReadSByte());
}

public sealed record ClearVoteRpc() : IRpcMessage
{
    public byte RpcCallId => RpcId.ClearVote;
    public void SerializeBody(IMessageWriter writer) { }
    public static ClearVoteRpc Deserialize(IMessageReader reader) => new();
}

public sealed record AddVoteRpc(int ClientId, int TargetClientId) : IRpcMessage
{
    public byte RpcCallId => RpcId.AddVote;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(ClientId);
        writer.Write(TargetClientId);
    }

    public static AddVoteRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32());
}

public sealed record CloseDoorsOfTypeRpc(SystemTypes SystemType) : IRpcMessage
{
    public byte RpcCallId => RpcId.CloseDoorsOfType;
    public void SerializeBody(IMessageWriter writer) => writer.Write((byte)SystemType);
    public static CloseDoorsOfTypeRpc Deserialize(IMessageReader reader) => new((SystemTypes)reader.ReadByte());
}

public sealed record RepairSystemRpc(SystemTypes SystemType, uint PlayerNetId, byte Amount) : IRpcMessage
{
    public byte RpcCallId => RpcId.RepairSystem;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write((byte)SystemType);
        writer.WritePacked(PlayerNetId);
        writer.Write(Amount);
    }

    public static RepairSystemRpc Deserialize(IMessageReader reader) =>
        new((SystemTypes)reader.ReadByte(), reader.ReadPackedUInt32(), reader.ReadByte());
}

public sealed record UpdateSystemRpc(SystemTypes SystemType, uint PlayerNetId, ushort SequenceId, byte State, byte VentId) : IRpcMessage
{
    public byte RpcCallId => RpcId.UpdateSystem;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write((byte)SystemType);
        writer.WritePacked(PlayerNetId);
        writer.Write(SequenceId);
        writer.Write(State);
        writer.Write(VentId);
    }

    public static UpdateSystemRpc Deserialize(IMessageReader reader) =>
        new((SystemTypes)reader.ReadByte(), reader.ReadPackedUInt32(), reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte());
}

public sealed record SetTasksRpc(byte[] TaskTypeIds) : IRpcMessage, IEquatable<SetTasksRpc>
{
    public byte RpcCallId => RpcId.SetTasks;
    public void SerializeBody(IMessageWriter writer) => writer.WriteBytesAndSize(TaskTypeIds);
    public static SetTasksRpc Deserialize(IMessageReader reader) => new(reader.ReadBytesAndSize().ToArray());
    public bool Equals(SetTasksRpc? other) => other is not null && TaskTypeIds.SequenceEqual(other.TaskTypeIds);
    public override int GetHashCode() => TaskTypeIds.Length;
}

public sealed record ClimbLadderRpc(byte LadderId, byte SequenceId) : IRpcMessage
{
    public byte RpcCallId => RpcId.ClimbLadder;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(LadderId);
        writer.Write(SequenceId);
    }

    public static ClimbLadderRpc Deserialize(IMessageReader reader) =>
        new(reader.ReadByte(), reader.ReadByte());
}

public sealed record UsePlatformRpc() : IRpcMessage
{
    public byte RpcCallId => RpcId.UsePlatform;
    public void SerializeBody(IMessageWriter writer) { }
    public static UsePlatformRpc Deserialize(IMessageReader reader) => new();
}

public sealed record SetRoleRpc(RoleTypes Role, bool CanOverride = false) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetRole;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write((ushort)Role);
        writer.Write(CanOverride);
    }

    public static SetRoleRpc Deserialize(IMessageReader reader) =>
        new((RoleTypes)reader.ReadUInt16(), reader.BytesRemaining > 0 && reader.ReadBoolean());
}

public sealed record CheckMurderRpc(uint TargetNetId) : IRpcMessage
{
    public byte RpcCallId => RpcId.CheckMurder;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(TargetNetId);
    public static CheckMurderRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record BootFromVentRpc(int VentId) : IRpcMessage
{
    public byte RpcCallId => RpcId.BootFromVent;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(VentId);
    public static BootFromVentRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedInt32());
}

public sealed record SetLevelRpc(uint Level) : IRpcMessage
{
    public byte RpcCallId => RpcId.SetLevel;
    public void SerializeBody(IMessageWriter writer) => writer.WritePacked(Level);
    public static SetLevelRpc Deserialize(IMessageReader reader) => new(reader.ReadPackedUInt32());
}

public sealed record SetCosmeticStrRpc(byte RpcCallIdValue, string Value, byte NextRpcSequenceId) : IRpcMessage
{
    public byte RpcCallId => RpcCallIdValue;
    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(Value);
        writer.Write(NextRpcSequenceId);
    }

    public static SetCosmeticStrRpc Deserialize(byte rpcId, IMessageReader reader) =>
        new(rpcId, reader.ReadString(), reader.ReadByte());

    public static SetCosmeticStrRpc Hat(string value = "", byte nextSeq = 1) => new(RpcId.SetHatStr, value, nextSeq);
    public static SetCosmeticStrRpc Skin(string value = "skin_None", byte nextSeq = 1) => new(RpcId.SetSkinStr, value, nextSeq);
    public static SetCosmeticStrRpc Pet(string value = "", byte nextSeq = 1) => new(RpcId.SetPetStr, value, nextSeq);
    public static SetCosmeticStrRpc Visor(string value = "visor_EmptyVisor", byte nextSeq = 1) => new(RpcId.SetVisorStr, value, nextSeq);
    public static SetCosmeticStrRpc NamePlate(string value = "", byte nextSeq = 1) => new(RpcId.SetNamePlateStr, value, nextSeq);
}

/// <summary>
/// QuickChat RPC payload encoding QuickChatPhrase (tag byte + phrase payload).
/// </summary>
public sealed record SendQuickChatRpc(
    string PhraseKey,
    QuickChatPhraseType PhraseType = QuickChatPhraseType.Empty,
    int StringNameId = 0,
    byte PlayerIdValue = 0) : IRpcMessage
{
    public byte RpcCallId => RpcId.SendQuickChat;

    public static SendQuickChatRpc Simple(int stringNameId, string? phraseKey = null) =>
        new(phraseKey ?? $"qc:{stringNameId}", QuickChatPhraseType.SimplePhrase, stringNameId, 0);

    public static SendQuickChatRpc ForPlayer(byte playerId) =>
        new($"player:{playerId}", QuickChatPhraseType.PlayerId, 0, playerId);

    public void SerializeBody(IMessageWriter writer)
    {
        var resolvedType = ResolvePhraseType(out var resolvedId);
        writer.Write((byte)resolvedType);
        switch (resolvedType)
        {
            case QuickChatPhraseType.PlayerId:
                writer.Write(PlayerIdValue);
                break;
            case QuickChatPhraseType.SimplePhrase:
                writer.Write((ushort)resolvedId);
                break;
            case QuickChatPhraseType.ComplexPhrase:
                writer.Write((ushort)resolvedId);
                writer.Write((byte)0);
                break;
        }
    }

    public static SendQuickChatRpc Deserialize(IMessageReader reader)
    {
        if (reader.BytesRemaining <= 0)
        {
            return new SendQuickChatRpc(string.Empty);
        }

        var phraseType = (QuickChatPhraseType)reader.ReadByte();
        switch (phraseType)
        {
            case QuickChatPhraseType.PlayerId when reader.BytesRemaining > 0:
                return ForPlayer(reader.ReadByte());
            case QuickChatPhraseType.SimplePhrase when reader.BytesRemaining >= 2:
                return Simple(reader.ReadUInt16());
            case QuickChatPhraseType.ComplexPhrase when reader.BytesRemaining >= 2:
                var formatId = reader.ReadUInt16();
                return new SendQuickChatRpc($"qc:{formatId}", QuickChatPhraseType.ComplexPhrase, formatId);
            default:
                return new SendQuickChatRpc(string.Empty, QuickChatPhraseType.Empty);
        }
    }

    public static bool TryParseStringNameId(string? value, out int stringNameId)
    {
        stringNameId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("qc:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(trimmed[3..].Trim(), out stringNameId) &&
            stringNameId >= 0 &&
            stringNameId <= ushort.MaxValue;
    }

    /// <summary>
    /// Resolves either an explicit <c>qc:&lt;id&gt;</c> token or a common Quick Chat phrase / enum key
    /// to its 2026.7.21 <c>StringNames</c> ID.
    /// </summary>
    public static bool TryResolvePhrase(string? value, out int stringNameId, out string canonicalKey)
    {
        stringNameId = 0;
        canonicalKey = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (TryParseStringNameId(value, out stringNameId))
        {
            canonicalKey = $"qc:{stringNameId}";
            return true;
        }

        var normalized = NormalizePhraseLookup(value);
        if (s_simplePhrases.TryGetValue(normalized, out var match))
        {
            stringNameId = match.Id;
            canonicalKey = match.Key;
            return true;
        }

        return false;
    }

    private QuickChatPhraseType ResolvePhraseType(out int resolvedId)
    {
        if (PhraseType != QuickChatPhraseType.Empty)
        {
            resolvedId = StringNameId;
            return PhraseType;
        }

        if (TryResolvePhrase(PhraseKey, out resolvedId, out _))
        {
            return QuickChatPhraseType.SimplePhrase;
        }

        resolvedId = 0;
        return QuickChatPhraseType.Empty;
    }

    private static string NormalizePhraseLookup(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        var prevSpace = true;
        foreach (var ch in raw.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else if (ch != '\'' && ch != '\u2019')
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
        }

        var s = sb.ToString().Trim();
        if (s is not ("!!!" or "???" or "!" or "?"))
        {
            s = s.TrimEnd('.', '!', '?').Trim();
        }
        return s;
    }

    private static readonly Dictionary<string, (int Id, string Key)> s_simplePhrases = BuildSimplePhraseMap();

    private static Dictionary<string, (int Id, string Key)> BuildSimplePhraseMap()
    {
        var map = new Dictionary<string, (int Id, string Key)>(StringComparer.Ordinal);
        void Add(int id, string enumName, params string[] aliases)
        {
            var entry = (id, enumName);
            map[enumName.ToLowerInvariant()] = entry;
            foreach (var alias in aliases)
            {
                map[NormalizePhraseLookup(alias)] = entry;
            }
        }

        Add(1974, "QCFollowMe", "follow", "follow me");
        Add(1975, "QCItsNotMe", "not me", "its not me", "innocent");
        Add(1976, "QCSelfDoneWithTasks", "done tasks", "done with tasks", "im done with tasks");
        Add(1977, "QCGhostsDoYourTasks", "ghosts do tasks", "ghosts do your tasks");
        Add(1978, "QCSelfNotTheImpostor", "not impostor", "im not impostor", "im not the impostor");
        Add(1979, "QCHello", "hello", "hi", "hey", "yo");
        Add(1980, "QCThanks", "thanks", "thank you", "thx", "ty");
        Add(1981, "QCSorry", "sorry", "my bad", "mb");
        Add(1982, "QCGG", "gg");
        Add(1983, "QCBye", "bye", "cya", "goodbye");
        Add(1984, "QCWhereWasTheBody", "where was body", "where was the body");
        Add(1985, "QCSelfReady", "ready", "im ready");
        Add(2004, "QCSystemsStart", "start", "go");
        Add(2005, "QCSystemsKick", "kick");
        Add(2006, "QCCrewI", "i");
        Add(2007, "QCCrewMe", "me");
        Add(2008, "QCCrewNoOne", "no one", "nobody", "skip");
        Add(2022, "QCResYes", "yes", "yeah", "yep", "yup");
        Add(2023, "QCResNo", "no", "nope", "nah");
        Add(2024, "QCResDontKnow", "idk", "dont know", "i dont know");
        Add(2038, "QCQstWhere", "where");
        Add(2039, "QCQstWho", "who");
        Add(2051, "QCStaPleaseDoTasks", "do tasks", "please do tasks");
        Add(3050, "QCQstWhy", "why");
        Add(3070, "QCResAgree", "agree", "i agree");
        Add(3071, "QCResDisagree", "disagree", "i disagree");
        Add(3072, "QCResNice", "nice", "ok", "okay", "cool");
        Add(3073, "QCResUhOh", "uh oh", "uhoh");
        Add(3074, "QCResOops", "oops");
        Add(3075, "QCResExclamationMarks", "!", "!!!");
        Add(3076, "QCResQuestionMarks", "?", "???");
        Add(3126, "QCCrewSomeone", "someone");
        Add(3127, "QCResUrWelcome", "welcome", "youre welcome", "yw");
        Add(3128, "QCResNp", "np", "no problem");
        Add(3129, "QCResYikes", "yikes");
        Add(3545, "QCPleaseWait", "wait", "please wait", "hold on");
        Add(3546, "QCWaitMinute", "wait a minute", "wait minute");
        Add(3547, "QCLobbyClosing", "closing", "lobby closing");
        Add(3548, "QCChangeCosmetics", "cosmetics", "change cosmetics");
        Add(3549, "QCWaitingPlayers", "waiting", "waiting for players");
        Add(3550, "QCLetsWaitPlayers", "lets wait", "lets wait for players");

        return map;
    }
}
