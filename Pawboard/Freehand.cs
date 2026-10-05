using System.Numerics;

namespace Pawboard;

// C# port of perfect-freehand by Steve Ruiz (MIT): https://github.com/steveruizok/perfect-freehand
// Turns a list of input points into the outline polygon of a variable-width stroke.
// Kept close to the original so its tuning advice still applies.
public sealed class FreehandOptions
{
    public float Size = 6;
    public float Thinning = 0.5f;
    public float Smoothing = 0.5f;
    public float Streamline = 0.5f;
    public bool SimulatePressure = true;
    public bool CapStart = true;
    public bool CapEnd = true;
    // True once the pointer is released: the last point snaps to the real input instead of trailing it.
    public bool Last;
}

public static class Freehand
{
    const float RateOfPressureChange = 0.275f;
    const float FixedPi = MathF.PI + 0.0001f;
    const float DefaultPressure = 0.5f;

    struct StrokePoint
    {
        public Vector2 Point;
        public float Pressure;
        public Vector2 Vector;
        public float Distance;
        public float RunningLength;
    }

    public static List<Vector2> GetStroke(IReadOnlyList<Vector2> input, FreehandOptions o)
        => GetOutline(GetStrokePoints(input, o), o);

    static List<StrokePoint> GetStrokePoints(IReadOnlyList<Vector2> input, FreehandOptions o)
    {
        var result = new List<StrokePoint>(input.Count);
        if (input.Count == 0) return result;

        float t = 0.15f + (1 - o.Streamline) * 0.85f;
        var pts = new List<Vector2>(input);

        // Two points: fill in a few between them so the stroke has enough points to shape.
        if (pts.Count == 2)
        {
            var last = pts[1];
            pts.RemoveAt(1);
            for (int i = 1; i < 5; i++) pts.Add(Vector2.Lerp(pts[0], last, i / 4f));
        }
        if (pts.Count == 1) pts.Add(pts[0] + Vector2.One);

        var prev = new StrokePoint { Point = pts[0], Pressure = 0.25f, Vector = Vector2.One };
        result.Add(prev);
        bool reachedMinLength = false;
        float runningLength = 0;
        int max = pts.Count - 1;

        for (int i = 1; i < pts.Count; i++)
        {
            var point = o.Last && i == max ? pts[i] : Vector2.Lerp(prev.Point, pts[i], t);
            if (point == prev.Point) continue;
            float distance = Vector2.Distance(point, prev.Point);
            runningLength += distance;
            if (i < max && !reachedMinLength)
            {
                if (runningLength < o.Size) continue;
                reachedMinLength = true;
            }
            prev = new StrokePoint
            {
                Point = point,
                Pressure = DefaultPressure,
                Vector = Uni(prev.Point - point),
                Distance = distance,
                RunningLength = runningLength,
            };
            result.Add(prev);
        }

        var first = result[0];
        first.Vector = result.Count > 1 ? result[1].Vector : Vector2.Zero;
        result[0] = first;
        return result;
    }

    static float Radius(float size, float thinning, float pressure)
        => size * (0.5f - thinning * (0.5f - pressure));

    static float SimulatedPressure(float prevPressure, float distance, float size)
    {
        float sp = MathF.Min(1, distance / size);
        float rp = MathF.Min(1, 1 - sp);
        return MathF.Min(1, prevPressure + (rp - prevPressure) * (sp * RateOfPressureChange));
    }

    static List<Vector2> GetOutline(List<StrokePoint> points, FreehandOptions o)
    {
        var outline = new List<Vector2>();
        if (points.Count == 0 || o.Size <= 0) return outline;

        float size = o.Size;
        float totalLength = points[^1].RunningLength;
        float minDistance = MathF.Pow(size * o.Smoothing, 2);
        var left = new List<Vector2>(points.Count + 16);
        var right = new List<Vector2>(points.Count + 16);

        float prevPressure = points[0].Pressure;
        for (int i = 0; i < Math.Min(10, points.Count); i++)
        {
            float p = points[i].Pressure;
            if (o.SimulatePressure) p = SimulatedPressure(prevPressure, points[i].Distance, size);
            prevPressure = (prevPressure + p) / 2;
        }

        float radius = Radius(size, o.Thinning, points[^1].Pressure);
        float? firstRadius = null;
        var prevVector = points[0].Vector;
        Vector2 pl = points[0].Point, pr = pl, tl = pl, tr = pr;
        bool prevWasSharpCorner = false;

        for (int i = 0; i < points.Count; i++)
        {
            var sp = points[i];
            float pressure = sp.Pressure;

            // Skip points too close to the end; the end cap covers them and they cause wobble.
            if (i < points.Count - 1 && totalLength - sp.RunningLength < 3) continue;

            if (o.Thinning != 0)
            {
                if (o.SimulatePressure) pressure = SimulatedPressure(prevPressure, sp.Distance, size);
                radius = Radius(size, o.Thinning, pressure);
            }
            else radius = size / 2;
            firstRadius ??= radius;
            radius = MathF.Max(0.01f, radius);

            var nextVector = (i < points.Count - 1 ? points[i + 1] : sp).Vector;
            float nextDpr = i < points.Count - 1 ? Vector2.Dot(sp.Vector, nextVector) : 1f;
            float prevDpr = Vector2.Dot(sp.Vector, prevVector);
            bool isSharpCorner = prevDpr < 0 && !prevWasSharpCorner;
            bool isNextSharpCorner = nextDpr < 0;

            // Sharp turn: round the corner with a half circle instead of a spike.
            if (isSharpCorner || isNextSharpCorner)
            {
                var off = Per(prevVector) * radius;
                for (float t = 0; t <= 1; t += 1 / 13f)
                {
                    tl = RotAround(sp.Point - off, sp.Point, FixedPi * t);
                    left.Add(tl);
                    tr = RotAround(sp.Point + off, sp.Point, FixedPi * -t);
                    right.Add(tr);
                }
                pl = tl; pr = tr;
                if (isNextSharpCorner) prevWasSharpCorner = true;
                continue;
            }
            prevWasSharpCorner = false;

            if (i == points.Count - 1)
            {
                var endOff = Per(sp.Vector) * radius;
                left.Add(sp.Point - endOff);
                right.Add(sp.Point + endOff);
                continue;
            }

            var offset = Per(Vector2.Lerp(nextVector, sp.Vector, nextDpr)) * radius;
            tl = sp.Point - offset;
            if (i <= 1 || Vector2.DistanceSquared(pl, tl) > minDistance) { left.Add(tl); pl = tl; }
            tr = sp.Point + offset;
            if (i <= 1 || Vector2.DistanceSquared(pr, tr) > minDistance) { right.Add(tr); pr = tr; }

            prevPressure = pressure;
            prevVector = sp.Vector;
        }

        var firstPoint = points[0].Point;
        var lastPoint = points.Count > 1 ? points[^1].Point : points[0].Point + Vector2.One;

        if (points.Count == 1)
        {
            // A click without movement: a round dot.
            var start = firstPoint + Uni(Per(firstPoint - lastPoint)) * -(firstRadius ?? radius);
            for (float t = 1 / 13f; t <= 1; t += 1 / 13f) outline.Add(RotAround(start, firstPoint, FixedPi * 2 * t));
            return outline;
        }

        if (left.Count == 0 || right.Count == 0) return outline;

        var startCap = new List<Vector2>();
        if (o.CapStart)
            for (float t = 1 / 13f; t <= 1; t += 1 / 13f) startCap.Add(RotAround(right[0], firstPoint, FixedPi * t));
        else
        {
            var corners = left[0] - right[0];
            startCap.Add(firstPoint - corners * 0.5f);
            startCap.Add(firstPoint - corners * 0.51f);
            startCap.Add(firstPoint + corners * 0.51f);
            startCap.Add(firstPoint + corners * 0.5f);
        }

        var endCap = new List<Vector2>();
        var direction = Per(-points[^1].Vector);
        if (o.CapEnd)
        {
            var start = lastPoint + direction * radius;
            for (float t = 1 / 29f; t < 1; t += 1 / 29f) endCap.Add(RotAround(start, lastPoint, FixedPi * 3 * t));
        }
        else
        {
            endCap.Add(lastPoint + direction * radius);
            endCap.Add(lastPoint + direction * radius * 0.99f);
            endCap.Add(lastPoint - direction * radius * 0.99f);
            endCap.Add(lastPoint - direction * radius);
        }

        right.Reverse();
        outline.Capacity = left.Count + endCap.Count + right.Count + startCap.Count;
        outline.AddRange(left);
        outline.AddRange(endCap);
        outline.AddRange(right);
        outline.AddRange(startCap);
        return outline;
    }

    static Vector2 Per(Vector2 a) => new(a.Y, -a.X);

    static Vector2 Uni(Vector2 a)
    {
        float len = a.Length();
        return len == 0 ? a : a / len;
    }

    static Vector2 RotAround(Vector2 a, Vector2 c, float r)
    {
        float s = MathF.Sin(r), co = MathF.Cos(r);
        float px = a.X - c.X, py = a.Y - c.Y;
        return new Vector2(px * co - py * s + c.X, px * s + py * co + c.Y);
    }
}
