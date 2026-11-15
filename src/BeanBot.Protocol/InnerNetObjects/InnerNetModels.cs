using System.Numerics;
using BeanBot.Protocol.Hazel;
using BeanBot.Protocol.Rpc;

namespace BeanBot.Protocol.InnerNetObjects;

public sealed record CustomNetworkTransformUpdate(ushort SequenceId, IReadOnlyList<Vector2> Positions)
    : IEquatable<CustomNetworkTransformUpdate>
{
    public void Serialize(IMessageWriter writer, bool initialState = false)
    {
        writer.Write(SequenceId);
        if (initialState)
        {
            writer.WriteVector2(Positions[0]);
            return;
        }

        writer.WritePacked(Positions.Count);
        foreach (var pos in Positions)
        {
            writer.WriteVector2(pos);
        }
    }

    public static CustomNetworkTransformUpdate Deserialize(IMessageReader reader, bool initialState = false)
    {
        var sid = reader.ReadUInt16();
        if (initialState)
        {
            return new CustomNetworkTransformUpdate(sid, [reader.ReadVector2()]);
        }

        var count = reader.ReadPackedInt32();
        if (count < 0 || count > 64)
        {
            throw new ProtocolException(GameDataTag.Data, reader.Buffer, $"Invalid CustomNetworkTransform position count {count}.");
        }

        var list = new List<Vector2>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(reader.ReadVector2());
        }

        return new CustomNetworkTransformUpdate(sid, list);
    }

    public bool Equals(CustomNetworkTransformUpdate? other) =>
        other is not null && SequenceId == other.SequenceId && Positions.SequenceEqual(other.Positions);

    public override int GetHashCode() => HashCode.Combine(SequenceId, Positions.Count);
}

public sealed record SpawnComponentEntry(uint NetId, byte[] InitialData);

public sealed record SpawnObjectPacket(
    uint SpawnId,
    int OwnerClientId,
    byte Flags,
    IReadOnlyList<SpawnComponentEntry> Components);

public enum GameDataKind
{
    Unknown = 0,
    Data = 1,
    Rpc = 2,
    Spawn = 4,
    Despawn = 5,
    SceneChange = 6,
    Ready = 7,
    ChangeSettings = 8,
}

public sealed record GameDataSubMessage(byte Tag, byte[] Payload) : IEquatable<GameDataSubMessage>
{
    public GameDataKind Kind => Tag switch
    {
        GameDataTag.Data => GameDataKind.Data,
        GameDataTag.Rpc => GameDataKind.Rpc,
        GameDataTag.Spawn => GameDataKind.Spawn,
        GameDataTag.Despawn => GameDataKind.Despawn,
        GameDataTag.SceneChange => GameDataKind.SceneChange,
        GameDataTag.Ready => GameDataKind.Ready,
        GameDataTag.ChangeSettings => GameDataKind.ChangeSettings,
        _ => GameDataKind.Unknown,
    };
    public static IReadOnlyList<GameDataSubMessage> ParseAll(byte[] rawSubMessages)
    {
        using var outer = MessageReader.Get(rawSubMessages);
        var list = new List<GameDataSubMessage>();
        while (outer.BytesRemaining > 0)
        {
            using var sub = outer.ReadMessage();
            list.Add(new GameDataSubMessage(sub.Tag, sub.ReadBytes(sub.BytesRemaining).ToArray()));
        }

        return list;
    }

    public static byte[] SerializeAll(IEnumerable<GameDataSubMessage> subMessages)
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        foreach (var sub in subMessages)
        {
            writer.StartMessage(sub.Tag);
            writer.Write(sub.Payload);
            writer.EndMessage();
        }

        return writer.ToByteArray(includeHeader: false);
    }

    public static GameDataSubMessage FromRpc(uint targetNetId, IRpcMessage rpc)
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        writer.WritePacked(targetNetId);
        writer.Write(rpc.RpcCallId);
        rpc.SerializeBody(writer);
        return new GameDataSubMessage(GameDataTag.Rpc, writer.ToByteArray(includeHeader: false));
    }

    public static GameDataSubMessage FromTransform(uint transformNetId, CustomNetworkTransformUpdate update, bool initialState = false)
    {
        using var writer = MessageWriter.Get(MessageType.Unreliable);
        writer.WritePacked(transformNetId);
        update.Serialize(writer, initialState);
        return new GameDataSubMessage(GameDataTag.Data, writer.ToByteArray(includeHeader: false));
    }

    public static GameDataSubMessage FromSceneChange(int clientId, string sceneName)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.WritePacked(clientId);
        writer.Write(sceneName);
        return new GameDataSubMessage(GameDataTag.SceneChange, writer.ToByteArray(includeHeader: false));
    }

    public static GameDataSubMessage FromReady(int clientId)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.WritePacked(clientId);
        return new GameDataSubMessage(GameDataTag.Ready, writer.ToByteArray(includeHeader: false));
    }

    public static GameDataSubMessage FromSpawn(SpawnObjectPacket packet)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.WritePacked(packet.SpawnId);
        writer.WritePacked(packet.OwnerClientId);
        writer.Write(packet.Flags);
        writer.WritePacked(packet.Components.Count);
        foreach (var comp in packet.Components)
        {
            writer.WritePacked(comp.NetId);
            writer.StartMessage(1);
            writer.Write(comp.InitialData);
            writer.EndMessage();
        }

        return new GameDataSubMessage(GameDataTag.Spawn, writer.ToByteArray(includeHeader: false));
    }

    public bool TryParseRpc(ProtocolRegistry registry, out uint targetNetId, out IRpcMessage? rpc)
    {
        targetNetId = 0;
        rpc = null;
        if (Tag != GameDataTag.Rpc)
        {
            return false;
        }

        using var reader = MessageReader.Get(Payload);
        targetNetId = reader.ReadPackedUInt32();
        var rpcPayload = reader.ReadBytes(reader.BytesRemaining).ToArray();
        rpc = registry.DecodeRpc(rpcPayload);
        return true;
    }

    public bool TryParseData(out uint targetNetId, out byte[] componentData)
    {
        targetNetId = 0;
        componentData = Array.Empty<byte>();
        if (Tag != GameDataTag.Data)
        {
            return false;
        }

        using var reader = MessageReader.Get(Payload);
        targetNetId = reader.ReadPackedUInt32();
        componentData = reader.ReadBytes(reader.BytesRemaining).ToArray();
        return true;
    }

    public bool TryParseSpawn(out SpawnObjectPacket? packet)
    {
        packet = null;
        if (Tag != GameDataTag.Spawn)
        {
            return false;
        }

        using var reader = MessageReader.Get(Payload);
        var spawnId = reader.ReadPackedUInt32();
        var ownerClientId = reader.ReadPackedInt32();
        var flags = reader.ReadByte();
        var count = reader.ReadPackedInt32();
        if (count < 0 || count > 16)
        {
            throw new ProtocolException(GameDataTag.Spawn, Payload, $"Invalid Spawn component count {count}.");
        }

        var components = new List<SpawnComponentEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var netId = reader.ReadPackedUInt32();
            using var compData = reader.ReadMessage();
            components.Add(new SpawnComponentEntry(netId, compData.ReadBytes(compData.BytesRemaining).ToArray()));
        }

        packet = new SpawnObjectPacket(spawnId, ownerClientId, flags, components);
        return true;
    }

    public bool TryParseReady(out int clientId)
    {
        clientId = 0;
        if (Tag != GameDataTag.Ready || Payload.Length == 0)
        {
            return false;
        }

        using var reader = MessageReader.Get(Payload);
        clientId = reader.ReadPackedInt32();
        return true;
    }

    public bool TryParseSceneChange(out int clientId, out string sceneName)
    {
        clientId = 0;
        sceneName = string.Empty;
        if (Tag != GameDataTag.SceneChange || Payload.Length == 0)
        {
            return false;
        }

        using var reader = MessageReader.Get(Payload);
        clientId = reader.ReadPackedInt32();
        sceneName = reader.ReadString();
        return true;
    }

    public bool Equals(GameDataSubMessage? other) =>
        other is not null && Tag == other.Tag && Payload.SequenceEqual(other.Payload);

    public override int GetHashCode() => HashCode.Combine(Tag, Payload.Length);
}

public sealed record PlayerInfoSpawnData(
    byte PlayerId,
    int ClientId,
    string PlayerName,
    int ColorId);

public static class ComponentDataCodec
{
    public const byte NormalGameManagerLogicOptionsTag = 4;

    public static byte ParsePlayerControlPlayerId(byte[] initialData, bool initialState = true)
    {
        using var reader = MessageReader.Get(initialData);
        if (initialState && reader.BytesRemaining >= 2)
        {
            _ = reader.ReadBoolean(); // IsNew
        }

        return reader.BytesRemaining >= 1 ? reader.ReadByte() : byte.MaxValue;
    }

    public static byte[] SerializePlayerControlInitial(byte playerId, bool isNew = true)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.Write(isNew);
        writer.Write(playerId);
        return writer.ToByteArray(includeHeader: false);
    }

    public static byte[] SerializePlayerInfoInitial(
        byte playerId,
        int clientId,
        string name,
        ColorType color,
        uint playerLevel = 1,
        string friendCode = "",
        string puid = "")
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.Write(playerId);
        writer.WritePacked(clientId);
        writer.Write((byte)1); // Outfit count (Default outfit only)
        writer.Write((byte)0); // PlayerOutfitType.Default
        writer.Write(string.IsNullOrWhiteSpace(name) ? $"Player{clientId}" : name);
        writer.WritePacked((int)color);
        writer.Write(string.Empty); // hat
        writer.Write(string.Empty); // pet
        writer.Write("skin_None"); // skin
        writer.Write("visor_EmptyVisor"); // visor
        writer.Write(string.Empty); // nameplate
        for (var i = 0; i < 5; i++)
        {
            writer.Write((byte)1); // cosmetic sequence ids
        }

        writer.Write(playerLevel);
        writer.Write((byte)0); // isDead/disconnected flags
        writer.Write((byte)0); // tasks count
        writer.Write((ushort)0); // role type (Crewmate)
        writer.Write(friendCode);
        writer.Write(puid);
        return writer.ToByteArray(includeHeader: false);
    }

    public static bool TryParsePlayerInfoInitial(byte[] initialData, out PlayerInfoSpawnData? info)
    {
        info = null;
        if (initialData.Length < 4)
        {
            return false;
        }

        try
        {
            using var reader = MessageReader.Get(initialData);
            var playerId = reader.ReadByte();
            var clientId = reader.ReadPackedInt32();
            var outfitCount = reader.ReadByte();
            var playerName = string.Empty;
            var colorId = 0;

            for (var i = 0; i < outfitCount && reader.BytesRemaining > 0; i++)
            {
                var outfitType = reader.ReadByte();
                var name = reader.ReadString();
                var color = reader.ReadPackedInt32();
                _ = reader.ReadString(); // hat
                _ = reader.ReadString(); // pet
                _ = reader.ReadString(); // skin
                _ = reader.ReadString(); // visor
                _ = reader.ReadString(); // nameplate
                for (var s = 0; s < 5 && reader.BytesRemaining > 0; s++)
                {
                    _ = reader.ReadByte();
                }

                if (outfitType == 0 || i == 0)
                {
                    playerName = name;
                    colorId = color;
                }
            }

            info = new PlayerInfoSpawnData(playerId, clientId, playerName, colorId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryExtractGameManagerOptions(byte[] componentData, out IGameOptions? options)
    {
        options = null;
        if (componentData.Length == 0)
        {
            return false;
        }

        using var reader = MessageReader.Get(componentData);
        while (reader.BytesRemaining > 0)
        {
            using var sub = reader.ReadMessage();
            if (sub.Tag == NormalGameManagerLogicOptionsTag)
            {
                options = GameOptionsFactory.Deserialize(sub);
                return true;
            }
        }

        return false;
    }

    public static byte[] SerializeNormalGameManagerInitial(IGameOptions options)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        for (byte tag = 0; tag < NormalGameManagerLogicOptionsTag; tag++)
        {
            writer.StartMessage(tag);
            writer.EndMessage();
        }

        writer.StartMessage(NormalGameManagerLogicOptionsTag);
        GameOptionsFactory.SerializeLengthPrefixed(writer, options);
        writer.EndMessage();
        return writer.ToByteArray(includeHeader: false);
    }

    public static byte[] SerializeGameManagerOptionsDelta(IGameOptions options)
    {
        using var writer = MessageWriter.Get(MessageType.Reliable);
        writer.StartMessage(NormalGameManagerLogicOptionsTag);
        GameOptionsFactory.SerializeLengthPrefixed(writer, options);
        writer.EndMessage();
        return writer.ToByteArray(includeHeader: false);
    }
}
