using BeanBot.Protocol.Hazel;

namespace BeanBot.Protocol;

/// <summary>
/// Among Us encoded game version (year*25000 + month*1800 + day*50 + revision).
/// Source: Impostor.Api.Innersloth.GameVersion.
/// </summary>
public readonly struct GameVersion : IEquatable<GameVersion>, IComparable<GameVersion>
{
    private const int YearMask = 25000;
    private const int MonthMask = 1800;
    private const int DayMask = 50;
    private const int DisableServerAuthorityFlag = 25;

    public GameVersion(int value) => Value = value;

    public GameVersion(int year, int month, int day, int revision = 0)
    {
        Value = (year * YearMask) + (month * MonthMask) + (day * DayMask) + revision;
    }

    public int Value { get; }
    public int Year => Value / YearMask;
    public int Month => (Value % YearMask) / MonthMask;
    public int Day => ((Value % YearMask) % MonthMask) / DayMask;
    public int Revision => Value % DayMask;
    public bool HasDisableServerAuthorityFlag => Revision >= DisableServerAuthorityFlag;

    public GameVersion Normalize() =>
        HasDisableServerAuthorityFlag ? new GameVersion(Value - DisableServerAuthorityFlag) : this;

    public static bool operator ==(GameVersion left, GameVersion right) => left.Value == right.Value;
    public static bool operator !=(GameVersion left, GameVersion right) => left.Value != right.Value;
    public static bool operator >(GameVersion left, GameVersion right) => left.Value > right.Value;
    public static bool operator >=(GameVersion left, GameVersion right) => left.Value >= right.Value;
    public static bool operator <(GameVersion left, GameVersion right) => left.Value < right.Value;
    public static bool operator <=(GameVersion left, GameVersion right) => left.Value <= right.Value;

    public int CompareTo(GameVersion other) => Value.CompareTo(other.Value);
    public bool Equals(GameVersion other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is GameVersion other && Equals(other);
    public override int GetHashCode() => Value;

    public override string ToString() =>
        $"{Year}.{Month}.{Day}{(Revision == 0 ? string.Empty : "." + Revision)}";
}

/// <summary>
/// Single source of truth for CLIENT_VERSION and the compatibility-group table (G2, 5.7).
/// Source: Impostor CompatibilityManager.cs (build 7492 mobile = 19.0 / 2026.7.21).
/// </summary>
public static class ProtocolVersion
{
    /// <summary>
    /// Single CLIENT_VERSION constant for the entire codebase (G2).
    /// 2026.7.21 = Among Us 19.0 mobile build 7492 (2026-09-29).
    /// </summary>
    public static readonly GameVersion CLIENT_VERSION = new(2026, 7, 21);

    /// <summary>Human-readable semantic release tag corresponding to CLIENT_VERSION.</summary>
    public const string CLIENT_SEMANTIC_VERSION = "19.0.0";

    /// <summary>
    /// Compatibility groups sourced from Impostor CompatibilityManager.
    /// Peers in the same group can cross-play.
    /// </summary>
    public static readonly IReadOnlyList<GameVersion[]> CompatibilityGroups =
    [
        [
            new(2024, 3, 1),
            new(2024, 4, 1),
            new(2024, 4, 2),
            new(2024, 8, 10),
        ],
        [
            new(2024, 8, 11), // 16.0.0
        ],
        [
            new(2025, 4, 15), // 16.0.5 & 16.1.0
        ],
        [
            new(2025, 7, 15),  // 17.0.0
            new(2025, 9, 12),  // 17.0.1
            new(2025, 10, 9),  // 17.1
            new(2025, 11, 6),  // 17.1.1
            new(2025, 12, 8),  // 17.1.2
            new(2025, 11, 5),  // 17.2
            new(2026, 1, 22),  // 17.2 hotfix
            new(2026, 2, 2),   // 17.2.2
            new(2026, 1, 12),  // 17.3
            new(2026, 3, 17),  // 17.3.1
            new(2026, 3, 18),  // 17.4 pc
            new(2026, 4, 23),  // 17.4 mobile
        ],
        [
            new(2026, 7, 15),  // 18.0 pc
            new(2026, 7, 16),  // 18.0 mobile
        ],
        [
            new(2026, 7, 20),  // 19.0 pc
            new(2026, 7, 21),  // 19.0 mobile (CLIENT_VERSION)
        ],
    ];

    public static int GetCompatibilityGroupId(GameVersion version)
    {
        var normalized = version.Normalize();
        for (var i = 0; i < CompatibilityGroups.Count; i++)
        {
            if (CompatibilityGroups[i].Contains(normalized))
            {
                return i;
            }
        }

        return -1;
    }

    public static bool AreCompatible(GameVersion a, GameVersion b)
    {
        var groupA = GetCompatibilityGroupId(a);
        var groupB = GetCompatibilityGroupId(b);
        return groupA >= 0 ? groupA == groupB : a.Normalize() == b.Normalize();
    }
}

/// <summary>
/// Single builder for the Hazel hello platform record (5.7).
/// </summary>
public static class PlatformRecord
{
    public static PlatformSpecificData Build(string displayName, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device id is required.", nameof(deviceId));
        }

        // Spec D2 / 5.7: Platform is always Android (tag 7).
        return new PlatformSpecificData(Platforms.Android, displayName, PlatformDeviceId: deviceId);
    }

    public static void WriteHandshake(
        IMessageWriter writer,
        string displayName,
        string deviceId,
        Language language = Language.English,
        QuickChatModes chatMode = QuickChatModes.QuickChatOnly,
        CrossplayFlags crossplayFlags = CrossplayFlags.All,
        uint lastNonceReceived = 0u)
    {
        var platform = Build(displayName, deviceId);
        writer.Write(ProtocolVersion.CLIENT_VERSION.Value);
        writer.Write(displayName);
        writer.Write(lastNonceReceived);
        writer.Write((uint)language);
        writer.Write((byte)chatMode);
        platform.Serialize(writer);
        writer.Write((int)crossplayFlags);
        writer.Write((byte)0); // purpose unknown, hardcoded 0 since 2021.12.14
    }
}
