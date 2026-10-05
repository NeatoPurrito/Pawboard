using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Pawboard;

public abstract class Item
{
    public uint Color;              // 0xAARRGGBB
    public RectangleF Bounds;       // world space; derived, never saved

    // Frees the item's graphics resources once nothing (board or undo history) can show it again.
    public abstract void Release();
}

public sealed class Stroke : Item
{
    public float Size;              // world units
    public float Smoothing;         // world units: how far along the line points blend with neighbours (0 = none)
    public List<Vector2> Points = new();   // input points in world space
    // Eraser paths that went over this stroke; their shape is cut out of the line.
    public List<EraseRun> Erasures = new();
    // How many of Erasures came from the stroke this was copied from. Those are shared, so never modified.
    public int InheritedRuns;

    // Derived from Points and Erasures; rebuilt on load, never saved.
    public ID2D1PathGeometry? Geometry;

    // A copy to erase into. Points never change after drawing, so they're shared.
    public Stroke CopyForErasing() => new()
    {
        Color = Color, Size = Size, Smoothing = Smoothing, Points = Points,
        Erasures = new List<EraseRun>(Erasures), InheritedRuns = Erasures.Count, Bounds = Bounds,
    };

    public override void Release()
    {
        Geometry?.Dispose();
        Geometry = null;
    }
}

// One continuous eraser movement over a stroke: a round eraser of Radius dragged through Points.
public sealed class EraseRun
{
    public float Radius;
    public List<Vector2> Points = new();
}

public sealed class TextItem : Item
{
    public string Text = "";
    public Vector2 Position;        // world space, top-left
    public float FontSize;          // world units
    public float Width;             // world units; 0 = as wide as the text, otherwise lines wrap at this width

    // Laid out at TextInk.ReferenceSize and scaled when drawn; rebuilt on load, never saved.
    public IDWriteTextLayout? Layout;

    public float Scale => FontSize / TextInk.ReferenceSize;

    public TextItem Copy() =>
        new() { Color = Color, FontSize = FontSize, Width = Width, Text = Text, Position = Position };

    public override void Release()
    {
        Layout?.Dispose();
        Layout = null;
    }
}

// What's on the board, plus undo history. Each change stores the item list before and after, so
// one undo step can cover anything: a stroke, a whole eraser drag that cut many strokes, a text edit.
// The app runs all day, so items that drop out of reach (oldest undo steps, cleared redo) have
// their graphics resources freed.
public sealed class Board
{
    const int MaxUndo = 100;

    public readonly List<Item> Items = new();
    readonly List<(Item[] Before, Item[] After)> undo = new();
    readonly List<(Item[] Before, Item[] After)> redo = new();

    public event Action? Changed;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Add(Item item) => Commit([.. Items, item]);

    // Swap one item for another in the same spot (or remove it when replacement is null).
    public void Replace(Item old, Item? replacement)
    {
        var after = new List<Item>(Items.Count);
        foreach (var i in Items)
        {
            if (i != old) after.Add(i);
            else if (replacement != null) after.Add(replacement);
        }
        Commit(after);
    }

    public void Commit(List<Item> after)
    {
        var before = Items.ToArray();
        Items.Clear();
        Items.AddRange(after);
        undo.Add((before, after.ToArray()));

        var dropped = new List<(Item[] Before, Item[] After)>(redo);
        redo.Clear();
        if (undo.Count > MaxUndo)
        {
            dropped.Add(undo[0]);
            undo.RemoveAt(0);
        }
        ReleaseUnreachable(dropped);
        Changed?.Invoke();
    }

    public bool Undo() => Step(undo, redo, back: true);
    public bool Redo() => Step(redo, undo, back: false);

    bool Step(List<(Item[] Before, Item[] After)> from, List<(Item[] Before, Item[] After)> to, bool back)
    {
        if (from.Count == 0) return false;
        var edit = from[^1];
        from.RemoveAt(from.Count - 1);
        Items.Clear();
        Items.AddRange(back ? edit.Before : edit.After);
        to.Add(edit);
        Changed?.Invoke();
        return true;
    }

    // Frees items that were only reachable through history steps that are now gone.
    void ReleaseUnreachable(List<(Item[] Before, Item[] After)> dropped)
    {
        if (dropped.Count == 0) return;
        var reachable = new HashSet<Item>(Items);
        foreach (var (b, a) in undo) { reachable.UnionWith(b); reachable.UnionWith(a); }
        foreach (var (b, a) in redo) { reachable.UnionWith(b); reachable.UnionWith(a); }
        var released = new HashSet<Item>();
        foreach (var (b, a) in dropped)
            foreach (var item in b.Concat(a))
                if (!reachable.Contains(item) && released.Add(item)) item.Release();
    }

    // Everything, on the way out (e.g. Explorer restarted and the board is rebuilt).
    public void ReleaseAll()
    {
        var all = new HashSet<Item>(Items);
        foreach (var (b, a) in undo.Concat(redo)) { all.UnionWith(b); all.UnionWith(a); }
        foreach (var item in all) item.Release();
    }
}
