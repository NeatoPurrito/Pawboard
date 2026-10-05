using Color4 = Vortice.Mathematics.Color4;

namespace Pawboard;

// Board colours for light and dark mode. Items always store their light-mode colour; the theme
// maps it to what's shown, so one board looks right in both modes (black ink turns white, etc.).
public sealed class Theme
{
    public required Color4 Background;
    public required Color4 Dots;
    public required Color4 Panel;           // toolbar and grab handles
    public required Color4 PanelBorder;
    public required Color4 PanelShadow;
    public required Color4 Icon;
    public required Color4 Accent;
    // Subtle overlays (hover boxes, eraser circle, dividers) are this colour at low opacity.
    public required Color4 Tint;
    public required Dictionary<uint, uint> Ink;

    public uint Display(uint stored) => Ink.TryGetValue(stored, out var shown) ? shown : stored;

    public Color4 Faint(float alpha) => new(Tint.R, Tint.G, Tint.B, alpha);

    public static readonly Theme Light = new()
    {
        Background = Rgb(0xF8F7F4),
        Dots = Rgb(0xCFCBC2),
        Panel = Rgb(0xFFFFFF),
        PanelBorder = new(0, 0, 0, 0.1f),
        PanelShadow = new(0, 0, 0, 0.06f),
        Icon = new(0, 0, 0, 0.62f),
        Accent = new(0.1f, 0.44f, 0.76f, 1),
        Tint = new(0, 0, 0, 1),
        Ink = new(),
    };

    // Grey charcoal with a hint of blue, and neon ink that glows on it.
    public static readonly Theme Dark = new()
    {
        Background = Rgb(0x15181E),
        Dots = Rgb(0x2A2F38),
        Panel = Rgb(0x1E2229),
        PanelBorder = Rgb(0x323843),
        PanelShadow = new(0, 0, 0, 0.35f),
        Icon = Rgb(0xB4BCC8),
        Accent = Rgb(0x38C8FF),
        Tint = new(1, 1, 1, 1),
        Ink = new()
        {
            [0xFF1E1E1E] = 0xFFEEF3FA,   // black  -> white
            [0xFF1971C2] = 0xFF38C8FF,   // blue   -> neon cyan
            [0xFFE03131] = 0xFFFF4F7B,   // red    -> hot pink-red
            [0xFF2F9E44] = 0xFF3DFFA2,   // green  -> mint
            [0xFFF08C00] = 0xFFFFB23F,   // orange -> amber
        },
    };

    public static Color4 Argb(uint c) =>
        new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);

    static Color4 Rgb(uint c) => Argb(0xFF000000 | c);
}
