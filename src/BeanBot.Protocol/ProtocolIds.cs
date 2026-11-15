namespace BeanBot.Protocol;

/// <summary>
/// Root message flags (5.4 + Impostor MessageFlags).
/// </summary>
public static class RootMessageId
{
    public const byte HostGame = 0x00;
    public const byte JoinGame = 0x01;
    public const byte StartGame = 0x02;
    public const byte RemoveGame = 0x03;
    public const byte RemovePlayer = 0x04;
    public const byte GameData = 0x05;
    public const byte GameDataTo = 0x06;
    public const byte JoinedGame = 0x07;
    public const byte EndGame = 0x08;
    public const byte GetGameList = 0x09;
    public const byte AlterGame = 0x0a;
    public const byte KickPlayer = 0x0b;
    public const byte WaitForHost = 0x0c;
    public const byte Redirect = 0x0d;
    public const byte ReselectServer = 0x0e;
    public const byte GetGameListV2 = 0x10;
    public const byte ReportPlayer = 0x11;

    // Extended Impostor root flags
    public const byte QuickMatch = 0x12;
    public const byte QuickMatchHost = 0x13;
    public const byte SetGameSession = 0x14;
    public const byte SetActivePodType = 0x15;
    public const byte QueryPlatformIds = 0x16;
    public const byte QueryLobbyInfo = 0x17;
    public const byte EndGameHostMigration = 0x18;
    public const byte HostModdedGame = 0x19;
    public const byte PackedGameDataTo = 0x1a;

    /// <summary>All 17 root message ids specified in Section 5.4.</summary>
    public static readonly byte[] Spec54Ids =
    [
        HostGame, JoinGame, StartGame, RemoveGame, RemovePlayer,
        GameData, GameDataTo, JoinedGame, EndGame, GetGameList,
        AlterGame, KickPlayer, WaitForHost, Redirect, ReselectServer,
        GetGameListV2, ReportPlayer,
    ];
}

/// <summary>
/// RPC call ids (5.5 + Impostor RpcCalls.json).
/// </summary>
public static class RpcId
{
    public const byte PlayAnimation = 0x00;
    public const byte CompleteTask = 0x01;
    public const byte SyncSettings = 0x02;
    public const byte SetInfected = 0x03;
    public const byte Exiled = 0x04;
    public const byte CheckName = 0x05;
    public const byte SetName = 0x06;
    public const byte CheckColor = 0x07;
    public const byte SetColor = 0x08;
    public const byte SetHat = 0x09;
    public const byte SetSkin = 0x0a;
    public const byte ReportDeadBody = 0x0b;
    public const byte MurderPlayer = 0x0c;
    public const byte SendChat = 0x0d;
    public const byte StartMeeting = 0x0e;
    public const byte SetScanner = 0x0f;
    public const byte SendChatNote = 0x10;
    public const byte SetPet = 0x11;
    public const byte SetStartCounter = 0x12;
    public const byte EnterVent = 0x13;
    public const byte ExitVent = 0x14;
    public const byte SnapTo = 0x15;
    public const byte Close = 0x16;
    public const byte VotingComplete = 0x17;
    public const byte CastVote = 0x18;
    public const byte ClearVote = 0x19;
    public const byte AddVote = 0x1a;
    public const byte CloseDoorsOfType = 0x1b;
    public const byte RepairSystem = 0x1c;
    public const byte SetTasks = 0x1d;
    public const byte ClimbLadder = 0x1f;
    public const byte UsePlatform = 0x20;

    // Modern Among Us / Impostor RPCs
    public const byte SendQuickChat = 0x21;
    public const byte BootFromVent = 0x22;
    public const byte UpdateSystem = 0x23;
    public const byte SetLevel = 0x26;
    public const byte SetHatStr = 0x27;
    public const byte SetSkinStr = 0x28;
    public const byte SetPetStr = 0x29;
    public const byte SetVisorStr = 0x2a;
    public const byte SetNamePlateStr = 0x2b;
    public const byte SetRole = 0x2c;
    public const byte CheckMurder = 0x2f;

    /// <summary>All 31 RPC ids specified in Section 5.5.</summary>
    public static readonly byte[] Spec55Ids =
    [
        CompleteTask, SyncSettings, SetInfected, Exiled, CheckName, SetName,
        CheckColor, SetColor, SetHat, SetSkin, ReportDeadBody, MurderPlayer,
        SendChat, StartMeeting, SetScanner, SendChatNote, SetPet, SetStartCounter,
        EnterVent, ExitVent, SnapTo, Close, VotingComplete, CastVote, ClearVote,
        AddVote, CloseDoorsOfType, RepairSystem, SetTasks, ClimbLadder, UsePlatform,
    ];
}

/// <summary>
/// Inner GameData message sub-tags (Source: Impostor GameDataTag.cs).
/// </summary>
public static class GameDataTag
{
    public const byte Data = 1;
    public const byte Rpc = 2;
    public const byte Spawn = 4;
    public const byte Despawn = 5;
    public const byte SceneChange = 6;
    public const byte Ready = 7;
    public const byte ChangeSettings = 8;
}

/// <summary>
/// Spawnable InnerNetObject IDs (Source: Impostor Game.Data.cs).
/// </summary>
public static class SpawnObjectId
{
    public const uint SkeldShipStatus = 0;
    public const uint MeetingHud = 1;
    public const uint LobbyBehaviour = 2;
    public const uint PlayerControl = 4;
    public const uint NormalGameManager = 10;
    public const uint PlayerInfo = 11;
    public const uint VoteBanSystem = 12;
}

public enum Platforms : byte
{
    Unknown = 0,
    StandaloneEpicPC = 1,
    StandaloneSteamPC = 2,
    StandaloneMac = 3,
    StandaloneWin10 = 4,
    StandaloneItch = 5,
    IPhone = 6,
    Android = 7,
    Switch = 8,
    Xbox = 9,
    Playstation = 10,
}

public enum Language : uint
{
    English = 0,
    Latam = 1,
    Brazilian = 2,
    Portuguese = 3,
    Korean = 4,
    Russian = 5,
    Dutch = 6,
    Filipino = 7,
    French = 8,
    German = 9,
    Italian = 10,
    Japanese = 11,
    Spanish = 12,
    SChinese = 13,
    TChinese = 14,
    Irish = 15,
}

public enum QuickChatModes : byte
{
    FreeChatOrQuickChat = 1,
    QuickChatOnly = 2,
}

public enum QuickChatPhraseType : byte
{
    Empty = 0,
    PlayerId = 1,
    SimplePhrase = 2,
    ComplexPhrase = 3,
}

[Flags]
public enum CrossplayFlags : int
{
    None = 0,
    All = int.MaxValue,
}

public enum AlterGameTags : byte
{
    ChangePrivacy = 1,
}

public enum DisconnectReason : byte
{
    ExitGame = 0,
    GameFull = 1,
    GameStarted = 2,
    GameNotFound = 3,
    IncorrectVersion = 5,
    Banned = 6,
    Kicked = 7,
    Custom = 8,
    InvalidName = 9,
    Hacking = 10,
    NotAuthorized = 11,
    ConnectionLimit = 12,
    Destroy = 16,
    Error = 17,
    IncorrectGame = 18,
    ServerRequest = 19,
    ServerFull = 20,
    MismatchedVersion = 21,

    // Extracted from Among Us 2026.7.21 global-metadata.dat (DisconnectReasons field default values).
    InternalPlayerMissing = 100,
    InternalNonceFailure = 101,
    InternalConnectionToken = 102,
    PlatformLock = 103,
    LobbyInactivity = 104,
    MatchmakerInactivity = 105,
    InvalidGameOptions = 106,
    NoServersAvailable = 107,
    QuickmatchDisabled = 108,
    TooManyGames = 109,
    QuickchatLock = 110,
    MatchmakerFull = 111,
    Sanctions = 112,
    ServerError = 113,
    SelfPlatformLock = 114,
    DuplicateConnectionDetected = 115,
    TooManyRequests = 116,
    FocusLostBackground = 207,
    IntentionalLeaving = 208,
    FocusLost = 209,
    NewConnection = 210,
    PlatformParentalControlsBlock = 211,
    PlatformUserBlock = 212,
    PlatformFailedToGetUserBlock = 213,
    ServerNotFound = 214,
    ClientTimeout = 215,
    ErrorAuthNonceFailure = 216,
    Unknown = 255,
}

public enum GameOverReason : byte
{
    CrewmatesByVote = 0,
    CrewmatesByTask = 1,
    ImpostorsByVote = 2,
    ImpostorsByKill = 3,
    ImpostorsBySabotage = 4,
    ImpostorDisconnect = 5,
    CrewmateDisconnect = 6,
    HideAndSeek_CrewmatesByTimer = 7,
    HideAndSeek_ImpostorsByKills = 8,
}

/// <summary>
/// 18-colour palette (Appendix A + Impostor ColorType.json).
/// </summary>
public enum ColorType : byte
{
    Red = 0,
    Blue = 1,
    Green = 2,
    Pink = 3,
    Orange = 4,
    Yellow = 5,
    Black = 6,
    White = 7,
    Purple = 8,
    Brown = 9,
    Cyan = 10,
    Lime = 11,
    Maroon = 12,
    Rose = 13,
    Banana = 14,
    Gray = 15,
    Tan = 16,
    Coral = 17,
}

public enum MapTypes : byte
{
    Skeld = 0,
    MiraHQ = 1,
    Polus = 2,
    Dleks = 3,
    Airship = 4,
    Fungle = 5,
}

public enum KillDistances : byte
{
    Short = 0,
    Normal = 1,
    Long = 2,
}

public enum SystemTypes : byte
{
    Hallway = 0,
    Storage = 1,
    Cafeteria = 2,
    Reactor = 3,
    UpperEngine = 4,
    Nav = 5,
    Admin = 6,
    Electrical = 7,
    LifeSupp = 8,
    Shields = 9,
    MedBay = 10,
    Security = 11,
    Weapons = 12,
    LowerEngine = 13,
    Comms = 14,
    ShipTasks = 15,
    Doors = 16,
    Sabotage = 17,
}

public enum TaskTypes : byte
{
    SubmitScan = 0,
    PrimeShields = 1,
    FuelEngines = 2,
    ChartCourse = 3,
    StartReactor = 4,
    SwipeCard = 5,
    ClearAsteroids = 6,
    UploadData = 7,
    InspectSample = 8,
    EmptyChute = 9,
    EmptyGarbage = 10,
    AlignEngineOutput = 11,
    FixWiring = 12,
    CalibrateDistributor = 13,
    DivertPower = 14,
    UnlockManifolds = 15,
    ResetReactor = 16,
    FixLights = 17,
    CleanO2Filter = 18,
    FixComms = 19,
    RestoreOxy = 20,
    StabilizeSteering = 21,
    VentCleaning = 60,
    None = 61,
}

public enum ChatNoteType : byte
{
    DidVote = 0,
}

[Flags]
public enum MurderResultFlags : int
{
    Succeeded = 1,
    FailedError = 2,
    FailedProtected = 4,
    DecisionByHost = 8,
}

[Flags]
public enum SpawnFlags : byte
{
    None = 0,
    IsClientCharacter = 1,
}

public enum RoleTypes : ushort
{
    Crewmate = 0,
    Impostor = 1,
    Scientist = 2,
    Engineer = 3,
    GuardianAngel = 4,
    Shapeshifter = 5,
    CrewmateGhost = 6,
    ImpostorGhost = 7,
    Noisemaker = 8,
    Phantom = 9,
    Tracker = 10,
    Detective = 12,
    Viper = 18,
    Judge = 19,
    SpiritGuide = 21,
}

public enum GameModes : byte
{
    None = 0,
    Normal = 1,
    HideNSeek = 2,
    NormalFools = 3,
    SeekFools = 4,
}

public enum TaskBarUpdate : byte
{
    Always = 0,
    Meetings = 1,
    Never = 2,
}

public enum SpecialGameModes : byte
{
    None = 0,
    AprilFools = 1,
}

public enum RulesPresets : byte
{
    Standard = 0,
    StandardRoles = 1,
    Flashlight = 2,
    Custom = 100,
}

[Flags]
public enum GameKeywords : uint
{
    All = 0,
    English = 256,
}

public enum GameTags : byte
{
    None = 0,
    Beginner = 1,
    Intermediate = 2,
    Expert = 3,
}
