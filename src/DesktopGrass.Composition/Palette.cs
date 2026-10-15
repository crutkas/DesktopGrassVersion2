// Palette.cs - scene palettes (blade hues from the original, plus accents for the restyle).
using System;
using System.Linq;
using Windows.UI;

namespace DesktopGrass.Composition;

internal enum SceneKind { Meadow, Desert, Winter, Autumn, Ocean, City }

internal sealed class Palette
{
    public required SceneKind Kind { get; init; }
    public required string Name { get; init; }
    public required Color[] Blades { get; init; }
    public required Color[] Flowers { get; init; }
    public required Color[] Caps { get; init; }
    public Color Stem { get; init; } = Anim.Hex(0xF5F5DC);
    public Color FlowerCenter { get; init; } = Anim.Hex(0xFFD54F);
    /// <summary>How much darker the base of a blade is than its tip (0..1).</summary>
    public float BaseShade { get; init; } = 0.55f;
    public float FlowerChance { get; init; } = 0.04f;
    public float MushroomChance { get; init; } = 0.025f;
    /// <summary>Scene grass height multiplier (animated as one Scale.Y per layer).</summary>
    public float GrassHeight { get; init; } = 1f;

    private static Color[] Hexes(params uint[] values) => values.Select(v => Anim.Hex(v)).ToArray();

    public static readonly Palette Meadow = new()
    {
        Kind = SceneKind.Meadow,
        Name = "Meadow",
        Blades = Hexes(0x2C5E1A, 0x3A7A24, 0x4C9A2E, 0x66B845, 0x7AC957, 0x8FD96A),
        Flowers = Hexes(0xFFEB3B, 0xFFA726, 0xFF80AB, 0xE1BEE7, 0xFFFFFF, 0xEF5350),
        Caps = Hexes(0xD32F2F, 0x8D6E63, 0xC9A66B, 0xFFF8E1, 0xE57373, 0x6D4C41),
    };

    public static readonly Palette Desert = new()
    {
        Kind = SceneKind.Desert,
        Name = "Desert",
        Blades = Hexes(0xC9A26B, 0xB48A56, 0xD9B57A, 0x8F6E3F, 0xE6C896, 0xA67843),
        Flowers = Hexes(0xFF7043, 0xFFCA28, 0xF06292, 0xFFFFFF, 0xFFB74D, 0xE57373),
        Caps = Hexes(0x8D6E63, 0xC9A66B, 0x6D4C41, 0xA1887F, 0xBCAAA4, 0x795548),
        BaseShade = 0.4f,
        FlowerChance = 0.015f,
        MushroomChance = 0f,
        GrassHeight = 0.72f,
    };

    public static readonly Palette Winter = new()
    {
        Kind = SceneKind.Winter,
        Name = "Winter",
        Blades = Hexes(0xE8EEF5, 0xB7C4D2, 0xCBD8E5, 0xD7E2EE, 0xA8B7C6, 0xEEF3F8),
        Flowers = Hexes(0xFFFFFF, 0xE3F2FD, 0xBBDEFB, 0xF8BBD0, 0xFFFFFF, 0xE1F5FE),
        Caps = Hexes(0xD32F2F, 0x8D6E63, 0xFFF8E1, 0xE57373, 0x6D4C41, 0xC9A66B),
        BaseShade = 0.3f,
        FlowerChance = 0.01f,
        MushroomChance = 0.01f,
        GrassHeight = 0.8f,
    };

    public static readonly Palette Autumn = new()
    {
        Kind = SceneKind.Autumn,
        Name = "Autumn",
        Blades = Hexes(0xD96B0C, 0xB54D1E, 0xE89A3C, 0xC23E12, 0xD9A65C, 0x8C2E0F),
        Flowers = Hexes(0xFFEB3B, 0xFFA726, 0xFFFFFF, 0xD7CCC8, 0xFFCC80, 0xEF5350),
        Caps = Hexes(0xD32F2F, 0x8D6E63, 0xC9A66B, 0xFFF8E1, 0xE57373, 0x6D4C41),
        BaseShade = 0.5f,
        FlowerChance = 0.02f,
        MushroomChance = 0.045f,
    };

    public static readonly Palette Ocean = new()
    {
        Kind = SceneKind.Ocean,
        Name = "Ocean",
        Blades = Hexes(0x3FA9A6, 0x2E8C8A, 0x6FC6C2, 0x1F6F75, 0x8FD7CC, 0x257D7B),
        Flowers = Hexes(0xFF8A80, 0xFFAB91, 0xF48FB1, 0xFFFFFF, 0xFFCC80, 0xCE93D8),
        Caps = Hexes(0xFF8A65, 0xF06292, 0xFFB74D, 0xBA68C8, 0x4DD0E1, 0xFFF59D),
        BaseShade = 0.55f,
        FlowerChance = 0.03f,
        MushroomChance = 0.02f,
    };

    public static readonly Palette City = new()
    {
        Kind = SceneKind.City,
        Name = "City",
        // A mown park lawn (Central Park meets Grant Park), a little cooler than the meadow.
        Blades = Hexes(0x2E5A27, 0x3B6E30, 0x4A8139, 0x5A9244, 0x6AA250, 0x7DB262),
        // Tulip beds, as on Park Avenue and Michigan Avenue.
        Flowers = Hexes(0xE8434B, 0xF6C445, 0xF28CB1, 0xFFFFFF, 0x9B6BD3, 0xF07F3C),
        Caps = Hexes(0x8D6E63, 0xC9A66B, 0x6D4C41, 0xA1887F, 0xBCAAA4, 0x795548),
        BaseShade = 0.5f,
        FlowerChance = 0.035f,
        MushroomChance = 0.008f,
        GrassHeight = 0.8f,
    };

    public static readonly Palette[] All = { Meadow, Desert, Winter, Autumn, Ocean, City };

    public static Palette From(SceneKind kind) => All.First(p => p.Kind == kind);

    public static bool TryParse(string? name, out SceneKind kind)
    {
        foreach (var p in All)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || (name == "grass" && p.Kind == SceneKind.Meadow))
            {
                kind = p.Kind;
                return true;
            }
        }
        kind = SceneKind.Meadow;
        return false;
    }
}
