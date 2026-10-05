using System.Numerics;
using Vortice.Direct2D1;

namespace Pawboard;

// The board has edges, so you can't get lost in endless empty space. It's as big as what you
// see fully zoomed out from the start view, and grows to include anything drawn beyond that,
// always keeping the screen's shape. So fully zoomed out, the board fills the screen exactly and
// its edges are never visible; zoomed in, you can't move past them.
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

        // Grow the shorter side so the board has exactly the screen's shape.
        float aspect = home.X / home.Y;
        var mid = new Vector2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        if (bounds.Width / bounds.Height > aspect)
            bounds = new RectangleF(bounds.X, mid.Y - bounds.Width / aspect / 2, bounds.Width, bounds.Width / aspect);
        else
            bounds = new RectangleF(mid.X - bounds.Height * aspect / 2, bounds.Y, bounds.Height * aspect, bounds.Height);
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

    // Keeps the view inside the board. At the lowest zoom the board and the screen are the same
    // size; the centring only catches rounding there.
    void ClampView()
    {
        var b = BoardBounds;
        var s = ClientDips;
        // Never further out than the whole board (a saved view or an undo can ask for that).
        float lowest = LowestZoom;
        if (zoom < lowest) zoom = lowest;
        if (targetZoom < lowest) targetZoom = lowest;
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
}
