using System.Numerics;
using SharpGen.Runtime;
using Vortice.DirectWrite;

namespace Pawboard;

// Laying out, measuring and hit-testing text items. Every text is laid out once at a fixed
// reference size and scaled to its real size when drawn, so one text format serves all sizes.
public sealed class TextInk : IDisposable
{
    public const float ReferenceSize = 32;
    // Comes with every Windows: looks handwritten but stays easy to read.
    public const string FontFamily = "Segoe Print";

    readonly IDWriteFactory dwrite;
    readonly IDWriteTextFormat format;

    public TextInk(IDWriteFactory dwrite)
    {
        this.dwrite = dwrite;
        format = dwrite.CreateTextFormat(FontFamily, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, ReferenceSize);
        format.WordWrapping = WordWrapping.NoWrap;
    }

    public void Finish(TextItem t)
    {
        t.Layout?.Dispose();
        bool wraps = t.Width > 0;
        t.Layout = dwrite.CreateTextLayout(t.Text, format, wraps ? t.Width / t.Scale : 100_000, 100_000);
        // Break between words, and inside a word only if it's longer than the whole line.
        if (wraps) t.Layout.WordWrapping = WordWrapping.EmergencyBreak;
        var m = t.Layout.Metrics;
        // An empty text still gets one line's height and a little width, so it has a box to click.
        float w = wraps ? t.Width / t.Scale : MathF.Max(m.WidthIncludingTrailingWhitespace, ReferenceSize * 0.4f);
        t.Bounds = new RectangleF(t.Position.X, t.Position.Y, w * t.Scale, m.Height * t.Scale);
    }

    // Caret position (top) and height in world space for a character index.
    public (Vector2 Top, float Height) Caret(TextItem t, int index)
    {
        t.Layout!.HitTestTextPosition((uint)index, false, out float x, out float y, out var hit);
        return (t.Position + new Vector2(x, y) * t.Scale, hit.Height * t.Scale);
    }

    // Character index closest to a world-space point.
    public int IndexAt(TextItem t, Vector2 world)
    {
        var local = (world - t.Position) / t.Scale;
        t.Layout!.HitTestPoint(local.X, local.Y, out RawBool trailing, out _, out var hit);
        return Math.Clamp((int)hit.TextPosition + (trailing ? (int)hit.Length : 0), 0, t.Text.Length);
    }

    // World-space rectangles covering the characters [start, end), one per line piece.
    public List<RectangleF> SelectionRects(TextItem t, int start, int end)
    {
        var rects = new List<RectangleF>();
        if (end <= start || t.Layout == null) return rects;
        foreach (var m in t.Layout.HitTestTextRange((uint)start, (uint)(end - start), 0, 0))
        {
            // A selected line break has no width; show a sliver so you can see it's included.
            float w = MathF.Max(m.Width, ReferenceSize * 0.2f);
            rects.Add(new RectangleF(t.Position.X + m.Left * t.Scale, t.Position.Y + m.Top * t.Scale, w * t.Scale, m.Height * t.Scale));
        }
        return rects;
    }

    public void Dispose() => format.Dispose();
}
