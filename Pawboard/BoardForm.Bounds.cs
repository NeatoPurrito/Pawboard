using System.Numerics;

namespace Pawboard;

// The board is your screen: the start view at 100% is as far out as it goes. You can zoom in to
// write small and move around in there, but never past the board's edges, and at 100% nothing
// moves at all. That keeps notes exactly where you put them on your desktop.
public sealed partial class BoardForm
{
    // The whole board, in world units: the start view (zoom 1, world origin at the top-left).
    RectangleF BoardBounds => new(0, 0, ClientDips.X, ClientDips.Y);

    // Lock zoom (in the menu): always the start view. The wheel and middle button are then left
    // to Windows; read from the hook thread too.
    volatile bool zoomLocked;

    // Keeps the view inside the board.
    void ClampView()
    {
        if (zoomLocked)
        {
            zoom = targetZoom = 1;
            zooming = coasting = false;
        }
        if (zoom < MinZoom) zoom = MinZoom;
        if (targetZoom < MinZoom) targetZoom = MinZoom;
        var b = BoardBounds;
        var s = ClientDips;
        offset = new Vector2(ClampAxis(offset.X, b.Left, b.Right, s.X), ClampAxis(offset.Y, b.Top, b.Bottom, s.Y));
    }

    float ClampAxis(float off, float low, float high, float screen)
    {
        // What's visible is [-off / zoom, (screen - off) / zoom]; keep it within [low, high].
        // At 100% the board and the screen are the same size, so this pins the view in place.
        return Math.Clamp(off, screen - high * zoom, -low * zoom);
    }

    // Back to the start view at 100%.
    void GoHome()
    {
        zooming = coasting = false;
        zoom = targetZoom = 1;
        offset = Vector2.Zero;
        cacheDirty = true;
        Invalidate();
    }

    void ToggleZoomLock()
    {
        zoomLocked = !zoomLocked;
        if (zoomLocked) GoHome();
        SaveSettings();
        Invalidate();
    }
}
