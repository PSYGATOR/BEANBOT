using BeanBot.Protocol.Hazel;

namespace BeanBot.Protocol;

public interface IGameOptions
{
    byte Version { get; }
    GameModes GameMode { get; }
    byte MaxPlayers { get; set; }
    GameKeywords Keywords { get; set; }
    MapTypes Map { get; set; }
    int NumImpostors { get; set; }
    bool IsDefaults { get; set; }
    void Serialize(IMessageWriter writer);
}

public sealed record NormalGameOptions : IGameOptions
{
    public const byte LatestVersion = 12;

    public byte Version { get; init; } = LatestVersion;
    public GameModes GameMode => GameModes.Normal;
    public SpecialGameModes SpecialMode { get; set; } = SpecialGameModes.None;
    public RulesPresets RulesPreset { get; set; } = RulesPresets.Custom;
    public byte MaxPlayers { get; set; } = 15;
    public GameKeywords Keywords { get; set; } = GameKeywords.English;
    public MapTypes Map { get; set; } = MapTypes.Skeld;
    public int NumImpostors { get; set; } = 1;
    public bool IsDefaults { get; set; } = true;
    public float PlayerSpeedMod { get; set; } = 1.0f;
    public float CrewLightMod { get; set; } = 1.0f;
    public float ImpostorLightMod { get; set; } = 1.5f;
    public float KillCooldown { get; set; } = 15.0f;
    public int NumCommonTasks { get; set; } = 1;
    public int NumLongTasks { get; set; } = 1;
    public int NumShortTasks { get; set; } = 2;
    public int NumEmergencyMeetings { get; set; } = 1;
    public int EmergencyCooldown { get; set; } = 15;
    public bool GhostsDoTasks { get; set; } = true;
    public KillDistances KillDistance { get; set; } = KillDistances.Normal;
    public int DiscussionTime { get; set; } = 15;
    public int VotingTime { get; set; } = 120;
    public bool ConfirmImpostor { get; set; } = true;
    public bool VisualTasks { get; set; } = false;
    public bool AnonymousVotes { get; set; } = false;
    public TaskBarUpdate TaskBarUpdate { get; set; } = TaskBarUpdate.Always;
    public GameTags Tag { get; set; } = GameTags.Beginner;
    public byte[] RawRoleOptionsTail { get; set; } =
    [
        0x0B,
        0x05, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x0A,
        0x08, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x0F,
        0x05, 0x04, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x3C,
        0x0A, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
        0x1E, 0x0F, 0x09, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00,
        0x0F, 0x1E, 0x0A, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00,
        0x0F, 0x1E, 0x01, 0x08, 0x00, 0x00, 0x00, 0x02, 0x00,
        0x00, 0x0A, 0x01, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x00, 0x03, 0x12, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
        0x0F, 0x13, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x32,
        0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x14,
    ];

    public static NormalGameOptions Deserialize(IMessageReader reader, byte version)
    {
        if (version < GameOptionsFactory.ModularOptionsDataVersion || version > LatestVersion)
        {
            throw new ProtocolException(reader.Tag, reader.Buffer, $"Unsupported NormalGameOptions version {version}.");
        }

        var opts = new NormalGameOptions { Version = version };
        if (version >= 8)
        {
            opts.SpecialMode = (SpecialGameModes)reader.ReadByte();
            opts.RulesPreset = (RulesPresets)reader.ReadByte();
        }

        opts.MaxPlayers = reader.ReadByte();
        opts.Keywords = (GameKeywords)reader.ReadUInt32();
        opts.Map = (MapTypes)reader.ReadByte();
        opts.PlayerSpeedMod = reader.ReadSingle();
        opts.CrewLightMod = reader.ReadSingle();
        opts.ImpostorLightMod = reader.ReadSingle();
        opts.KillCooldown = reader.ReadSingle();
        opts.NumCommonTasks = reader.ReadByte();
        opts.NumLongTasks = reader.ReadByte();
        opts.NumShortTasks = reader.ReadByte();
        opts.NumEmergencyMeetings = reader.ReadInt32();
        opts.NumImpostors = reader.ReadByte();
        opts.KillDistance = (KillDistances)reader.ReadByte();
        opts.DiscussionTime = reader.ReadInt32();
        opts.VotingTime = reader.ReadInt32();
        opts.IsDefaults = reader.ReadBoolean();
        opts.EmergencyCooldown = reader.ReadByte();
        opts.ConfirmImpostor = reader.ReadBoolean();
        opts.VisualTasks = reader.ReadBoolean();
        opts.AnonymousVotes = reader.ReadBoolean();
        opts.TaskBarUpdate = (TaskBarUpdate)reader.ReadByte();
        if (version >= 9)
        {
            opts.Tag = (GameTags)reader.ReadByte();
        }

        opts.RawRoleOptionsTail = reader.BytesRemaining > 0
            ? reader.ReadBytes(reader.BytesRemaining).ToArray()
            : [0];

        return opts;
    }

    public void Serialize(IMessageWriter writer)
    {
        if (Version >= 8)
        {
            writer.Write((byte)SpecialMode);
            writer.Write((byte)RulesPreset);
        }

        writer.Write(MaxPlayers);
        writer.Write((uint)Keywords);
        writer.Write((byte)Map);
        writer.Write(PlayerSpeedMod);
        writer.Write(CrewLightMod);
        writer.Write(ImpostorLightMod);
        writer.Write(KillCooldown);
        writer.Write((byte)NumCommonTasks);
        writer.Write((byte)NumLongTasks);
        writer.Write((byte)NumShortTasks);
        writer.Write(NumEmergencyMeetings);
        writer.Write((byte)NumImpostors);
        writer.Write((byte)KillDistance);
        writer.Write(DiscussionTime);
        writer.Write(VotingTime);
        writer.Write(IsDefaults);
        writer.Write((byte)EmergencyCooldown);
        writer.Write(ConfirmImpostor);
        writer.Write(VisualTasks);
        writer.Write(AnonymousVotes);
        writer.Write((byte)TaskBarUpdate);
        if (Version >= 9)
        {
            writer.Write((byte)Tag);
        }

        writer.Write(RawRoleOptionsTail.Length > 0 ? RawRoleOptionsTail : [0]);
    }

    public bool Equals(NormalGameOptions? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Version == other.Version &&
               SpecialMode == other.SpecialMode &&
               RulesPreset == other.RulesPreset &&
               MaxPlayers == other.MaxPlayers &&
               Keywords == other.Keywords &&
               Map == other.Map &&
               NumImpostors == other.NumImpostors &&
               IsDefaults == other.IsDefaults &&
               PlayerSpeedMod.Equals(other.PlayerSpeedMod) &&
               CrewLightMod.Equals(other.CrewLightMod) &&
               ImpostorLightMod.Equals(other.ImpostorLightMod) &&
               KillCooldown.Equals(other.KillCooldown) &&
               NumCommonTasks == other.NumCommonTasks &&
               NumLongTasks == other.NumLongTasks &&
               NumShortTasks == other.NumShortTasks &&
               NumEmergencyMeetings == other.NumEmergencyMeetings &&
               EmergencyCooldown == other.EmergencyCooldown &&
               GhostsDoTasks == other.GhostsDoTasks &&
               KillDistance == other.KillDistance &&
               DiscussionTime == other.DiscussionTime &&
               VotingTime == other.VotingTime &&
               ConfirmImpostor == other.ConfirmImpostor &&
               VisualTasks == other.VisualTasks &&
               AnonymousVotes == other.AnonymousVotes &&
               TaskBarUpdate == other.TaskBarUpdate &&
               Tag == other.Tag &&
               RawRoleOptionsTail.SequenceEqual(other.RawRoleOptionsTail);
    }

    public override int GetHashCode() => HashCode.Combine(Version, MaxPlayers, Map, NumImpostors, PlayerSpeedMod, KillCooldown, VotingTime, RawRoleOptionsTail.Length);
}

public static class GameOptionsFactory
{
    public const byte ModularOptionsDataVersion = 7;

    public static void Serialize(IMessageWriter writer, IGameOptions gameOptions)
    {
        if (gameOptions.Version < ModularOptionsDataVersion)
        {
            throw new NotSupportedException($"GameOptions version {gameOptions.Version} < {ModularOptionsDataVersion} not supported.");
        }

        writer.WritePacked(0);
        WriteModularPayload(writer, gameOptions);
    }

    /// <summary>
    /// Serializes modular options prefixed by their packed byte length (<c>WriteBytesAndSize</c>),
    /// matching live Among Us <c>NormalGameManager</c> (<c>LogicOptionsNormal</c>) wire format.
    /// </summary>
    public static void SerializeLengthPrefixed(IMessageWriter writer, IGameOptions gameOptions)
    {
        if (gameOptions.Version < ModularOptionsDataVersion)
        {
            throw new NotSupportedException($"GameOptions version {gameOptions.Version} < {ModularOptionsDataVersion} not supported.");
        }

        using var inner = MessageWriter.Get(MessageType.Reliable);
        WriteModularPayload(inner, gameOptions);
        var bytes = inner.ToByteArray(includeHeader: false);
        writer.WriteBytesAndSize(bytes);
    }

    private static void WriteModularPayload(IMessageWriter writer, IGameOptions gameOptions)
    {
        writer.Write(gameOptions.Version);
        writer.StartMessage(0);
        writer.Write((byte)gameOptions.GameMode);
        gameOptions.Serialize(writer);
        writer.EndMessage();
    }

    public static IGameOptions Deserialize(IMessageReader reader)
    {
        reader.ReadPackedInt32();
        var version = reader.ReadByte();
        if (version < ModularOptionsDataVersion)
        {
            throw new ProtocolException(reader.Tag, reader.Buffer, $"Legacy GameOptions version {version} not supported.");
        }

        using var optionsReader = reader.ReadMessage();
        var gameMode = (GameModes)optionsReader.ReadByte();
        return gameMode switch
        {
            GameModes.Normal or GameModes.NormalFools => NormalGameOptions.Deserialize(optionsReader, version),
            _ => throw new ProtocolException(reader.Tag, reader.Buffer, $"Unsupported GameMode {gameMode}."),
        };
    }
}
