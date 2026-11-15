using System.Net;
using BeanBot.Protocol.Hazel;

namespace BeanBot.Protocol.RootMessages;

public interface IRootMessage
{
    byte MessageId { get; }
    void SerializeBody(IMessageWriter writer);
}

public sealed record HostGameMessage(
    GameOptionsOrCode Payload) : IRootMessage
{
    public byte MessageId => RootMessageId.HostGame;

    public void SerializeBody(IMessageWriter writer)
    {
        if (Payload.IsServerResponse)
        {
            Payload.GameCode!.Value.Serialize(writer);
            return;
        }

        GameOptionsFactory.Serialize(writer, Payload.Options!);
        writer.Write((int)Payload.Crossplay);
        Payload.Filters!.Serialize(writer);
    }

    public static HostGameMessage Deserialize(IMessageReader reader)
    {
        if (reader.BytesRemaining == sizeof(int))
        {
            return new HostGameMessage(GameOptionsOrCode.FromResponse(new GameCode(reader.ReadInt32())));
        }

        var options = GameOptionsFactory.Deserialize(reader);
        var crossplay = (CrossplayFlags)reader.ReadInt32();
        var filters = GameFilterOptions.Deserialize(reader);
        return new HostGameMessage(GameOptionsOrCode.FromRequest(options, crossplay, filters));
    }
}

public sealed record GameOptionsOrCode(
    bool IsServerResponse,
    GameCode? GameCode,
    IGameOptions? Options,
    CrossplayFlags Crossplay,
    GameFilterOptions? Filters)
{
    public static GameOptionsOrCode FromResponse(GameCode code) =>
        new(true, code, null, CrossplayFlags.None, null);

    public static GameOptionsOrCode FromRequest(IGameOptions options, CrossplayFlags crossplay, GameFilterOptions filters) =>
        new(false, null, options, crossplay, filters);
}

public sealed record JoinGameMessage(
    GameCode GameCode,
    bool NoCrossplay = false,
    JoinedPlayerEntry? ServerJoinedPlayer = null,
    int ServerHostClientId = 0,
    DisconnectReason? ServerDisconnectReason = null,
    string? ServerCustomMessage = null) : IRootMessage
{
    public byte MessageId => RootMessageId.JoinGame;

    public void SerializeBody(IMessageWriter writer)
    {
        if (ServerDisconnectReason is { } disconnectReason)
        {
            writer.Write((int)disconnectReason);
            if (disconnectReason == DisconnectReason.Custom && ServerCustomMessage is not null)
            {
                writer.Write(ServerCustomMessage);
            }

            return;
        }

        GameCode.Serialize(writer);
        if (ServerJoinedPlayer is { } joined)
        {
            writer.Write(joined.ClientId);
            writer.Write(ServerHostClientId);
            writer.Write(joined.Name);
            joined.Platform.Serialize(writer);
            writer.WritePacked(joined.PlayerLevel);
            writer.Write(joined.Puid);
            writer.Write(joined.FriendCode);
            return;
        }

        writer.Write(NoCrossplay);
    }

    public static JoinGameMessage Deserialize(IMessageReader reader)
    {
        var rawCode = reader.ReadInt32();
        if (rawCode is >= 0 and <= byte.MaxValue)
        {
            var reason = Enum.IsDefined(typeof(DisconnectReason), (byte)rawCode)
                ? (DisconnectReason)(byte)rawCode
                : DisconnectReason.Unknown;
            var customMessage = reason == DisconnectReason.Custom && reader.BytesRemaining > 0
                ? reader.ReadString()
                : null;
            return new JoinGameMessage(
                new GameCode(rawCode),
                NoCrossplay: false,
                ServerJoinedPlayer: null,
                ServerHostClientId: 0,
                ServerDisconnectReason: reason,
                ServerCustomMessage: customMessage);
        }

        var code = new GameCode(rawCode);
        if (reader.BytesRemaining <= 5)
        {
            var noCrossplay = reader.BytesRemaining > 0 && reader.ReadBoolean();
            return new JoinGameMessage(code, noCrossplay);
        }

        var clientId = reader.ReadInt32();
        var hostId = reader.ReadInt32();
        if (reader.BytesRemaining == 0)
        {
            return new JoinGameMessage(
                code,
                NoCrossplay: false,
                ServerJoinedPlayer: null,
                ServerHostClientId: hostId);
        }

        var name = reader.ReadString();
        using var platformReader = reader.ReadMessage();
        var platform = PlatformSpecificData.Deserialize(platformReader);
        var level = reader.ReadPackedUInt32();
        var puid = reader.ReadString();
        var friendCode = reader.ReadString();
        return new JoinGameMessage(
            code,
            NoCrossplay: false,
            ServerJoinedPlayer: new JoinedPlayerEntry(clientId, name, platform, level, puid, friendCode),
            ServerHostClientId: hostId);
    }
}

public sealed record StartGameMessage(GameCode GameCode) : IRootMessage
{
    public byte MessageId => RootMessageId.StartGame;

    public void SerializeBody(IMessageWriter writer) => GameCode.Serialize(writer);

    public static StartGameMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()));
}

public sealed record RemoveGameMessage(GameCode GameCode) : IRootMessage
{
    public byte MessageId => RootMessageId.RemoveGame;

    public void SerializeBody(IMessageWriter writer) => GameCode.Serialize(writer);

    public static RemoveGameMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()));
}

public sealed record RemovePlayerMessage(
    GameCode GameCode,
    int PlayerId,
    int HostId,
    DisconnectReason Reason) : IRootMessage
{
    public byte MessageId => RootMessageId.RemovePlayer;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write(PlayerId);
        writer.Write(HostId);
        writer.Write((byte)Reason);
    }

    public static RemovePlayerMessage Deserialize(IMessageReader reader) =>
        new(
            new GameCode(reader.ReadInt32()),
            reader.ReadInt32(),
            reader.ReadInt32(),
            (DisconnectReason)reader.ReadByte());
}

public sealed record GameDataMessage(GameCode GameCode, byte[] RawSubMessages) : IRootMessage, IEquatable<GameDataMessage>
{
    public byte MessageId => RootMessageId.GameData;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write(RawSubMessages);
    }

    public static GameDataMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), reader.ReadBytes(reader.BytesRemaining).ToArray());

    public bool Equals(GameDataMessage? other) =>
        other is not null && GameCode == other.GameCode && RawSubMessages.SequenceEqual(other.RawSubMessages);

    public override int GetHashCode() => HashCode.Combine(GameCode, RawSubMessages.Length);
}

public sealed record GameDataToMessage(GameCode GameCode, int TargetClientId, byte[] RawSubMessages) : IRootMessage, IEquatable<GameDataToMessage>
{
    public byte MessageId => RootMessageId.GameDataTo;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.WritePacked(TargetClientId);
        writer.Write(RawSubMessages);
    }

    public static GameDataToMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), reader.ReadPackedInt32(), reader.ReadBytes(reader.BytesRemaining).ToArray());

    public bool Equals(GameDataToMessage? other) =>
        other is not null && GameCode == other.GameCode && TargetClientId == other.TargetClientId &&
        RawSubMessages.SequenceEqual(other.RawSubMessages);

    public override int GetHashCode() => HashCode.Combine(GameCode, TargetClientId, RawSubMessages.Length);
}

public sealed record JoinedPlayerEntry(
    int ClientId,
    string Name,
    PlatformSpecificData Platform,
    uint PlayerLevel,
    string Puid,
    string FriendCode);

public sealed record JoinedGameMessage(
    GameCode GameCode,
    int PlayerClientId,
    int HostClientId,
    IReadOnlyList<JoinedPlayerEntry> OtherPlayers) : IRootMessage, IEquatable<JoinedGameMessage>
{
    public byte MessageId => RootMessageId.JoinedGame;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write(PlayerClientId);
        writer.Write(HostClientId);
        writer.WritePacked(OtherPlayers.Count);
        foreach (var ply in OtherPlayers)
        {
            writer.WritePacked(ply.ClientId);
            writer.Write(ply.Name);
            ply.Platform.Serialize(writer);
            writer.WritePacked(ply.PlayerLevel);
            writer.Write(ply.Puid);
            writer.Write(ply.FriendCode);
        }
    }

    public static JoinedGameMessage Deserialize(IMessageReader reader)
    {
        var code = new GameCode(reader.ReadInt32());
        var clientId = reader.ReadInt32();
        var hostId = reader.ReadInt32();
        var count = reader.ReadPackedInt32();
        if (count < 0 || count > 64)
        {
            throw new ProtocolException(RootMessageId.JoinedGame, reader.Buffer, $"Invalid JoinedGame player count {count}.");
        }

        var others = new List<JoinedPlayerEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var id = reader.ReadPackedInt32();
            var name = reader.ReadString();
            using var platformReader = reader.ReadMessage();
            var platform = PlatformSpecificData.Deserialize(platformReader);
            var level = reader.ReadPackedUInt32();
            var puid = reader.ReadString();
            var friendCode = reader.ReadString();
            others.Add(new JoinedPlayerEntry(id, name, platform, level, puid, friendCode));
        }

        return new JoinedGameMessage(code, clientId, hostId, others);
    }

    public bool Equals(JoinedGameMessage? other) =>
        other is not null &&
        GameCode == other.GameCode &&
        PlayerClientId == other.PlayerClientId &&
        HostClientId == other.HostClientId &&
        OtherPlayers.SequenceEqual(other.OtherPlayers);

    public override int GetHashCode() => HashCode.Combine(GameCode, PlayerClientId, HostClientId, OtherPlayers.Count);
}

public sealed record EndGameMessage(GameCode GameCode, GameOverReason Reason, bool ShowAd = false) : IRootMessage
{
    public byte MessageId => RootMessageId.EndGame;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write((byte)Reason);
        writer.Write(ShowAd);
    }

    public static EndGameMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), (GameOverReason)reader.ReadByte(), reader.ReadBoolean());
}

public sealed record GetGameListMessage(bool IncludeShowPrivate, IGameOptions Options) : IRootMessage
{
    public byte MessageId => RootMessageId.GetGameList;

    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(IncludeShowPrivate);
        GameOptionsFactory.Serialize(writer, Options);
    }

    public static GetGameListMessage Deserialize(IMessageReader reader) =>
        new(reader.ReadBoolean(), GameOptionsFactory.Deserialize(reader));
}

public sealed record AlterGameMessage(GameCode GameCode, AlterGameTags GameTag, bool IsPublic) : IRootMessage
{
    public byte MessageId => RootMessageId.AlterGame;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write((byte)GameTag);
        writer.Write(IsPublic);
    }

    public static AlterGameMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), (AlterGameTags)reader.ReadByte(), reader.ReadBoolean());
}

public sealed record KickPlayerMessage(GameCode GameCode, int PlayerClientId, bool IsBan) : IRootMessage
{
    public byte MessageId => RootMessageId.KickPlayer;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.WritePacked(PlayerClientId);
        writer.Write(IsBan);
    }

    public static KickPlayerMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), reader.ReadPackedInt32(), reader.ReadBoolean());
}

public sealed record WaitForHostMessage(GameCode GameCode, int PlayerClientId) : IRootMessage
{
    public byte MessageId => RootMessageId.WaitForHost;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.Write(PlayerClientId);
    }

    public static WaitForHostMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), reader.ReadInt32());
}

public sealed record RedirectMessage(IPAddress Address, ushort Port) : IRootMessage
{
    public byte MessageId => RootMessageId.Redirect;

    public void SerializeBody(IMessageWriter writer)
    {
        writer.Write(Address);
        writer.Write(Port);
    }

    public static RedirectMessage Deserialize(IMessageReader reader)
    {
        var ipBytes = reader.ReadBytes(4);
        var port = reader.ReadUInt16();
        return new RedirectMessage(new IPAddress(ipBytes.Span), port);
    }
}

public sealed record ReselectServerMessage(byte[] Payload) : IRootMessage, IEquatable<ReselectServerMessage>
{
    public byte MessageId => RootMessageId.ReselectServer;

    public void SerializeBody(IMessageWriter writer) => writer.Write(Payload);

    public static ReselectServerMessage Deserialize(IMessageReader reader) =>
        new(reader.ReadBytes(reader.BytesRemaining).ToArray());

    public bool Equals(ReselectServerMessage? other) =>
        other is not null && Payload.SequenceEqual(other.Payload);

    public override int GetHashCode() => Payload.Length;
}

public sealed record GetGameListV2Message(
    int Version,
    IGameOptions Options,
    QuickChatModes ChatMode,
    CrossplayFlags Crossplay,
    GameFilterOptions FilterOptions) : IRootMessage
{
    public byte MessageId => RootMessageId.GetGameListV2;

    public void SerializeBody(IMessageWriter writer)
    {
        writer.WritePacked(Version);
        GameOptionsFactory.Serialize(writer, Options);
        writer.Write((byte)ChatMode);
        writer.Write((int)Crossplay);
        FilterOptions.Serialize(writer);
    }

    public static GetGameListV2Message Deserialize(IMessageReader reader)
    {
        var version = reader.ReadPackedInt32();
        var options = GameOptionsFactory.Deserialize(reader);
        var chatMode = (QuickChatModes)reader.ReadByte();
        var crossplay = (CrossplayFlags)reader.ReadInt32();
        var filters = GameFilterOptions.Deserialize(reader);
        return new GetGameListV2Message(version, options, chatMode, crossplay, filters);
    }
}

public sealed record ReportPlayerMessage(
    GameCode GameCode,
    int TargetClientId,
    byte Reason) : IRootMessage
{
    public byte MessageId => RootMessageId.ReportPlayer;

    public void SerializeBody(IMessageWriter writer)
    {
        GameCode.Serialize(writer);
        writer.WritePacked(TargetClientId);
        writer.Write(Reason);
    }

    public static ReportPlayerMessage Deserialize(IMessageReader reader) =>
        new(new GameCode(reader.ReadInt32()), reader.ReadPackedInt32(), reader.ReadByte());
}

public sealed record DisconnectMessage(bool HasReason, DisconnectReason? Reason, string? CustomMessage)
{
    public void Serialize(IMessageWriter writer)
    {
        writer.Write(HasReason);
        if (!HasReason)
        {
            return;
        }

        if (Reason is null)
        {
            throw new ArgumentNullException(nameof(Reason));
        }

        writer.StartMessage(0);
        writer.Write((byte)Reason.Value);
        if (Reason.Value == DisconnectReason.Custom)
        {
            writer.Write(CustomMessage ?? string.Empty);
        }

        writer.EndMessage();
    }

    public static DisconnectMessage Deserialize(IMessageReader reader)
    {
        var hasReason = reader.ReadBoolean();
        if (!hasReason)
        {
            return new DisconnectMessage(false, null, null);
        }

        using var inner = reader.ReadMessage();
        var reason = (DisconnectReason)inner.ReadByte();
        var msg = reason == DisconnectReason.Custom ? inner.ReadString() : null;
        return new DisconnectMessage(true, reason, msg);
    }
}
