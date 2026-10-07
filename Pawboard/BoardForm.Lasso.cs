using System.Numerics;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// The lasso: draw a loop around ink and text to pick them up, then drag to move, drag a corner
// to resize, the bin button to delete, or a colour on the toolbar to recolour. Each of those is
// one undo step. Strokes carry their own eraser marks, so those move and scale along with them.
public sealed partial class BoardForm
{
    List<Item>? picked;                               // board items the lasso picked up
    readonly HashSet<Item> pickedHidden = new();      // left out of the cache while being dragged
    readonly List<Vector2> lassoPath = new();         // world points of the loop being drawn
    Grip pickedGrip;                                  // the corner being dragged, or Move for the body
    Vector2 pickedStart;                              // world point where the drag started
    Vector2 pickedOffset;                             // world units, while moving
    float pickedScale = 1;                            // while resizing
    Vector2 pickedAnchor;                             // world point that stays put while resizing
    ID2D1StrokeStyle? dashStyle;
    // Screen area of the picked box, its handles and bin button (client DIPs), updated each frame.
    // Clicks there are the board's even over a desktop icon; read by the mouse hook too.
    RectangleF pickedHitRect;

    // How the picked items are shown mid-drag (world to world).
    Matrix3x2 PickedTransform =>
        Matrix3x2.CreateTranslation(-pickedAnchor) * Matrix3x2.CreateScale(pickedScale) *
        Matrix3x2.CreateTranslation(pickedAnchor + pickedOffset);

    bool DraggingPicked => mode == Mode.MovePicked;

    void CreateLassoResources()
    {
        dashStyle = factory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round, DashStyle = DashStyle.Custom,
        }, [2.5f, 2.5f]);
    }

    void ClearPicked()
    {
        if (picked == null) return;
        picked = null;
        Invalidate();
    }

    // World bounds of everything picked, as it is on the board (not mid-drag).
    RectangleF PickedBounds()
    {
        var b = picked![0].Bounds;
        foreach (var item in picked) b = RectangleF.Union(b, item.Bounds);
        return b;
    }

    // The dashed box around the picked items on screen, following a drag in progress.
    RectangleF PickedScreenBox()
    {
        var b = PickedBounds();
        var m = DraggingPicked ? PickedTransform : Matrix3x2.Identity;
        var a = WorldToScreen(Vector2.Transform(new Vector2(b.Left, b.Top), m));
        var c = WorldToScreen(Vector2.Transform(new Vector2(b.Right, b.Bottom), m));
        return RectangleF.Inflate(RectangleF.FromLTRB(a.X, a.Y, c.X, c.Y), 6, 6);
    }

    (Grip, Vector2)[] PickedCorners(RectangleF box) =>
    [
        (Grip.TopLeft, new(box.Left, box.Top)), (Grip.TopRight, new(box.Right, box.Top)),
        (Grip.BottomLeft, new(box.Left, box.Bottom)), (Grip.BottomRight, new(box.Right, box.Bottom)),
    ];

    // The bin button: under the box, or above it when the toolbar would be in the way.
    Vector2 TrashCenter(RectangleF box)
    {
        float below = box.Bottom + 24;
        bool room = below + 14 < ToolbarArea.Bottom - 80;
        return new Vector2((box.Left + box.Right) / 2, room ? below : box.Top - 24);
    }

    void LassoMouseDown()
    {
        if (picked != null)
        {
            var box = PickedScreenBox();
            if (Vector2.Distance(cursor, TrashCenter(box)) <= 15)
            {
                ReplacePicked(_ => null);
                return;
            }
            foreach (var (grip, p) in PickedCorners(box))
                if (Vector2.Distance(p, cursor) <= 10) { BeginPickedDrag(grip); return; }
            if (box.Contains(cursor.X, cursor.Y)) { BeginPickedDrag(Grip.Move); return; }
            ClearPicked();   // clicking elsewhere lets go, and starts a new loop right away
        }
        mode = Mode.Lasso;
        modeButton = MouseButtons.Left;
        lassoPath.Clear();
        lassoPath.Add(ScreenToWorld(cursor));
    }

    void ExtendLasso()
    {
        // A point every couple of pixels is plenty for the shape, and keeps picking quick.
        if (Vector2.Distance(WorldToScreen(lassoPath[^1]), cursor) < 3) return;
        lassoPath.Add(ScreenToWorld(cursor));
        Invalidate();
    }

    // A stroke is picked when most of its points are inside the loop, a text when its middle is.
    // So a line the loop only clipped stays put, and nothing gets cut in half.
    void FinishLasso()
    {
        var loop = lassoPath.ToArray();
        lassoPath.Clear();
        Invalidate();
        if (loop.Length < 3) return;
        var area = loop.Aggregate(new RectangleF(loop[0].X, loop[0].Y, 0, 0), (r, p) => RectangleF.Union(r, new RectangleF(p.X, p.Y, 0, 0)));
        var found = new List<Item>();
        foreach (var item in board.Items)
        {
            if (!item.Bounds.IntersectsWith(area)) continue;
            bool inside = item switch
            {
                Stroke s => s.Points.Count(p => InsideLoop(loop, p)) * 2 > s.Points.Count,
                TextItem t => InsideLoop(loop, new Vector2(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2)),
                _ => false,
            };
            if (inside) found.Add(item);
        }
        picked = found.Count > 0 ? found : null;
    }

    static bool InsideLoop(Vector2[] loop, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    void BeginPickedDrag(Grip grip)
    {
        SettleZoom();
        coasting = false;
        mode = Mode.MovePicked;
        modeButton = MouseButtons.Left;
        pickedGrip = grip;
        pickedStart = ScreenToWorld(cursor);
        pickedOffset = Vector2.Zero;
        pickedScale = 1;
        var b = PickedBounds();
        // Resizing keeps the opposite corner where it is.
        pickedAnchor = grip switch
        {
            Grip.TopLeft => new(b.Right, b.Bottom),
            Grip.TopRight => new(b.Left, b.Bottom),
            Grip.BottomLeft => new(b.Right, b.Top),
            _ => new(b.Left, b.Top),
        };
        // Draw them on top while they move, instead of from the cache.
        pickedHidden.Clear();
        pickedHidden.UnionWith(picked!);
        contentVersion++;
        cacheDirty = true;
        Invalidate();
    }

    void DragPicked()
    {
        var w = ScreenToWorld(cursor);
        var b = PickedBounds();
        var board = BoardBounds;
        if (pickedGrip == Grip.Move)
        {
            // Keep it on the board (where it fits), or it could end up somewhere you can't scroll to.
            var o = w - pickedStart;
            float minX = MathF.Min(board.Left - b.Left, 0), maxX = MathF.Max(board.Right - b.Right, 0);
            float minY = MathF.Min(board.Top - b.Top, 0), maxY = MathF.Max(board.Bottom - b.Bottom, 0);
            pickedOffset = new Vector2(Math.Clamp(o.X, minX, maxX), Math.Clamp(o.Y, minY, maxY));
        }
        else
        {
            var corner = pickedAnchor + (new Vector2(b.Left + b.Right, b.Top + b.Bottom) - 2 * pickedAnchor);
            var diagonal = corner - pickedAnchor;
            if (diagonal.LengthSquared() < 1e-6f) return;
            float ratio = Vector2.Dot(w - pickedAnchor, diagonal) / diagonal.LengthSquared();
            if (!float.IsFinite(ratio)) return;
            // At least a little box on screen, and no bigger than the board.
            float side = MathF.Max(b.Width, b.Height);
            float smallest = 16 / (side * zoom);
            float largest = MathF.Max(1, MathF.Min(board.Width / b.Width, board.Height / b.Height));
            pickedScale = Math.Clamp(ratio, MathF.Min(smallest, 1), largest);
        }
        Invalidate();
    }

    void FinishPickedDrag()
    {
        pickedHidden.Clear();
        contentVersion++;
        cacheDirty = true;
        if (pickedOffset == Vector2.Zero && pickedScale == 1) return;   // just a click on the box
        var m = PickedTransform;
        float scale = pickedScale;
        ReplacePicked(item => Transformed(item, m, scale, null));
        pickedOffset = Vector2.Zero;
        pickedScale = 1;
    }

    void RecolorPicked(uint color) => ReplacePicked(item => Transformed(item, Matrix3x2.Identity, 1, color));

    // Swaps every picked item for what `make` returns (null removes it) as one undo step, and
    // keeps the new versions picked.
    void ReplacePicked(Func<Item, Item?> make)
    {
        if (picked == null) return;
        var map = picked.ToDictionary(item => item, make);
        var after = new List<Item>(board.Items.Count);
        foreach (var item in board.Items)
        {
            if (!map.TryGetValue(item, out var replacement)) after.Add(item);
            else if (replacement != null) after.Add(replacement);
        }
        var stillPicked = picked.Select(item => map[item]).OfType<Item>().ToList();
        board.Commit(after);   // (lets go of the old picks, see board.Changed)
        picked = stillPicked.Count > 0 ? stillPicked : null;
        cacheDirty = true;
        Invalidate();
    }

    // A moved / resized / recoloured copy. The original stays as it is, for undo.
    Item Transformed(Item item, Matrix3x2 m, float scale, uint? color)
    {
        switch (item)
        {
            case Stroke s:
                var stroke = new Stroke
                {
                    Color = color ?? s.Color,
                    Size = s.Size * scale,
                    Smoothing = s.Smoothing * scale,
                    Points = s.Points.Select(p => Vector2.Transform(p, m)).ToList(),
                    // The eraser marks go along, so the same parts stay erased.
                    Erasures = s.Erasures.Select(r => new EraseRun
                    {
                        Radius = r.Radius * scale,
                        Points = r.Points.Select(p => Vector2.Transform(p, m)).ToList(),
                    }).ToList(),
                };
                Ink.Finish(factory, stroke);
                return stroke;
            case TextItem t:
                var text = t.Copy();
                text.Color = color ?? t.Color;
                text.Position = Vector2.Transform(t.Position, m);
                text.FontSize *= scale;
                text.Width *= scale;
                textInk.Finish(text);
                return text;
            default:
                return item;
        }
    }

    void DrawLasso(ID2D1RenderTarget r)
    {
        if (mode == Mode.Lasso && lassoPath.Count > 1)
        {
            using var loop = factory.CreatePathGeometry();
            using (var sink = loop.Open())
            {
                sink.BeginFigure(WorldToScreen(lassoPath[0]), FigureBegin.Filled);
                for (int i = 1; i < lassoPath.Count; i++) sink.AddLine(WorldToScreen(lassoPath[i]));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            brush!.Color = WithAlpha(Colors.Accent, 0.07f);
            r.FillGeometry(loop, brush);
            brush.Color = Colors.Accent;
            r.DrawGeometry(loop, brush, 1.5f, dashStyle);
        }
        pickedHitRect = RectangleF.Empty;
        if (picked == null) return;

        if (DraggingPicked)
            foreach (var item in picked) DrawItem(r, item, PickedTransform);

        var box = PickedScreenBox();
        var bin = TrashCenter(box);
        pickedHitRect = RectangleF.Union(RectangleF.Inflate(box, 12, 12), new RectangleF(bin.X - 16, bin.Y - 16, 32, 32));
        brush!.Color = Colors.Accent;
        r.DrawRectangle(ToDRect(box), brush, 1.5f, dashStyle);
        foreach (var (_, p) in PickedCorners(box))
        {
            var handle = new RoundedRectangle(new RectangleF(p.X - 4.5f, p.Y - 4.5f, 9, 9), 2.5f, 2.5f);
            brush.Color = Colors.Panel;
            r.FillRoundedRectangle(handle, brush);
            brush.Color = Colors.Accent;
            r.DrawRoundedRectangle(handle, brush, 1.2f);
        }
        if (DraggingPicked) return;

        // The bin button.
        var c = TrashCenter(box);
        brush.Color = Colors.PanelShadow;
        r.FillEllipse(new Ellipse(c + new Vector2(0, 1.5f), 15, 15), brush);
        brush.Color = Colors.Panel;
        r.FillEllipse(new Ellipse(c, 15, 15), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawEllipse(new Ellipse(c, 15, 15), brush, 1f);
        brush.Color = Colors.Icon;
        r.DrawText(TrashIcon, iconFont, new DRect(c.X - 15, c.Y - 15, 30, 30), brush);
    }

    // The toolbar's lasso icon: a dashed loop with a little tail, drawn rather than taken from the
    // icon font (which has no real lasso).
    void DrawLassoIcon(ID2D1RenderTarget r, RectangleF slot, Color4 color)
    {
        var c = new Vector2(slot.X + slot.Width / 2, slot.Y + slot.Height / 2 - 2);
        brush!.Color = color;
        r.DrawEllipse(new Ellipse(c, 8, 5.5f), brush, 1.5f, dashStyle);
        // The tail: where the rope leaves the loop.
        r.DrawLine(c + new Vector2(-4.5f, 4.5f), c + new Vector2(-5.5f, 8.5f), brush, 1.5f);
        r.DrawLine(c + new Vector2(-5.5f, 8.5f), c + new Vector2(-2.5f, 10.5f), brush, 1.5f);
    }
}
