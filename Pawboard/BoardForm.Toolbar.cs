using System.Numerics;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// The toolbar at the bottom. Simple (the default): just the tools, each with a dot in its
// colour; right-click a tool, or click the selected one again, for a small panel with its
// colours and sizes. Expanded (a switch in the menu): colours and sizes always on the bar.
// Hidden: the mini bar (see DrawMiniToolbar).
public sealed partial class BoardForm
{
    const float BarHeight = 48, ToolWidth = 40, SwatchWidth = 28, SizeWidth = 30, BarPad = 8, BarGap = 18;

    bool simpleToolbar = true;
    readonly int[] colorIndex = [0, 0, 0, 0];         // per tool: Pen, (Eraser), Text, Highlighter
    readonly Dictionary<Tool, RectangleF> toolSlots = new();   // where each tool's button is

    // The colours and sizes panel above a tool, while it's open.
    Tool? optionsTool;
    RectangleF optionsRect;
    readonly List<(RectangleF Rect, Action Click)> optionsButtons = new();

    static bool HasColors(Tool t) => t is Tool.Pen or Tool.Text or Tool.Highlighter;
    static bool HasSizes(Tool t) => t is Tool.Pen or Tool.Eraser or Tool.Text or Tool.Highlighter;
    static uint[] PaletteOf(Tool t) => t == Tool.Highlighter ? HighlightPalette : Palette;
    uint ColorOf(Tool t) => PaletteOf(t)[colorIndex[(int)t]];

    void DrawToolbar(ID2D1RenderTarget r)
    {
        toolbarButtons.Clear();
        toolSlots.Clear();
        var area = ToolbarArea;
        if (toolbarHidden)
        {
            DrawMiniToolbar(r, area);
            return;
        }
        var tools = ToolButtons;
        float h = BarHeight;
        float w = BarPad + tools.Length * ToolWidth + BarGap
            + (simpleToolbar ? 0 : Palette.Length * SwatchWidth + BarGap + 5 * SizeWidth + BarGap)
            + 2 * ToolWidth + BarGap + 2 * ToolWidth + BarPad;
        float x = MathF.Round(area.Left + (area.Width - w) / 2), y = area.Bottom - h - 14;
        toolbarRect = new RectangleF(x, y, w, h);
        DrawPanel(r, toolbarRect, 14);

        float cx = x + BarPad;
        foreach (var (t, icon) in tools)
        {
            DrawToolButton(r, t, icon, new RectangleF(cx, y, ToolWidth, h), inset: 7);
            cx += ToolWidth;
        }
        Divider(r, ref cx, y, h, BarGap);

        if (!simpleToolbar)
        {
            // Colours and sizes of the current tool, right on the bar. Tools without colours show
            // the pen's, faded (with the lasso they recolour what it picked up).
            var shown = HasColors(tool) ? tool : Tool.Pen;
            bool faded = tool is Tool.Eraser or Tool.Desktop || (tool == Tool.Lasso && picked == null);
            DrawSwatches(r, ref cx, y, h, shown, faded, ring: HasColors(tool), toolbarButtons, i => SetColor(tool, i));
            Divider(r, ref cx, y, h, BarGap);
            DrawSizes(r, ref cx, y, h, HasSizes(tool) ? tool : Tool.Pen, toolbarButtons, i => SetSize(tool, i));
            Divider(r, ref cx, y, h, BarGap);
        }

        // Undo / redo
        foreach (var (icon, enabled, action) in new (string, bool, Action)[]
                 { (UndoIcon, board.CanUndo || editing != null, UndoAction), (RedoIcon, board.CanRedo, RedoAction) })
        {
            brush!.Color = enabled ? Colors.Icon : WithAlpha(Colors.Icon, Colors.Icon.A * 0.35f);
            r.DrawText(icon, iconFont, ToDRect(IconRect(cx, y, h)), brush);
            if (enabled) toolbarButtons.Add((new RectangleF(cx, y, ToolWidth, h), action));
            cx += ToolWidth;
        }
        Divider(r, ref cx, y, h, BarGap);

        // ☰ menu
        var menuIcon = IconRect(cx, y, h);
        if (menuOpen)
        {
            brush!.Color = WithAlpha(Colors.Accent, dark ? 0.16f : 0.13f);
            r.FillRoundedRectangle(new RoundedRectangle(menuIcon, 9, 9), brush);
        }
        brush!.Color = menuOpen ? Colors.Accent : Colors.Icon;
        r.DrawText(MenuIcon, iconFont, ToDRect(menuIcon), brush);
        menuButtonRect = new RectangleF(cx, y, ToolWidth, h);
        toolbarButtons.Add((menuButtonRect, ToggleMenu));
        cx += ToolWidth;

        // Hide: the toolbar shrinks to the mini bar
        brush.Color = Colors.Icon;
        r.DrawText(HideIcon, iconFont, ToDRect(IconRect(cx, y, h)), brush);
        toolbarButtons.Add((new RectangleF(cx, y, ToolWidth, h), HideToolbar));
    }

    static RectangleF IconRect(float cx, float y, float h) => new(cx + 3, y + 7, ToolWidth - 6, h - 14);

    void DrawPanel(ID2D1RenderTarget r, RectangleF rect, float radius)
    {
        brush!.Color = Colors.PanelShadow;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(rect.X, rect.Y + 2, rect.Width, rect.Height), radius, radius), brush);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(new RoundedRectangle(rect, radius, radius), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawRoundedRectangle(new RoundedRectangle(RectangleF.Inflate(rect, -0.5f, -0.5f), radius, radius), brush, 1f);
    }

    // A tool's button (on the toolbar or the mini bar). Tools that draw show their icon in the
    // colour they draw with, and tools with sizes a dot under it that grows with the size.
    void DrawToolButton(ID2D1RenderTarget r, Tool t, string icon, RectangleF slot, float inset)
    {
        var rect = new RectangleF(slot.X + 3, slot.Y + inset, slot.Width - 6, slot.Height - inset * 2);
        bool selected = tool == t;
        if (selected)
        {
            brush!.Color = WithAlpha(Colors.Accent, dark ? 0.16f : 0.13f);
            r.FillRoundedRectangle(new RoundedRectangle(rect, 9, 9), brush);
        }
        bool sized = HasSizes(t);
        var iconRect = sized ? new RectangleF(rect.X, rect.Y - 3, rect.Width, rect.Height) : rect;   // room for the size dot
        brush!.Color = HasColors(t) ? ToolInk(t) : selected ? Colors.Accent : Colors.Icon;
        if (t == Tool.Lasso) DrawLassoIcon(r, iconRect, brush.Color);
        else r.DrawText(icon, iconFont, ToDRect(iconRect), brush);
        if (sized) DrawSizeDot(r, t, new Vector2(slot.X + slot.Width / 2, rect.Bottom - 4.5f));
        var chosen = t;
        toolSlots[t] = slot;
        toolbarButtons.Add((slot, () => ClickTool(chosen)));
    }

    Color4 ToolInk(Tool t) => Argb(Colors.Display(ColorOf(t)));

    // The current size at a glance: five steps of dot (a ring for the eraser).
    void DrawSizeDot(ID2D1RenderTarget r, Tool t, Vector2 at)
    {
        float radius = 1.2f + sizeIndex[(int)t] * 0.55f;
        if (t == Tool.Eraser)
        {
            brush!.Color = Colors.Faint(0.55f);
            r.DrawEllipse(new Ellipse(at, radius + 0.4f, radius + 0.4f), brush, 1f);
            return;
        }
        brush!.Color = ToolInk(t);
        r.FillEllipse(new Ellipse(at, radius, radius), brush);
    }

    // Five colour swatches for a tool (`ring` marks its current one).
    void DrawSwatches(ID2D1RenderTarget r, ref float cx, float y, float h, Tool t, bool faded, bool ring,
        List<(RectangleF Rect, Action Click)> buttons, Action<int> pick)
    {
        var palette = PaletteOf(t);
        float cy = y + h / 2;
        for (int i = 0; i < palette.Length; i++)
        {
            var center = new Vector2(cx + SwatchWidth / 2, cy);
            var color = Argb(Colors.Display(palette[i]));
            if (ring && i == colorIndex[(int)t])
            {
                brush!.Color = WithAlpha(color, 0.35f);
                r.DrawEllipse(new Ellipse(center, 12, 12), brush, 2f);
            }
            brush!.Color = WithAlpha(color, faded ? 0.3f : 1f);
            r.FillEllipse(new Ellipse(center, 8, 8), brush);
            int index = i;
            buttons.Add((new RectangleF(cx, y, SwatchWidth, h), () => pick(index)));
            cx += SwatchWidth;
        }
    }

    // Five sizes for a tool, each shown the way that tool draws.
    void DrawSizes(ID2D1RenderTarget r, ref float cx, float y, float h, Tool t,
        List<(RectangleF Rect, Action Click)> buttons, Action<int> pick)
    {
        int ti = (int)t;
        float cy = y + h / 2;
        for (int i = 0; i < 5; i++)
        {
            var center = new Vector2(cx + SizeWidth / 2, cy);
            if (i == sizeIndex[ti])
            {
                brush!.Color = Colors.Faint(0.08f);
                r.FillEllipse(new Ellipse(center, 13.5f, 13.5f), brush);
            }
            switch (t)
            {
                case Tool.Pen:
                    float dot = MathF.Min(10, 1.5f + ToolSizes[ti][i] * 0.45f);
                    brush!.Color = Argb(Colors.Display(ColorOf(t)));
                    r.FillEllipse(new Ellipse(center, dot, dot), brush);
                    break;
                case Tool.Eraser:
                    float ring = 3 + i * 2.2f;
                    brush!.Color = Colors.Faint(0.55f);
                    r.DrawEllipse(new Ellipse(center, ring, ring), brush, 1.2f);
                    break;
                case Tool.Text:
                    brush!.Color = Argb(Colors.Display(ColorOf(t)));
                    r.DrawText("a", sizeLetterFonts[i], new DRect(center.X - 14, center.Y - 15, 28, 28), brush);
                    break;
                case Tool.Highlighter:
                    // A short see-through band as tall as the highlighter is wide (scaled down).
                    float band = MathF.Min(18, 3 + ToolSizes[ti][i] * 0.32f);
                    brush!.Color = WithAlpha(Argb(Colors.Display(ColorOf(t))), MathF.Min(1, Colors.HighlightAlpha * 1.6f));
                    r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(center.X - 9, center.Y - band / 2, 18, band), band / 2, band / 2), brush);
                    break;
            }
            int index = i;
            buttons.Add((new RectangleF(cx, y, SizeWidth, h), () => pick(index)));
            cx += SizeWidth;
        }
    }

    // ---------- the colours and sizes panel ----------

    // Clicking the tool that's already selected opens (or closes) its panel.
    void ClickTool(Tool t)
    {
        if (t == tool && HasSizes(t)) ToggleOptions(t);
        else SetTool(t);
    }

    Tool? ToolAt(Vector2 at)
    {
        foreach (var (t, slot) in toolSlots)
            if (slot.Contains(at.X, at.Y)) return t;
        return null;
    }

    void ToggleOptions(Tool t)
    {
        CloseMenu();
        optionsTool = optionsTool == t || !HasSizes(t) ? null : t;
        Invalidate();
    }

    void CloseOptions()
    {
        if (optionsTool == null) return;
        optionsTool = null;
        Invalidate();
    }

    // A left click while the panel is open: used up when it's on the panel. Anywhere else just
    // closes it, and the click goes on to do its job (so you can pick a colour and draw at once).
    bool OptionsClick(Vector2 at)
    {
        if (optionsTool is not { } open) return false;
        if (!optionsRect.Contains(at.X, at.Y))
        {
            CloseOptions();
            // On that tool's own button, closing was the whole point (or it would reopen at once).
            return toolSlots.TryGetValue(open, out var slot) && slot.Contains(at.X, at.Y);
        }
        foreach (var (rect, click) in optionsButtons)
            if (rect.Contains(at.X, at.Y)) { click(); break; }
        Invalidate();
        return true;
    }

    void DrawOptions(ID2D1RenderTarget r)
    {
        optionsButtons.Clear();
        if (optionsTool is not { } t || !toolSlots.TryGetValue(t, out var slot)) { optionsRect = RectangleF.Empty; return; }
        float h = BarHeight;
        float w = BarPad + (HasColors(t) ? Palette.Length * SwatchWidth + BarGap : 0) + 5 * SizeWidth + BarPad;
        var area = ToolbarArea;
        float x = Math.Clamp(MathF.Round(slot.X + slot.Width / 2 - w / 2), area.Left + 8, area.Right - w - 8);
        float y = toolbarRect.Top - 8 - h;
        optionsRect = new RectangleF(x, y, w, h);
        DrawPanel(r, optionsRect, 14);
        float cx = x + BarPad;
        if (HasColors(t))
        {
            DrawSwatches(r, ref cx, y, h, t, faded: false, ring: true, optionsButtons, i => SetColor(t, i));
            Divider(r, ref cx, y, h, BarGap);
        }
        DrawSizes(r, ref cx, y, h, t, optionsButtons, i => SetSize(t, i));
    }

    void ToggleSimpleToolbar()
    {
        simpleToolbar = !simpleToolbar;
        CloseOptions();
        SaveSettings();
        Invalidate();
    }
}
