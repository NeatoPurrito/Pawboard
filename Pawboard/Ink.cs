using System.Numerics;
using Vortice.Direct2D1;

namespace Pawboard;

// Stroke feel settings and turning strokes into Direct2D geometry.
public static class Ink
{
    // Tuned for a mouse: pressure is faked from speed (fast = thinner), like a marker. A highlighter
    // keeps one even width instead, like a real one.
    public static FreehandOptions Options(float size, bool last, bool highlight = false) => new()
    {
        Size = size,
        // Higher looks livelier but makes slow turns (tops of l, h, k) pool into blobs.
        Thinning = highlight ? 0 : 0.32f,
        Smoothing = 0.5f,
        Streamline = 0.4f,
        SimulatePressure = !highlight,
        Last = last,
    };

    // Blends each point with its neighbours along the line (a Gaussian over distance travelled).
    // Unlike a lagging filter it never holds the pen back: the newest point stays on the cursor and
    // both ends stay exactly where they were drawn. Wiggles much shorter than `sigma` flatten out,
    // shapes much longer than it are untouched.
    public static List<Vector2> SmoothPath(List<Vector2> pts, float sigma)
    {
        int n = pts.Count;
        if (n < 3 || sigma <= 0) return pts;

        var along = new float[n];
        for (int i = 1; i < n; i++) along[i] = along[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
        float total = along[n - 1];

        var result = new List<Vector2>(n);
        int lo = 0, hi = 0;
        for (int i = 0; i < n; i++)
        {
            // Narrow the blend near the ends so they stay pinned.
            float sig = MathF.Min(sigma, MathF.Min(along[i], total - along[i]) / 2);
            if (sig < 0.01f) { result.Add(pts[i]); continue; }
            float reach = sig * 3;
            while (along[lo] < along[i] - reach) lo++;
            while (hi + 1 < n && along[hi + 1] <= along[i] + reach) hi++;

            float inv = 1 / (2 * sig * sig);
            var sum = Vector2.Zero;
            float weights = 0;
            // At most 64 neighbours each side: plenty for real strokes, and it keeps very dense or
            // oddly scaled lines (say, from a crafted board file) from taking quadratic time.
            for (int j = Math.Max(lo, i - 64); j <= Math.Min(hi, i + 64); j++)
            {
                float d = along[j] - along[i];
                float w = MathF.Exp(-d * d * inv);
                sum += pts[j] * w;
                weights += w;
            }
            result.Add(sum / weights);
        }
        return result;
    }

    public static ID2D1PathGeometry? BuildGeometry(ID2D1Factory factory, List<Vector2> outline)
    {
        if (outline.Count < 3) return null;
        var geometry = factory.CreatePathGeometry();
        using var sink = geometry.Open();
        // Winding keeps a self-crossing stroke solid instead of punching holes where it overlaps.
        sink.SetFillMode(FillMode.Winding);

        // Quadratic curves through the midpoints of the outline: smooth edges without extra points.
        int n = outline.Count;
        sink.BeginFigure(Mid(outline[n - 1], outline[0]), FigureBegin.Filled);
        for (int i = 0; i < n; i++)
        {
            var p = outline[i];
            var next = outline[(i + 1) % n];
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p, Point2 = Mid(p, next) });
        }
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geometry;
    }

    public static List<Vector2> Outline(Stroke s, bool last)
    {
        // A click without movement: a round dot as wide as the line would be.
        if (s.Points.Count == 1)
        {
            var c = s.Points[0];
            float r = s.Size * 0.6f;
            var dot = new List<Vector2>(24);
            for (int i = 0; i < 24; i++)
            {
                float a = i / 24f * MathF.Tau;
                dot.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
            }
            return dot;
        }
        return Freehand.GetStroke(SmoothPath(s.Points, s.Smoothing), Options(s.Size, last, s.Highlight));
    }

    // Geometry operations turn curves into short lines; this keeps those invisible even at max zoom.
    const float Tolerance = 0.03f;

    public static void Finish(ID2D1Factory factory, Stroke s)
    {
        var outline = Outline(s, last: true);
        s.Geometry?.Dispose();
        s.Geometry = BuildGeometry(factory, outline);
        s.Bounds = BoundsOf(s.Points, s.Size);
        foreach (var run in s.Erasures) CutOut(factory, s, EraserShape(factory, run.Points, run.Radius));
    }

    // Erases along a→b (world space) with a round eraser: cuts that shape out of the stroke and
    // records it, so it's redone on load. Returns false if the stroke wasn't actually touched.
    public static bool Erase(ID2D1Factory factory, Stroke s, Vector2 a, Vector2 b, float radius)
    {
        if (s.Geometry == null || !HitsSegment(s, a, b, radius)) return false;
        var shape = EraserShape(factory, [a, b], radius);
        // Rubbing over a part that's already gone shouldn't grow the saved eraser history.
        if (s.Geometry.CompareWithGeometry(shape, null, Tolerance) == GeometryRelation.Disjoint)
        {
            shape.Dispose();
            return false;
        }
        CutOut(factory, s, shape);

        // Extend the current eraser run if this continues it, otherwise start a new one.
        // Only runs this copy made itself may grow; older ones are shared with the undo history.
        var last = s.Erasures.Count > s.InheritedRuns ? s.Erasures[^1] : null;
        if (last != null && last.Radius == radius && last.Points[^1] == a)
            last.Points.Add(b);
        else
            s.Erasures.Add(new EraseRun { Radius = radius, Points = a == b ? [a] : [a, b] });
        return true;
    }

    // Whether erasing along a→b would remove any visible part of the stroke. Doesn't change it.
    public static bool Touches(ID2D1Factory factory, Stroke s, Vector2 a, Vector2 b, float radius)
    {
        if (s.Geometry == null || !HitsSegment(s, a, b, radius)) return false;
        using var shape = EraserShape(factory, [a, b], radius);
        return s.Geometry.CompareWithGeometry(shape, null, Tolerance) != GeometryRelation.Disjoint;
    }

    // Makes erasing real. The eraser's shape is cut out of a line's drawing, but its points are
    // still in the data, so a saved or shared board would still contain what was rubbed out. This
    // drops every point the eraser covered across the line's full width and splits the line into
    // the pieces that are left. Eraser paths that touch a piece stay with it, so its cut edges look
    // exactly as erased. A line rubbed out completely leaves nothing at all. Pieces too small to
    // see are dropped. Returns the line itself if nothing needed removing.
    public static List<Stroke> Bake(ID2D1Factory factory, Stroke s)
    {
        if (s.Erasures.Count == 0) return [s];
        var pts = s.Points;
        float half = s.Size / 2;
        var gone = new bool[pts.Count];
        bool any = false;
        // Only eraser paths wide enough to cover the line's full width can remove points; their
        // bounding boxes keep the exact distance checks to the points actually near them.
        var covering = s.Erasures
            .Where(r => r.Radius > half)
            .Select(r => (Run: r, Margin: r.Radius - half, Box: RectangleF.Inflate(BoundsOf(r.Points, 0), r.Radius - half, r.Radius - half)))
            .ToList();
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            foreach (var (run, margin, box) in covering)
            {
                if (!box.Contains(p.X, p.Y) || DistanceSquaredToPath(p, run.Points) > margin * margin) continue;
                gone[i] = any = true;
                break;
            }
        }
        if (!any) return [s];

        var pieces = new List<Stroke>();
        int start = -1;
        for (int i = 0; i <= pts.Count; i++)
        {
            bool keep = i < pts.Count && !gone[i];
            if (keep && start < 0) start = i;
            if (keep || start < 0) continue;
            AddPiece(start, i);
            start = -1;
        }
        return pieces;

        void AddPiece(int from, int to)
        {
            if (to - from < 2) return;   // a lone point would draw a dot that was never there
            var piece = new Stroke { Color = s.Color, Size = s.Size, Smoothing = s.Smoothing, Highlight = s.Highlight, Points = pts.GetRange(from, to - from) };
            var bounds = BoundsOf(piece.Points, s.Size);
            foreach (var run in s.Erasures)
                if (RectangleF.Inflate(BoundsOf(run.Points, 0), run.Radius, run.Radius).IntersectsWith(bounds))
                    piece.Erasures.Add(run);
            piece.InheritedRuns = piece.Erasures.Count;   // shared with the original: never extended
            Finish(factory, piece);
            float crumb = s.Size * 0.35f;
            if (Area(piece) < crumb * crumb) { piece.Release(); return; }
            pieces.Add(piece);
        }
    }

    static float DistanceSquaredToPath(Vector2 p, List<Vector2> path)
    {
        if (path.Count == 1) return Vector2.DistanceSquared(p, path[0]);
        float best = float.MaxValue;
        for (int i = 1; i < path.Count; i++) best = MathF.Min(best, SegmentDistanceSquared(p, path[i - 1], path[i]));
        return best;
    }

    // What's left of the stroke, as an area in world units².
    public static float Area(Stroke s) => s.Geometry?.ComputeArea(Tolerance) ?? 0;

    static void CutOut(ID2D1Factory factory, Stroke s, ID2D1Geometry shape)
    {
        var result = factory.CreatePathGeometry();
        using (var sink = result.Open())
        {
            s.Geometry!.CombineWithGeometry(shape, CombineMode.Exclude, Tolerance, sink);
            sink.Close();
        }
        s.Geometry.Dispose();
        s.Geometry = result;
        shape.Dispose();
    }

    // The area a round eraser covers while dragged along a path: the path widened by its diameter.
    static ID2D1Geometry EraserShape(ID2D1Factory factory, List<Vector2> path, float radius)
    {
        if (path.Count == 1 || path.All(p => p == path[0]))
            return factory.CreateEllipseGeometry(new Ellipse(path[0], radius, radius));

        using var line = factory.CreatePathGeometry();
        using (var sink = line.Open())
        {
            sink.BeginFigure(path[0], FigureBegin.Hollow);
            for (int i = 1; i < path.Count; i++) sink.AddLine(path[i]);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        var props = new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round };
        using var style = factory.CreateStrokeStyle(props);
        var shape = factory.CreatePathGeometry();
        using (var sink = shape.Open())
        {
            sink.SetFillMode(FillMode.Winding);
            line.Widen(radius * 2, style, Tolerance, sink);
            sink.Close();
        }
        return shape;
    }

    // True if a round eraser dragged from a to b touches the stroke's centre line (thickened by its size).
    static bool HitsSegment(Stroke s, Vector2 a, Vector2 b, float radius)
    {
        var bounds = s.Bounds;
        if (MathF.Max(a.X, b.X) < bounds.Left - radius || MathF.Min(a.X, b.X) > bounds.Right + radius ||
            MathF.Max(a.Y, b.Y) < bounds.Top - radius || MathF.Min(a.Y, b.Y) > bounds.Bottom + radius) return false;

        float reach = radius + s.Size / 2;
        float reach2 = reach * reach;
        var pts = s.Points;
        if (pts.Count == 1) return SegmentDistanceSquared(pts[0], a, b) <= reach2;
        for (int i = 1; i < pts.Count; i++)
            if (SegmentsDistanceSquared(a, b, pts[i - 1], pts[i]) <= reach2) return true;
        return false;
    }

    static float SegmentsDistanceSquared(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (SegmentsCross(a, b, c, d)) return 0;
        return MathF.Min(
            MathF.Min(SegmentDistanceSquared(a, c, d), SegmentDistanceSquared(b, c, d)),
            MathF.Min(SegmentDistanceSquared(c, a, b), SegmentDistanceSquared(d, a, b)));
    }

    static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 o, Vector2 p, Vector2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        float d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    public static RectangleF BoundsOf(List<Vector2> points, float size)
    {
        if (points.Count == 0) return RectangleF.Empty;
        var min = points[0];
        var max = points[0];
        foreach (var p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        float pad = size;
        return RectangleF.FromLTRB(min.X - pad, min.Y - pad, max.X + pad, max.Y + pad);
    }

    // True if a circle at `center` with `radius` touches the stroke's centre line (thickened by its size).
    public static bool Hits(Stroke s, Vector2 center, float radius)
    {
        var b = s.Bounds;
        if (center.X < b.Left - radius || center.X > b.Right + radius ||
            center.Y < b.Top - radius || center.Y > b.Bottom + radius) return false;

        float reach = radius + s.Size / 2;
        float reach2 = reach * reach;
        var pts = s.Points;
        if (pts.Count == 1) return Vector2.DistanceSquared(pts[0], center) <= reach2;
        for (int i = 1; i < pts.Count; i++)
            if (SegmentDistanceSquared(center, pts[i - 1], pts[i]) <= reach2) return true;
        return false;
    }

    static float SegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 == 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    static Vector2 Mid(Vector2 a, Vector2 b) => (a + b) * 0.5f;
}
