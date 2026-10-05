using System.Numerics;
using Vortice.Direct2D1;

namespace Pawboard;

// The board has edges, so you can't get lost in endless empty space. It's as big as what you
// see fully zoomed out from the start view, and grows to include anything drawn beyond that.
// The view can't zoom out past the whole board or move beyond its edges.
public sealed partial class BoardForm
{
    RectangleF boardBounds;
    bool boundsDirty = true;

    RectangleF BoardBounds
    {
        get
        {
            if (boundsDirty)
            {
                boardBounds = ComputeBoardBounds();
                boundsDirty = false;
            }
            return boardBounds;
        }
    }

    RectangleF ComputeBoardBounds()
    {
        // The start view is zoom 1 with the world origin at the top-left of the screen.
        var home = ClientDips;
        var center = home / 2;
        var size = home / MinZoom;
        var bounds = new RectangleF(center.X - size.X / 2, center.Y - size.Y / 2, size.X, size.Y);
        foreach (var item in board.Items) bounds = RectangleF.Union(bounds, item.Bounds);
        return bounds;
    }

    // The lowest zoom: the whole board just fits on screen.
    float LowestZoom
    {
        get
        {
            var b = BoardBounds;
            var s = ClientDips;
            return Math.Min(MaxZoom, Math.Min(s.X / b.Width, s.Y / b.Height));
        }
    }

    // Keeps the view inside the board. Where the board is smaller than the screen (fully zoomed
    // out), it's centred instead.
    void ClampView()
    {
        var b = BoardBounds;
        var s = ClientDips;
        offset = new Vector2(ClampAxis(offset.X, b.Left, b.Right, s.X), ClampAxis(offset.Y, b.Top, b.Bottom, s.Y));
    }

    float ClampAxis(float off, float low, float high, float screen)
    {
        float length = (high - low) * zoom;
        if (length <= screen) return (screen - length) / 2 - low * zoom;
        // What's visible is [-off / zoom, (screen - off) / zoom]; keep it within [low, high].
        return Math.Clamp(off, screen - high * zoom, -low * zoom);
    }

    // Back to the start view at 100%.
    void GoHome()
    {
        zooming = coasting = false;
        zoom = targetZoom = 1;
        offset = Vector2.Zero;
        ClampView();
        cacheDirty = true;
        Invalidate();
    }

    // Shades whatever lies beyond the board's edges, so you can see where it ends.
    void DrawBoardEdge(ID2D1RenderTarget c)
    {
        var b = BoardBounds;
        var tl = WorldToScreen(new Vector2(b.Left, b.Top));
        var br = WorldToScreen(new Vector2(b.Right, b.Bottom));
        var s = ClientDips;
        if (tl.X <= 0 && tl.Y <= 0 && br.X >= s.X && br.Y >= s.Y) return;   // the board fills the screen

        // Slightly darker beyond the edge, in both light and dark mode.
        brush!.Color = new Vortice.Mathematics.Color4(0, 0, 0, dark ? 0.35f : 0.06f);
        if (tl.Y > 0) c.FillRectangle(new Vortice.RawRectF(0, 0, s.X, tl.Y), brush);
        if (br.Y < s.Y) c.FillRectangle(new Vortice.RawRectF(0, br.Y, s.X, s.Y), brush);
        if (tl.X > 0) c.FillRectangle(new Vortice.RawRectF(0, MathF.Max(0, tl.Y), tl.X, MathF.Min(s.Y, br.Y)), brush);
        if (br.X < s.X) c.FillRectangle(new Vortice.RawRectF(br.X, MathF.Max(0, tl.Y), s.X, MathF.Min(s.Y, br.Y)), brush);
        brush.Color = Colors.Faint(dark ? 0.18f : 0.12f);
        c.DrawRectangle(new Vortice.RawRectF(tl.X, tl.Y, br.X, br.Y), brush, 1f);
    }
}
