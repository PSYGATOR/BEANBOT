using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using BeanBot.Protocol.Hazel;

namespace BeanBot.Protocol;

/// <summary>
/// 4-char or 6-char Among Us lobby code (Source: Impostor GameCodeParser.cs).
/// </summary>
public readonly struct GameCode : IEquatable<GameCode>
{
    private const string V2 = "QWXRTYLPESDFGHUJKZOCVBINMA";
    private static readonly int[] V2Map = Enumerable.Range(65, 26).Select(v => V2.IndexOf((char)v)).ToArray();

    public GameCode(int value)
    {
        Value = value;
        Code = IntToGameName(value);
    }

    public GameCode(string code)
    {
        Code = code.ToUpperInvariant();
        Value = GameNameToInt(Code);
    }

    public int Value { get; }
    public string Code { get; }
    public bool IsInvalid => Value == -1;

    public static GameCode Create() => new(GenerateCode(6));
    public static GameCode From(int value) => new(value);
    public static GameCode From(string code) => new(code);

    public void Serialize(IMessageWriter writer) => writer.Write(Value);

    public bool Equals(GameCode other) => Value == other.Value && Code == other.Code;
    public override bool Equals(object? obj) => obj is GameCode other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Value, Code);
    public override string ToString() => Code;
    public static bool operator ==(GameCode left, GameCode right) => left.Equals(right);
    public static bool operator !=(GameCode left, GameCode right) => !left.Equals(right);

    public static string IntToGameName(int input)
    {
        if (input < -1)
        {
            var a = input & 0x3FF;
            var b = (input >> 10) & 0xFFFFF;
            return new string(
            [
                V2[a % 26],
                V2[a / 26],
                V2[b % 26],
                V2[(b / 26) % 26],
                V2[(b / (26 * 26)) % 26],
                V2[(b / (26 * 26 * 26)) % 26],
            ]);
        }

        Span<byte> code = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(code, input);
        return Encoding.UTF8.GetString(code);
    }

    public static int GameNameToInt(string code)
    {
        var upper = code.ToUpperInvariant();
        if (upper.Any(c => c is < 'A' or > 'Z'))
        {
            return -1;
        }

        if (upper.Length == 6)
        {
            var a = V2Map[upper[0] - 65];
            var b = V2Map[upper[1] - 65];
            var c = V2Map[upper[2] - 65];
            var d = V2Map[upper[3] - 65];
            var e = V2Map[upper[4] - 65];
            var f = V2Map[upper[5] - 65];

            var one = (a + (26 * b)) & 0x3FF;
            var two = c + (26 * (d + (26 * (e + (26 * f)))));
            return (int)(one | ((two << 10) & 0x3FFFFC00) | 0x80000000);
        }

        if (upper.Length == 4)
        {
            return upper[0] | ((upper[1] | ((upper[2] | (upper[3] << 8)) << 8)) << 8);
        }

        return -1;
    }

    public static int GenerateCode(int len = 6)
    {
        if (len is not (4 or 6))
        {
            throw new ArgumentException("GameCode length must be 4 or 6.", nameof(len));
        }

        Span<byte> data = stackalloc byte[len];
        RandomNumberGenerator.Fill(data);
        Span<char> chars = stackalloc char[len];
        for (var i = 0; i < len; i++)
        {
            chars[i] = V2[V2Map[data[i] % 26]];
        }

        return GameNameToInt(new string(chars));
    }
}

public sealed record PlatformSpecificData(
    Platforms Platform,
    string PlatformName,
    ulong? XboxPlatformId = null,
    ulong? PsnPlatformId = null,
    string? PlatformDeviceId = null) : IEquatable<PlatformSpecificData>
{
    public bool Equals(PlatformSpecificData? other) =>
        other is not null &&
        Platform == other.Platform &&
        PlatformName == other.PlatformName &&
        XboxPlatformId == other.XboxPlatformId &&
        PsnPlatformId == other.PsnPlatformId;

    public override int GetHashCode() =>
        HashCode.Combine(Platform, PlatformName, XboxPlatformId, PsnPlatformId);

    public static PlatformSpecificData Deserialize(IMessageReader reader)
    {
        var platform = (Platforms)reader.Tag;
        var platformName = reader.ReadString();
        ulong? xbox = platform == Platforms.Xbox ? reader.ReadUInt64() : null;
        ulong? psn = platform == Platforms.Playstation ? reader.ReadUInt64() : null;
        return new PlatformSpecificData(platform, platformName, xbox, psn);
    }

    public void Serialize(IMessageWriter writer)
    {
        writer.StartMessage((byte)Platform);
        writer.Write(PlatformName);
        if (Platform == Platforms.Xbox)
        {
            writer.Write(XboxPlatformId ?? throw new InvalidOperationException("XboxPlatformId required for Xbox."));
        }
        else if (Platform == Platforms.Playstation)
        {
            writer.Write(PsnPlatformId ?? throw new InvalidOperationException("PsnPlatformId required for Playstation."));
        }

        writer.EndMessage();
    }
}

public static class Vector2Encoding
{
    private const float Range = 50f;

    public static void WriteVector2(this IMessageWriter writer, Vector2 vector)
    {
        var nx = Math.Clamp((vector.X - -Range) / (Range - -Range), 0f, 1f);
        var ny = Math.Clamp((vector.Y - -Range) / (Range - -Range), 0f, 1f);
        writer.Write((ushort)(nx * ushort.MaxValue));
        writer.Write((ushort)(ny * ushort.MaxValue));
    }

    public static Vector2 ReadVector2(this IMessageReader reader)
    {
        var x = reader.ReadUInt16() / (float)ushort.MaxValue;
        var y = reader.ReadUInt16() / (float)ushort.MaxValue;
        return new Vector2(-Range + (2f * Range * Math.Clamp(x, 0f, 1f)), -Range + (2f * Range * Math.Clamp(y, 0f, 1f)));
    }
}

public sealed class GameFilterOptions : IEquatable<GameFilterOptions>
{
    public List<string> FilterTags { get; init; } = new();

    public static GameFilterOptions CreateDefault() => new() { FilterTags = ["Beginner"] };

    public static GameFilterOptions Deserialize(IMessageReader reader)
    {
        var count = reader.ReadPackedInt32();
        if (count < 0 || count > 256)
        {
            throw new ProtocolException(reader.Tag, reader.Buffer, $"Invalid GameFilterOptions count {count}.");
        }

        var tags = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            tags.Add(reader.ReadString());
        }

        return new GameFilterOptions { FilterTags = tags };
    }

    public void Serialize(IMessageWriter writer)
    {
        writer.WritePacked(FilterTags.Count);
        foreach (var tag in FilterTags)
        {
            writer.Write(tag);
        }
    }

    public bool Equals(GameFilterOptions? other) =>
        other is not null && FilterTags.SequenceEqual(other.FilterTags);

    public override bool Equals(object? obj) => Equals(obj as GameFilterOptions);
    public override int GetHashCode() => FilterTags.Count;
}
