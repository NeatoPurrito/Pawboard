using System.Drawing.Imaging;
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.WIC;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// Renders strokes from simulated mouse input to a PNG. The input is sampled at 1000 Hz and
// rounded to whole pixels like a real mouse, so it shows what the smoothing does with real-ish data.
static class RenderTest
{
    const int Width = 1400, Height = 900;

    public static void Run(string outPath)
    {
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
        using var dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
        using var wic = new IWICImagingFactory();
        using var bitmap = wic.CreateBitmap((uint)Width, (uint)Height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var rt = factory.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties());
        using var brush = rt.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        using var label = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 13f);

        rt.BeginDraw();
        rt.Clear(new Color4(0xF8 / 255f, 0xF7 / 255f, 0xF4 / 255f, 1));

        void Draw(List<Vector2> pts, float size, uint color, float smoothing = 2f)
        {
            var s = new Stroke { Color = color, Size = size, Smoothing = smoothing, Points = pts };
            Ink.Finish(factory, s);
            if (s.Geometry == null) return;
            brush.Color = new Color4(((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f, 1);
            rt.FillGeometry(s.Geometry, brush);
            s.Geometry.Dispose();
        }
        void Label(string text, float x, float y)
        {
            brush.Color = new Color4(0.45f, 0.45f, 0.45f, 1);
            rt.DrawText(text, label, new DRect(x, y, 600, 20), brush);
        }

        // Handwriting-like loops at two speeds.
        Label("loops, slow (300 px/s)", 40, 20);
        Draw(Mouse(t => Loops(t, 60, 90), 1.0f, 300), 5, 0xFF1E1E1E);
        Label("loops, fast (1500 px/s)", 40, 150);
        Draw(Mouse(t => Loops(t, 60, 220), 1.0f, 1500), 5, 0xFF1971C2);

        // Sharp corners.
        Label("zigzag (sharp turns)", 40, 290);
        Draw(Mouse(t => Zigzag(t, 60, 330), 1.0f, 800), 5, 0xFFE03131);

        // A wobbly hand (about 10 Hz tremor, 1.5 px): no smoothing vs the pen's light smoothing.
        Label("wobbly hand, no smoothing", 40, 430);
        Draw(Mouse(t => Wavy(t, 60, 475), 1.0f, 250, tremor: 1.5f), 4, 0xFF1E1E1E, smoothing: 0);
        Draw(Mouse(t => SmallLoops(t, 500, 475), 1.0f, 200, tremor: 1.2f), 3, 0xFF1E1E1E, smoothing: 0);
        Label("wobbly hand, light smoothing (what the pen does now)", 40, 540);
        Draw(Mouse(t => Wavy(t, 60, 585), 1.0f, 250, tremor: 1.5f), 4, 0xFF2F9E44);
        Draw(Mouse(t => SmallLoops(t, 500, 585), 1.0f, 200, tremor: 1.2f), 3, 0xFF2F9E44);

        // Each pen size, plus single clicks.
        Label("pen sizes and single clicks", 40, 670);
        float[] sizes = [3, 5, 8, 13, 20];
        for (int i = 0; i < sizes.Length; i++)
        {
            float x = 60 + i * 120;
            Draw(Mouse(t => new Vector2(x + t * 80, 740 + MathF.Sin(t * MathF.PI * 2) * 20), 1.0f, 500), sizes[i], 0xFFF08C00);
            Draw([new Vector2(x + 40, 820)], sizes[i], 0xFF1E1E1E);
        }

        // Spiral.
        Label("spiral", 900, 20);
        Draw(Mouse(t =>
        {
            float a = t * MathF.PI * 8;
            float r = 10 + t * 170;
            return new Vector2(1100 + MathF.Cos(a) * r, 230 + MathF.Sin(a) * r);
        }, 1.0f, 900), 8, 0xFF1971C2);

        // Fake handwriting: "hello" made of loops and humps.
        Label("handwriting-ish", 900, 450);
        Draw(Mouse(t => Hello(t, 900, 560), 1.0f, 600), 4, 0xFF1E1E1E);

        // Eraser: a thin wave cut through in three places, and a thick line with its edge nibbled
        // and a notch carved in. Red outlines show where the eraser went.
        Label("eraser (red = where the eraser went)", 900, 625);
        Vector2 OnLine(float x) => new(x, 690 + MathF.Sin((x - 920) / 420 * MathF.PI * 4) * 30);
        var wave = new Stroke
        {
            Color = 0xFF1971C2, Size = 6, Smoothing = 2,
            Points = Mouse(t => OnLine(920 + t * 420), 1.0f, 300),
        };
        var thick = new Stroke
        {
            Color = 0xFFE03131, Size = 22, Smoothing = 2,
            Points = Mouse(t => new Vector2(930 + t * 400, 770), 1.0f, 300),
        };
        Ink.Finish(factory, wave);
        Ink.Finish(factory, thick);
        var sweeps = new (Stroke S, Vector2 A, Vector2 B, float R)[]
        {
            (wave, OnLine(1010), OnLine(1010), 12),
            (wave, OnLine(1130) + new Vector2(-10, -20), OnLine(1130) + new Vector2(10, 25), 9),
            (wave, OnLine(1250), OnLine(1250), 5),
            (thick, new(960, 750), new(1150, 754), 7),     // grazes the top edge
            (thick, new(1220, 790), new(1220, 768), 6),    // carves a notch from below
        };
        foreach (var (s, a, b, r) in sweeps)
        {
            Ink.Erase(factory, s, a, b, r);
            brush.Color = new Color4(0.85f, 0.15f, 0.15f, 0.7f);
            if (a == b) rt.DrawEllipse(new Ellipse(a, r, r), brush, 1f);
            else
            {
                var n = Vector2.Normalize(new Vector2(-(b - a).Y, (b - a).X)) * r;
                rt.DrawLine(a + n, b + n, brush, 1f);
                rt.DrawLine(a - n, b - n, brush, 1f);
                rt.DrawEllipse(new Ellipse(a, r, r), brush, 1f);
                rt.DrawEllipse(new Ellipse(b, r, r), brush, 1f);
            }
        }
        foreach (var s in new[] { wave, thick })
        {
            brush.Color = new Color4(((s.Color >> 16) & 0xFF) / 255f, ((s.Color >> 8) & 0xFF) / 255f, (s.Color & 0xFF) / 255f, 1);
            rt.FillGeometry(s.Geometry!, brush);
        }
        // Reloading must give the same shape: rebuild the thick line from its saved eraser history, one line lower.
        var reloaded = new Stroke { Color = thick.Color, Size = thick.Size, Smoothing = thick.Smoothing, Points = thick.Points.Select(p => p + new Vector2(0, 45)).ToList() };
        reloaded.Erasures = thick.Erasures.Select(r => new EraseRun { Radius = r.Radius, Points = r.Points.Select(p => p + new Vector2(0, 45)).ToList() }).ToList();
        Ink.Finish(factory, reloaded);
        brush.Color = new Color4(0.88f, 0.19f, 0.19f, 0.55f);
        rt.FillGeometry(reloaded.Geometry!, brush);
        Label("same line rebuilt from its saved eraser history", 1000, 830);
        // Text in the board's handwriting font.
        using (var textInk = new TextInk(dwrite))
        {
            var note = new TextItem { Color = 0xFF1E1E1E, FontSize = 26, Position = new Vector2(40, 840), Text = "Strix's plans: arc reactor v2, fix the suit, call Happy at 5!" };
            textInk.Finish(note);
            rt.Transform = System.Numerics.Matrix3x2.CreateScale(note.Scale) * System.Numerics.Matrix3x2.CreateTranslation(note.Position);
            brush.Color = new Color4(0.12f, 0.12f, 0.12f, 1);
            rt.DrawTextLayout(Vector2.Zero, note.Layout!, brush);
            rt.Transform = System.Numerics.Matrix3x2.Identity;
            brush.Color = new Color4(0.1f, 0.44f, 0.76f, 1);
            var b = note.Bounds;
            rt.DrawRoundedRectangle(new RoundedRectangle(RectangleF.Inflate(b, 6, 6), 6, 6), brush, 1.5f);
            var (top, height) = textInk.Caret(note, 9);
            rt.DrawLine(top, top + new Vector2(0, height), brush, 1.6f);

            // Same kind of note with a set width: lines wrap inside the box.
            var wrapped = new TextItem { Color = 0xFF1971C2, FontSize = 22, Width = 230, Position = new Vector2(520, 820), Text = "Meeting notes: ask about the budget and the new timeline" };
            textInk.Finish(wrapped);
            rt.Transform = System.Numerics.Matrix3x2.CreateScale(wrapped.Scale) * System.Numerics.Matrix3x2.CreateTranslation(wrapped.Position);
            brush.Color = new Color4(0.1f, 0.44f, 0.76f, 1);
            rt.DrawTextLayout(Vector2.Zero, wrapped.Layout!, brush);
            rt.Transform = System.Numerics.Matrix3x2.Identity;
            rt.DrawRoundedRectangle(new RoundedRectangle(RectangleF.Inflate(wrapped.Bounds, 6, 6), 6, 6), brush, 1.5f);
        }

        rt.EndDraw();
        SavePng(bitmap, outPath);
    }

    // Walks a path at `speed` px/s, sampling at 1000 Hz and rounding to the pixel grid like a mouse.
    static List<Vector2> Mouse(Func<float, Vector2> path, float duration, float speed, float tremor = 0, bool dedupe = true)
    {
        // Measure the path length to get the right number of samples.
        float length = 0;
        var prev = path(0);
        for (int i = 1; i <= 2000; i++)
        {
            var p = path(i / 2000f);
            length += Vector2.Distance(p, prev);
            prev = p;
        }
        int samples = Math.Max(2, (int)(length / speed * 1000 * duration));
        var pts = new List<Vector2>();
        for (int i = 0; i <= samples; i++)
        {
            var p = path(i / (float)samples);
            float sec = i / 1000f;
            if (tremor > 0) p += new Vector2(MathF.Sin(sec * MathF.Tau * 9), MathF.Sin(sec * MathF.Tau * 11 + 1)) * tremor;
            p = new Vector2(MathF.Round(p.X), MathF.Round(p.Y));
            if (!dedupe || pts.Count == 0 || pts[^1] != p) pts.Add(p);
        }
        return pts;
    }

    static Vector2 Loops(float t, float x0, float y0)
    {
        float a = t * MathF.PI * 2 * 6;
        return new Vector2(x0 + t * 700 + MathF.Cos(a) * -28, y0 + MathF.Sin(a) * 34);
    }

    static Vector2 Zigzag(float t, float x0, float y0)
    {
        float u = t * 8;
        int seg = Math.Min(7, (int)u);
        float f = u - seg;
        float yA = seg % 2 == 0 ? 0 : 70, yB = seg % 2 == 0 ? 70 : 0;
        return new Vector2(x0 + u * 85, y0 + yA + (yB - yA) * f);
    }

    static Vector2 Wavy(float t, float x0, float y0) =>
        new(x0 + t * 400, y0 + MathF.Sin(t * MathF.PI * 3) * 22);

    // Small, letter-sized loops: checks that smoothing keeps detail at handwriting scale.
    static Vector2 SmallLoops(float t, float x0, float y0)
    {
        float a = t * MathF.PI * 2 * 7;
        return new Vector2(x0 + t * 300 - MathF.Cos(a) * 9, y0 + MathF.Sin(a) * 14);
    }

    static Vector2 Hello(float t, float x0, float y0)
    {
        // A cursive-looking run of tall loops and humps.
        float a = t * MathF.PI * 2 * 7;
        float tall = (int)(t * 7) % 3 == 0 ? 2.2f : 1f;
        float y = MathF.Sin(a) * 26 * (MathF.Sin(a) > 0 ? 1 : tall);
        return new Vector2(x0 + t * 440 - MathF.Cos(a) * 14, y0 + y);
    }

    static unsafe void SavePng(IWICBitmap bitmap, string path)
    {
        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            bitmap.CopyPixels((uint)data.Stride, (uint)(data.Stride * Height), data.Scan0);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
