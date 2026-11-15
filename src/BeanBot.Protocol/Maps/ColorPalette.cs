using System.Reflection;
using System.Text.Json;

namespace BeanBot.Protocol.Maps;

/// <summary>
/// Embedded Appendix A 18-colour palette & default 15-seat roster naming (P2.3, P2.6).
/// Loaded from embedded resource BeanBot.Protocol.Resources.palettes.json so single-file publish works standalone.
/// </summary>
public static class ColorPalette
{
    private sealed record PalettePayload(string[] Palette, string[] DefaultBotNames);

    private static readonly Lazy<(IReadOnlyList<ColorType> Colors, IReadOnlyList<string> Names)> Embedded =
        new(LoadEmbedded);

    public static IReadOnlyList<ColorType> AppendixAOrder => Embedded.Value.Colors;
    public static IReadOnlyList<string> DefaultBotNames => Embedded.Value.Names;

    /// <summary>
    /// Assigns unique Appendix A colour for seatIndex 0..17 (Seat 0 = Host = Red).
    /// </summary>
    public static ColorType GetSeatColor(int seatIndex)
    {
        if (seatIndex < 0 || seatIndex >= AppendixAOrder.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seatIndex),
                seatIndex,
                $"Seat index must be between 0 and {AppendixAOrder.Count - 1}.");
        }

        return AppendixAOrder[seatIndex];
    }

    public static string GetSeatDefaultName(int seatIndex)
    {
        if (seatIndex >= 0 && seatIndex < DefaultBotNames.Count)
        {
            return DefaultBotNames[seatIndex];
        }

        return $"Bean{seatIndex:D2}";
    }

    public static bool TryParse(string? colorName, out ColorType color)
    {
        if (!string.IsNullOrWhiteSpace(colorName) &&
            Enum.TryParse<ColorType>(colorName.Trim(), ignoreCase: true, out color) &&
            Enum.IsDefined(color))
        {
            return true;
        }

        color = default;
        return false;
    }

    private static (IReadOnlyList<ColorType> Colors, IReadOnlyList<string> Names) LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "BeanBot.Protocol.Resources.palettes.json";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded palette resource '{resourceName}' not found.");
        var payload = JsonSerializer.Deserialize<PalettePayload>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize embedded palette resource.");

        var colors = new List<ColorType>(payload.Palette.Length);
        foreach (var entry in payload.Palette)
        {
            if (!Enum.TryParse<ColorType>(entry, ignoreCase: true, out var color))
            {
                throw new InvalidOperationException($"Invalid color name '{entry}' in embedded palette resource.");
            }

            colors.Add(color);
        }

        return (colors.AsReadOnly(), Array.AsReadOnly(payload.DefaultBotNames));
    }
}
