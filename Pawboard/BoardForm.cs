using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Color4 = Vortice.Mathematics.Color4;
using SizeI = Vortice.Mathematics.SizeI;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// The whiteboard window. Nothing animates on its own: a frame is drawn only when input changes
// something, so an untouched board costs no CPU or GPU. Pan momentum, zoom easing and the text
// caret blink are the only timers, and each runs only while it's needed.
//
// Everything is driven from the toolbar at the bottom; the keyboard is only used for typing
// into a text box (plus Ctrl+Z / Ctrl+V).
public sealed partial class BoardForm : Form
{
    // Desktop: the board steps aside so icons and the desktop work as usual (wallpaper mode only).
    enum Tool { Pen, Eraser, Text, Desktop }

    static readonly uint[] Palette = [0xFF1E1E1E, 0xFF1971C2, 0xFFE03131, 0xFF2F9E44, 0xFFF08C00];
    // Per tool, five sizes in screen DIPs: pen width, eraser radius, text height.
    static readonly float[][] ToolSizes = [[3, 5, 8, 13, 20], [6, 10, 16, 26, 40], [16, 22, 30, 40, 56]];

    const float DotSpacing = 24;     // world units at zoom 1
    // Screen DIPs: mouse wiggles shorter than about this get ironed out of the line. Measured on
    // screen, so it feels the same at every zoom.
    const float PenSmoothing = 2f;
    // 100% is as far out as it goes: the board is exactly your screen (see BoardForm.Bounds.cs).
    const float MinZoom = 1f, MaxZoom = 8f;
    const string IconFont = "Segoe Fluent Icons";
    const string PenIcon = "\uE70F", EraserIcon = "\uE75C", TextIcon = "\uE8D2", UndoIcon = "\uE7A7", RedoIcon = "\uE7A6";
    const string DesktopIcon = "\uE8B0";
    const string MoonIcon = "\uE708", SunIcon = "\uE706";

    readonly ID2D1Factory factory = D2D1.D2D1CreateFactory<ID2D1Factory>();
    readonly IDWriteFactory dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
    readonly TextInk textInk;
    readonly IDWriteTextFormat iconFont;
    readonly IDWriteTextFormat[] sizeLetterFonts;
    readonly IDWriteTextFormat bannerFont;

    // GPU resources; thrown away and rebuilt if the graphics device is lost (driver update, sleep).
    ID2D1HwndRenderTarget? rt;
    ID2D1SolidColorBrush? brush;
    // Background, dots and finished items, drawn once per view change. While you draw,
    // a frame is just this bitmap plus whatever is in progress.
    ID2D1BitmapRenderTarget? cache;
    // A picture of the whole board at 100%. While a zoom animates, the frames just scale this
    // picture instead of redrawing everything (too slow for every frame on big screens); once
    // the zoom settles, the cache is redrawn sharp. Rebuilt only when the content has changed.
    ID2D1BitmapRenderTarget? boardPicture;
    int contentVersion, boardPictureVersion = -1;
    bool cacheDirty = true;

    readonly Board board = new();
    Vector2 offset;                  // screen position of the world origin, in DIPs
    float zoom = 1;

    float targetZoom = 1;
    Vector2 zoomAnchorScreen, zoomAnchorWorld;
    bool zooming;
    Vector2 panVelocity;
    bool coasting;
    long lastAnimTick;

    bool dark;
    Theme Colors => dark ? Theme.Dark : Theme.Light;

    Tool tool = Tool.Pen;
    int colorIndex;
    readonly int[] sizeIndex = [1, 1, 1];

    enum Mode { None, Draw, Erase, Pan, PressText, MoveText, ResizeText }
    Mode mode;
    MouseButtons modeButton;
    Vector2 cursor;                  // last mouse position, screen DIPs
    bool cursorInside;

    Stroke? active;
    ID2D1PathGeometry? activeGeometry;
    bool activeDirty;

    // While erasing, the drag works on a copy of the item list and commits it as one undo step.
    List<Item>? eraseWorking;
    readonly HashSet<Stroke> eraseCopies = new();   // strokes copied for this drag (never the originals)
    bool eraseChanged;
    Vector2 lastErase;

    TextItem? editing;               // the text box being typed in (a copy; the original stays for undo)
    TextItem? editOriginal;          // null when it's a new text
    int caret;
    readonly Stack<(string Text, int Caret)> editHistory = new();
    bool caretOn;
    readonly System.Windows.Forms.Timer caretTimer = new() { Interval = 530 };
    TextItem? hoverText;
    TextItem? pressedText;
    TextItem? movingText;
    Vector2 pressStart;
    // The original of a text being edited, moved or resized: drawn as an overlay instead.
    Item? HiddenInCache
    {
        get => hiddenItem;
        set { hiddenItem = value; contentVersion++; }
    }
    Item? hiddenItem;

    // Grab handles on a text box: corners scale the text, the side edges set where lines wrap.
    enum Grip { None, TopLeft, TopRight, BottomLeft, BottomRight, Left, Right }
    Grip hoverHandle;
    Grip resizeHandle;
    TextItem? resizeItem;            // the text being resized (the edit copy, or a copy of a placed text)
    TextItem? resizeOriginal;        // the placed text it replaces when done; null when resizing the edit copy
    RectangleF resizeStartBounds;
    Vector2 resizeStartPosition;
    float resizeStartFontSize, resizeStartWidth;

    Vector2 panLast;
    readonly Queue<(long Tick, Vector2 Offset)> panSamples = new();

    readonly Native.MOUSEMOVEPOINT[] moveBuffer = new Native.MOUSEMOVEPOINT[64];
    Native.MOUSEMOVEPOINT? lastMove;

    readonly List<(RectangleF Rect, Action Click)> toolbarButtons = new();
    RectangleF toolbarRect;

    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1500 };
    string? saveError;

    readonly bool wallpaper;         // living behind the desktop icons instead of in a window

    float EraserRadius => ToolSizes[(int)Tool.Eraser][sizeIndex[(int)Tool.Eraser]];

    public BoardForm(bool wallpaper = false)
    {
        this.wallpaper = wallpaper;
        Text = "Pawboard";
        if (wallpaper)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            tool = Tool.Desktop;   // start out of the way: the desktop works exactly as before
        }
        else
        {
            Icon = PawIcon(new Size(32, 32));
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(LogicalToDeviceUnits(1280), LogicalToDeviceUnits(800));
        }
        var settings = BoardStore.LoadSettings();
        dark = settings.Dark;
        if (!Enum.TryParse(settings.Background, out backdrop)) backdrop = Backdrop.Dots;
        ApplyWindowTheme();
        SetPointer(Cursors.Cross);
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);

        textInk = new TextInk(dwrite);
        iconFont = dwrite.CreateTextFormat(IconFont, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 17f);
        iconFont.TextAlignment = TextAlignment.Center;
        iconFont.ParagraphAlignment = ParagraphAlignment.Center;
        sizeLetterFonts = new[] { 11f, 13.5f, 16f, 19f, 22f }.Select(size =>
        {
            var f = dwrite.CreateTextFormat(TextInk.FontFamily, FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            f.TextAlignment = TextAlignment.Center;
            f.ParagraphAlignment = ParagraphAlignment.Center;
            return f;
        }).ToArray();
        bannerFont = dwrite.CreateTextFormat("Segoe UI", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 14f);
        CreateMenuFonts();

        LoadBoard();
        board.Changed += () => { contentVersion++; saveTimer.Stop(); saveTimer.Start(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveBoard(); };
        caretTimer.Tick += (_, _) => { caretOn = !caretOn; Invalidate(); };
    }

    // ---------- persistence ----------

    void LoadBoard()
    {
        var data = BoardStore.Load();
        offset = new Vector2(data.ViewX, data.ViewY);
        zoom = targetZoom = Math.Clamp(data.Zoom, MinZoom, MaxZoom);
        foreach (var saved in data.Items)
        {
            var item = BoardStore.ToItem(saved);
            if (item == null) continue;
            Finish(item);
            board.Items.Add(item);
        }
    }

    void SaveBoard()
    {
        try
        {
            BoardStore.Save(board, offset, zoom);
            saveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saveError = ex.Message;
            Invalidate();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (wallpaper) Log.Write($"closing: {e.CloseReason}");
        CommitTextEdit();
        saveTimer.Stop();
        SaveBoard();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (wallpaper) DetachFromDesktop();
        DiscardDevice();
        caretTimer.Dispose();
        textInk.Dispose();
        iconFont.Dispose();
        foreach (var f in sizeLetterFonts) f.Dispose();
        bannerFont.Dispose();
        DisposeMenuFonts();
        dwrite.Dispose();
        factory.Dispose();
        base.OnFormClosed(e);
    }

    void Finish(Item item)
    {
        switch (item)
        {
            case Stroke s: Ink.Finish(factory, s); break;
            case TextItem t: textInk.Finish(t); break;
        }
    }

    // ---------- coordinates ----------

    float DpiScale => DeviceDpi / 96f;
    Vector2 ToDip(System.Drawing.Point px) => new Vector2(px.X, px.Y) / DpiScale;
    Vector2 ScreenToWorld(Vector2 dip) => (dip - offset) / zoom;
    Vector2 WorldToScreen(Vector2 world) => world * zoom + offset;
    Matrix3x2 ViewTransform => Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(offset);
    Vector2 ClientDips => new Vector2(ClientSize.Width, ClientSize.Height) / DpiScale;

    RectangleF ToScreen(RectangleF world, float pad)
    {
        var tl = WorldToScreen(new Vector2(world.Left, world.Top));
        return new RectangleF(tl.X - pad, tl.Y - pad, world.Width * zoom + pad * 2, world.Height * zoom + pad * 2);
    }

    // ---------- device resources ----------

    void EnsureDevice()
    {
        if (rt != null) return;
        var props = new RenderTargetProperties { DpiX = DeviceDpi, DpiY = DeviceDpi };
        var hwndProps = new HwndRenderTargetProperties
        {
            Hwnd = Handle,
            PixelSize = new SizeI(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)),
            // Show each frame as soon as it's drawn instead of waiting a refresh: lowest pen latency.
            PresentOptions = PresentOptions.Immediately,
        };
        rt = factory.CreateHwndRenderTarget(props, hwndProps);
        rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        brush = rt.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
        // First frame (or a new graphics device): the window has its real size now.
        ClampView();
        cacheDirty = true;
    }

    void DiscardDevice()
    {
        cache?.Dispose(); cache = null;
        boardPicture?.Dispose(); boardPicture = null;
        brush?.Dispose(); brush = null;
        rt?.Dispose(); rt = null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (rt == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        rt.Resize(new SizeI(ClientSize.Width, ClientSize.Height));
        cache?.Dispose(); cache = null;
        boardPicture?.Dispose(); boardPicture = null;
        ClampView();
        cacheDirty = true;
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        hookDpiScale = DpiScale;
        DiscardDevice();
        Invalidate();
    }

    // ---------- rendering ----------

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Render();
        if (zooming || coasting)
        {
            // Wait for the next screen refresh, then draw again: smooth at 60, 144 or 240 Hz alike.
            Native.DwmFlush();
            Invalidate();
        }
    }

    void Render()
    {
        EnsureDevice();
        Animate();
        if (cache == null)
        {
            cache = rt!.CreateCompatibleRenderTarget(null, null, null, CompatibleRenderTargetOptions.None);
            cache.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            cacheDirty = true;
        }
        bool scalePicture = zooming;   // still zooming after this frame's animation step
        if (scalePicture) EnsureBoardPicture();
        else if (cacheDirty) RebuildCache();

        if (active != null && activeDirty)
        {
            activeGeometry?.Dispose();
            activeGeometry = Ink.BuildGeometry(factory, Ink.Outline(active, last: false));
            activeDirty = false;
        }

        var r = rt!;
        r.BeginDraw();
        r.Transform = Matrix3x2.Identity;
        if (scalePicture)
        {
            r.Transform = ViewTransform;
            r.DrawBitmap(boardPicture!.Bitmap, 1f, BitmapInterpolationMode.Linear);
            r.Transform = Matrix3x2.Identity;
        }
        else r.DrawBitmap(cache.Bitmap, 1f, BitmapInterpolationMode.NearestNeighbor);

        if (active != null && activeGeometry != null)
        {
            r.Transform = ViewTransform;
            brush!.Color = Argb(Colors.Display(active.Color));
            r.FillGeometry(activeGeometry, brush);
            r.Transform = Matrix3x2.Identity;
        }

        DrawTextOverlay(r);
        DrawEraserCursor(r);
        DrawToolbar(r);
        DrawMenu(r);
        if (saveError != null) DrawBanner(r, $"Couldn't save the board: {saveError}");

        if (r.EndDraw().Failure) DiscardDevice();   // device lost: rebuild everything on the next frame
    }

    void RebuildCache()
    {
        ClampView();   // the board may have shrunk (undo, erase) since the view was last checked
        if (PaintBoard(cache!)) cacheDirty = false;
    }

    void EnsureBoardPicture()
    {
        if (boardPicture == null)
        {
            boardPicture = rt!.CreateCompatibleRenderTarget(null, null, null, CompatibleRenderTargetOptions.None);
            boardPicture.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            boardPictureVersion = -1;
        }
        if (boardPictureVersion == contentVersion) return;
        // Paint the start view (100%, which is the whole board) into the picture.
        var (viewOffset, viewZoom) = (offset, zoom);
        (offset, zoom) = (Vector2.Zero, 1);
        bool painted = PaintBoard(boardPicture);
        (offset, zoom) = (viewOffset, viewZoom);
        if (painted) boardPictureVersion = contentVersion;
    }

    // Background, pattern and every visible item, at the current view. False if the device was lost.
    bool PaintBoard(ID2D1BitmapRenderTarget c)
    {
        c.BeginDraw();
        c.Transform = Matrix3x2.Identity;
        c.Clear(Colors.Background);
        DrawBackdrop(c);


        var view = VisibleWorldRect();
        foreach (var item in eraseWorking ?? board.Items)
        {
            if (item == HiddenInCache || !item.Bounds.IntersectsWith(view)) continue;
            DrawItem(c, item);
        }
        c.Transform = Matrix3x2.Identity;
        if (c.EndDraw().Failure) { DiscardDevice(); return false; }
        return true;
    }

    void DrawItem(ID2D1RenderTarget target, Item item)
    {
        brush!.Color = Argb(Colors.Display(item.Color));
        switch (item)
        {
            case Stroke { Geometry: { } geometry }:
                target.Transform = ViewTransform;
                target.FillGeometry(geometry, brush);
                break;
            case TextItem { Layout: { } layout } t:
                target.Transform = Matrix3x2.CreateScale(t.Scale) * Matrix3x2.CreateTranslation(t.Position) * ViewTransform;
                target.DrawTextLayout(Vector2.Zero, layout, brush, DrawTextOptions.NoSnap);
                break;
        }
        target.Transform = Matrix3x2.Identity;
    }

    // A new stroke only needs painting on top of the cache, not a full rebuild.
    void AddToCache(Item item)
    {
        if (cache == null || cacheDirty) { cacheDirty = true; return; }
        cache.BeginDraw();
        DrawItem(cache, item);
        if (cache.EndDraw().Failure) DiscardDevice();
    }

    RectangleF VisibleWorldRect()
    {
        var tl = ScreenToWorld(Vector2.Zero);
        var br = ScreenToWorld(ClientDips);
        return RectangleF.FromLTRB(tl.X, tl.Y, br.X, br.Y);
    }

    void DrawDots(ID2D1RenderTarget c)
    {
        // Pick a dot spacing between 24 and 48 DIPs at any zoom. Dots that only exist at the finer
        // spacing fade in as you zoom, so the grid never pops.
        float s = DotSpacing * zoom;
        while (s < 24) s *= 2;
        while (s >= 48) s /= 2;
        float fade = Math.Clamp((s - 24) / 24, 0, 1);
        fade = fade * fade * (3 - 2 * fade);

        var size = ClientDips;
        int i0 = (int)MathF.Floor(-offset.X / s), i1 = (int)MathF.Ceiling((size.X - offset.X) / s);
        int j0 = (int)MathF.Floor(-offset.Y / s), j1 = (int)MathF.Ceiling((size.Y - offset.Y) / s);
        const float r = 0.9f;
        for (int pass = 0; pass < 2; pass++)
        {
            bool coarsePass = pass == 0;
            var color = Colors.Dots;
            if (!coarsePass)
            {
                if (fade <= 0.01f) break;
                color = WithAlpha(color, fade);
            }
            brush!.Color = color;
            for (int j = j0; j <= j1; j++)
            {
                float y = offset.Y + j * s;
                for (int i = i0; i <= i1; i++)
                {
                    bool coarse = (i & 1) == 0 && (j & 1) == 0;
                    if (coarse != coarsePass) continue;
                    float x = offset.X + i * s;
                    c.FillRectangle(new Vortice.RawRectF(x - r, y - r, x + r, y + r), brush);
                }
            }
        }
    }

    void DrawTextOverlay(ID2D1RenderTarget r)
    {
        if (movingText != null)
        {
            DrawItem(r, movingText);
            DrawBox(r, movingText, Colors.Accent, 1.5f, handles: false);
        }
        else if (mode == Mode.ResizeText && resizeOriginal != null && resizeItem != null)
        {
            DrawItem(r, resizeItem);
            DrawBox(r, resizeItem, Colors.Accent, 1.5f, handles: true);
        }
        else if (editing != null)
        {
            DrawItem(r, editing);
            DrawBox(r, editing, Colors.Accent, 1.5f, handles: true);
            if (caretOn)
            {
                var (top, height) = textInk.Caret(editing, caret);
                var a = WorldToScreen(top);
                brush!.Color = Argb(Colors.Display(editing.Color));
                r.DrawLine(a, a + new Vector2(0, height * zoom), brush, 1.6f);
            }
        }
        else if (hoverText != null && tool == Tool.Text && mode == Mode.None)
        {
            DrawBox(r, hoverText, Colors.Faint(0.3f), 1f, handles: true);
        }
    }

    void DrawBox(ID2D1RenderTarget r, TextItem t, Color4 color, float width, bool handles)
    {
        var box = ToScreen(t.Bounds, 6);
        brush!.Color = color;
        r.DrawRoundedRectangle(new RoundedRectangle(box, 6, 6), brush, width);
        if (!handles) return;
        foreach (var (handle, p) in HandlePoints(t))
        {
            var rect = handle is Grip.Left or Grip.Right
                ? new RectangleF(p.X - 2.5f, p.Y - 8, 5, 16)
                : new RectangleF(p.X - 4, p.Y - 4, 8, 8);
            brush.Color = Colors.Panel;
            r.FillRoundedRectangle(new RoundedRectangle(rect, 2.5f, 2.5f), brush);
            brush.Color = color;
            r.DrawRoundedRectangle(new RoundedRectangle(rect, 2.5f, 2.5f), brush, 1.2f);
        }
    }

    // Handle positions in screen DIPs, on the corners and side midpoints of the text's box.
    (Grip, Vector2)[] HandlePoints(TextItem t)
    {
        var b = ToScreen(t.Bounds, 6);
        float midY = (b.Top + b.Bottom) / 2;
        return
        [
            (Grip.TopLeft, new(b.Left, b.Top)), (Grip.TopRight, new(b.Right, b.Top)),
            (Grip.BottomLeft, new(b.Left, b.Bottom)), (Grip.BottomRight, new(b.Right, b.Bottom)),
            (Grip.Left, new(b.Left, midY)), (Grip.Right, new(b.Right, midY)),
        ];
    }

    Grip HandleAt(TextItem? t, Vector2 screen)
    {
        if (t == null) return Grip.None;
        foreach (var (handle, p) in HandlePoints(t))
            if (Vector2.Distance(p, screen) <= 9) return handle;
        return Grip.None;
    }

    void DrawEraserCursor(ID2D1RenderTarget r)
    {
        bool show = mode == Mode.Erase || (tool == Tool.Eraser && mode == Mode.None && cursorInside && !toolbarRect.Contains(cursor.X, cursor.Y));
        if (!show) return;
        float radius = EraserRadius;
        brush!.Color = Colors.Faint(0.05f);
        r.FillEllipse(new Ellipse(cursor, radius, radius), brush);
        brush.Color = Colors.Faint(0.45f);
        r.DrawEllipse(new Ellipse(cursor, radius, radius), brush, 1f);
    }

    void DrawToolbar(ID2D1RenderTarget r)
    {
        toolbarButtons.Clear();
        const float h = 48, toolW = 40, swatch = 28, sizeSlot = 30, pad = 8, gap = 18;
        var tools = ToolButtons;
        float w = pad + tools.Length * toolW + gap + Palette.Length * swatch + gap + 5 * sizeSlot + gap + 2 * toolW + gap + toolW + pad;
        var area = ToolbarArea;
        float x = MathF.Round(area.Left + (area.Width - w) / 2), y = area.Bottom - h - 14;
        toolbarRect = new RectangleF(x, y, w, h);

        brush!.Color = Colors.PanelShadow;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y + 2, w, h), 14, 14), brush);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y, w, h), 14, 14), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawRoundedRectangle(new RoundedRectangle(new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1), 14, 14), brush, 1f);

        float cx = x + pad;
        float cy = y + h / 2;

        // Tools
        foreach (var (t, icon) in tools)
        {
            var rect = new RectangleF(cx + 3, y + 7, toolW - 6, h - 14);
            bool selected = tool == t;
            if (selected)
            {
                brush.Color = WithAlpha(Colors.Accent, dark ? 0.16f : 0.13f);
                r.FillRoundedRectangle(new RoundedRectangle(rect, 9, 9), brush);
            }
            brush.Color = selected ? Colors.Accent : Colors.Icon;
            r.DrawText(icon, iconFont, ToDRect(rect), brush);
            var chosen = t;
            toolbarButtons.Add((new RectangleF(cx, y, toolW, h), () => SetTool(chosen)));
            cx += toolW;
        }
        Divider(r, ref cx, y, h, gap);

        // Colours (the eraser has none, so they fade while it's selected)
        float colorAlpha = tool is Tool.Eraser or Tool.Desktop ? 0.3f : 1f;
        for (int i = 0; i < Palette.Length; i++)
        {
            var center = new Vector2(cx + swatch / 2, cy);
            if (i == colorIndex && tool is Tool.Pen or Tool.Text)
            {
                brush.Color = WithAlpha(Argb(Colors.Display(Palette[i])), 0.35f);
                r.DrawEllipse(new Ellipse(center, 12, 12), brush, 2f);
            }
            brush.Color = WithAlpha(Argb(Colors.Display(Palette[i])), colorAlpha);
            r.FillEllipse(new Ellipse(center, 8, 8), brush);
            int index = i;
            toolbarButtons.Add((new RectangleF(cx, y, swatch, h), () => SetColor(index)));
            cx += swatch;
        }
        Divider(r, ref cx, y, h, gap);

        // Sizes for the current tool
        int ti = tool == Tool.Desktop ? (int)Tool.Pen : (int)tool;   // Desktop shows the pen's sizes
        for (int i = 0; i < 5; i++)
        {
            var center = new Vector2(cx + sizeSlot / 2, cy);
            if (i == sizeIndex[ti])
            {
                brush.Color = Colors.Faint(0.08f);
                r.FillEllipse(new Ellipse(center, 13.5f, 13.5f), brush);
            }
            var ink = Argb(Colors.Display(Palette[colorIndex]));
            switch ((Tool)ti)
            {
                case Tool.Pen:
                    float dot = MathF.Min(10, 1.5f + ToolSizes[ti][i] * 0.45f);
                    brush.Color = ink;
                    r.FillEllipse(new Ellipse(center, dot, dot), brush);
                    break;
                case Tool.Eraser:
                    float ring = 3 + i * 2.2f;
                    brush.Color = Colors.Faint(0.55f);
                    r.DrawEllipse(new Ellipse(center, ring, ring), brush, 1.2f);
                    break;
                case Tool.Text:
                    brush.Color = ink;
                    r.DrawText("a", sizeLetterFonts[i], new DRect(center.X - 14, center.Y - 15, 28, 28), brush);
                    break;
            }
            int index = i;
            toolbarButtons.Add((new RectangleF(cx, y, sizeSlot, h), () => SetSize(index)));
            cx += sizeSlot;
        }
        Divider(r, ref cx, y, h, gap);

        // Undo / redo
        foreach (var (icon, enabled, action) in new (string, bool, Action)[]
                 { (UndoIcon, board.CanUndo || editing != null, UndoAction), (RedoIcon, board.CanRedo, RedoAction) })
        {
            var rect = new RectangleF(cx + 3, y + 7, toolW - 6, h - 14);
            brush.Color = enabled ? Colors.Icon : WithAlpha(Colors.Icon, Colors.Icon.A * 0.35f);
            r.DrawText(icon, iconFont, ToDRect(rect), brush);
            if (enabled) toolbarButtons.Add((new RectangleF(cx, y, toolW, h), action));
            cx += toolW;
        }
        Divider(r, ref cx, y, h, gap);

        // ☰ menu: save / open, background pattern, light / dark
        var menuIcon = new RectangleF(cx + 3, y + 7, toolW - 6, h - 14);
        if (menuOpen)
        {
            brush.Color = WithAlpha(Colors.Accent, dark ? 0.16f : 0.13f);
            r.FillRoundedRectangle(new RoundedRectangle(menuIcon, 9, 9), brush);
        }
        brush.Color = menuOpen ? Colors.Accent : Colors.Icon;
        r.DrawText(MenuIcon, iconFont, ToDRect(menuIcon), brush);
        menuButtonRect = new RectangleF(cx, y, toolW, h);
        toolbarButtons.Add((menuButtonRect, () => { menuOpen = !menuOpen; Invalidate(); }));
    }

    void Divider(ID2D1RenderTarget r, ref float cx, float y, float h, float gap)
    {
        brush!.Color = Colors.Faint(dark ? 0.12f : 0.09f);
        float lx = MathF.Round(cx + gap / 2) + 0.5f;
        r.DrawLine(new Vector2(lx, y + 12), new Vector2(lx, y + h - 12), brush, 1f);
        cx += gap;
    }

    void DrawBanner(ID2D1RenderTarget r, string text)
    {
        var client = ClientDips;
        brush!.Color = new Color4(0.88f, 0.19f, 0.19f, 0.95f);
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(10, client.Y - 120, client.X - 20, 36), 8, 8), brush);
        brush.Color = new Color4(1, 1, 1, 1);
        r.DrawText(text, bannerFont, new DRect(22, client.Y - 112, client.X - 44, 30), brush);
    }

    // ---------- toolbar actions ----------

    void ToggleDark()
    {
        dark = !dark;
        contentVersion++;
        ApplyWindowTheme();
        cacheDirty = true;
        Invalidate();
        SaveSettings();

    }

    void ApplyWindowTheme()
    {
        var bg = Colors.Background;
        BackColor = System.Drawing.Color.FromArgb((int)(bg.R * 255), (int)(bg.G * 255), (int)(bg.B * 255));
        if (IsHandleCreated) Native.UseDarkTitleBar(Handle, dark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (wallpaper) AttachToDesktop();
        else Native.UseDarkTitleBar(Handle, dark);
    }

    string boardFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    void SaveBoardAs()
    {
        CommitTextEdit();
        using var dialog = new SaveFileDialog
        {
            Title = "Save a copy of this board",
            Filter = "Pawboard board (*.pawboard)|*.pawboard",
            DefaultExt = "pawboard",
            FileName = $"board {DateTime.Now:yyyy-MM-dd}.pawboard",
            InitialDirectory = boardFolder,
        };
        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
        boardFolder = Path.GetDirectoryName(dialog.FileName) ?? boardFolder;
        try
        {
            BoardStore.WriteFile(dialog.FileName, board, offset, zoom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(DialogOwner, $"Couldn't save the board:\n{ex.Message}", "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OpenBoard()
    {
        CommitTextEdit();
        using var dialog = new OpenFileDialog
        {
            Title = "Open a saved board",
            Filter = "Pawboard board (*.pawboard;*.deskboard)|*.pawboard;*.deskboard|All files (*.*)|*.*",
            InitialDirectory = boardFolder,
        };
        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
        boardFolder = Path.GetDirectoryName(dialog.FileName) ?? boardFolder;

        var data = BoardStore.ReadFile(dialog.FileName);
        if (data == null)
        {
            MessageBox.Show(DialogOwner, "That file couldn't be read as a Pawboard board.", "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var items = new List<Item>();
        foreach (var saved in data.Items)
        {
            var item = BoardStore.ToItem(saved);
            if (item == null) continue;
            Finish(item);
            items.Add(item);
        }
        // Replacing the board is a single undo step, so Undo brings back what was there before.
        board.Commit(items);
        zooming = coasting = false;
        offset = new Vector2(data.ViewX, data.ViewY);
        zoom = targetZoom = Math.Clamp(data.Zoom, MinZoom, MaxZoom);
        ClampView();

        hoverText = null;
        cacheDirty = true;
        Invalidate();
    }

    void SetTool(Tool t)
    {
        CommitTextEdit();
        CloseMenu();
        tool = t;
        hoverText = null;
        UpdateCursor();
        Invalidate();
    }

    void SetColor(int index)
    {
        colorIndex = index;
        if (tool is Tool.Eraser or Tool.Desktop) SetTool(Tool.Pen);   // picking a colour means you want to draw
        if (editing != null)
        {
            editing.Color = Palette[index];
            Invalidate();
        }
    }

    void SetSize(int index)
    {
        if (tool == Tool.Desktop) SetTool(Tool.Pen);
        sizeIndex[(int)tool] = index;
        if (editing != null)
        {
            editing.FontSize = ToolSizes[(int)Tool.Text][index] / zoom;
            textInk.Finish(editing);
        }
        Invalidate();
    }

    void UndoAction()
    {
        if (editing != null)
        {
            if (editHistory.Count > 0) { var (text, c) = editHistory.Pop(); SetEditText(text, c, record: false); }
            else CommitTextEdit();
            return;
        }
        if (mode == Mode.None && board.Undo()) cacheDirty = true;
        Invalidate();
    }

    void RedoAction()
    {
        CommitTextEdit();
        if (mode == Mode.None && board.Redo()) cacheDirty = true;
        Invalidate();
    }

    // The mouse pointer shape, in a window only. On the wallpaper the icon layer above the board
    // owns the pointer and resets it at once, so setting it there just makes it flicker.
    void SetPointer(Cursor shape)
    {
        if (!wallpaper) Cursor = shape;
    }

    void UpdateCursor()
    {
        if (mode == Mode.Pan) { SetPointer(Cursors.SizeAll); return; }
        if (toolbarRect.Contains(cursor.X, cursor.Y)) { SetPointer(Cursors.Hand); return; }
        var handle = mode == Mode.ResizeText ? resizeHandle : hoverHandle;
        SetPointer(tool switch
        {
            Tool.Text when handle is Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
            Tool.Text when handle is Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
            Tool.Text when handle is Grip.Left or Grip.Right => Cursors.SizeWE,
            Tool.Text when hoverText != null && editing == null => Cursors.SizeAll,
            Tool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        });
    }

    // ---------- animation ----------

    void StartAnimation()
    {
        lastAnimTick = Stopwatch.GetTimestamp();
        Invalidate();
    }

    void Animate()
    {
        if (!zooming && !coasting) return;
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((float)Stopwatch.GetElapsedTime(lastAnimTick, now).TotalSeconds, 0, 1 / 30f);
        lastAnimTick = now;

        if (zooming)
        {
            var (lastZoom, lastOffset) = (zoom, offset);
            zoom += (targetZoom - zoom) * (1 - MathF.Exp(-dt * 22));
            if (MathF.Abs(zoom - targetZoom) / targetZoom < 0.002f) { zoom = targetZoom; zooming = false; }
            offset = zoomAnchorScreen - zoomAnchorWorld * zoom;
            ClampView();
            if (zoom != lastZoom || offset != lastOffset) cacheDirty = true;
        }
        if (coasting)
        {
            var lastOffset = offset;
            var moved = offset + panVelocity * dt;
            offset = moved;
            ClampView();
            // Hitting an edge stops the fling in that direction instead of pressing against it.
            if (offset.X != moved.X) panVelocity.X = 0;
            if (offset.Y != moved.Y) panVelocity.Y = 0;
            panVelocity *= MathF.Exp(-dt * 5.5f);
            if (panVelocity.Length() < 10) coasting = false;
            if (offset != lastOffset) cacheDirty = true;
        }
    }

    void SettleZoom()
    {
        if (!zooming) return;
        zoom = targetZoom;
        offset = zoomAnchorScreen - zoomAnchorWorld * zoom;
        ClampView();
        zooming = false;
        cacheDirty = true;
    }

    // ---------- mouse ----------

    // Window events feed the shared Pointer* handlers. In wallpaper mode the window never gets
    // mouse events (the icon layer sits on top); the desktop hooks call the same handlers instead.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!wallpaper) PointerDown(e.Button, e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!wallpaper) PointerMove(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!wallpaper) PointerLeave();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!wallpaper) PointerUp(e.Button, e.Location);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        // Alt+Tab or a popup stole the mouse mid-gesture: finish it cleanly instead of leaving it stuck.
        if (wallpaper || Capture || mode == Mode.None) return;
        EndGesture(null);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!wallpaper) PointerWheel(e.Delta, e.Location);
    }

    void PointerDown(MouseButtons button, System.Drawing.Point location)
    {
        cursor = ToDip(location);
        if (mode != Mode.None) return;

        if (button == MouseButtons.Left && MenuClick(cursor)) return;
        if (button != MouseButtons.Left) CloseMenu();

        if (button == MouseButtons.Left && toolbarRect.Contains(cursor.X, cursor.Y))
        {
            foreach (var (rect, click) in toolbarButtons)
                if (rect.Contains(cursor.X, cursor.Y)) { click(); break; }
            Invalidate();
            return;
        }

        if (button == MouseButtons.Middle) BeginPan(button);
        else if (button == MouseButtons.Right) BeginErase(button);   // right-drag erases with any tool
        else if (button == MouseButtons.Left)
        {
            switch (tool)
            {
                case Tool.Pen: BeginStroke(); break;
                case Tool.Eraser: BeginErase(button); break;
                case Tool.Text: TextMouseDown(); break;
            }
        }
    }

    void PointerMove(System.Drawing.Point location)
    {
        cursor = ToDip(location);
        cursorInside = true;
        switch (mode)
        {
            case Mode.Draw:
                foreach (var p in CollectMoves(location)) AddStrokePoint(p);
                Invalidate();
                break;
            case Mode.Erase:
                EraseAlong(cursor);
                Invalidate();
                break;
            case Mode.Pan:
            {
                var lastOffset = offset;
                offset += cursor - panLast;
                ClampView();
                panLast = cursor;
                RecordPanSample();
                // Pressed against an edge: nothing moved, so there's nothing to redraw.
                if (offset == lastOffset) break;
                cacheDirty = true;
                Invalidate();
                break;
            }
            case Mode.PressText:
                // Only start moving after a few pixels, so a slightly shaky click still opens the text.
                if (Vector2.Distance(cursor, pressStart) > 4) BeginMoveText();
                break;
            case Mode.MoveText:
                MoveText();
                Invalidate();
                break;
            case Mode.ResizeText:
                UpdateResize();
                Invalidate();
                break;
            default:
                if (tool == Tool.Eraser) Invalidate();   // the eraser circle follows the mouse
                if (tool == Tool.Text)
                {
                    if (editing == null)
                    {
                        // Keep the box while the mouse is on one of its handles, which stick out past the text.
                        var hit = HandleAt(hoverText, cursor) != Grip.None ? hoverText : TextAt(ScreenToWorld(cursor));
                        if (hit != hoverText) { hoverText = hit; Invalidate(); }
                    }
                    hoverHandle = HandleAt(editing ?? hoverText, cursor);
                }
                UpdateCursor();
                break;
        }
    }

    void PointerLeave()
    {
        cursorInside = false;
        hoverText = null;
        Invalidate();
    }

    void PointerUp(MouseButtons button, System.Drawing.Point location)
    {
        if (mode == Mode.None || button != modeButton) return;
        cursor = ToDip(location);
        EndGesture(location);
    }

    void PointerWheel(int delta, System.Drawing.Point location)
    {
        if (mode != Mode.None && mode != Mode.Pan) return;
        var p = ToDip(location);
        coasting = false;
        float next = Math.Clamp(targetZoom * MathF.Pow(1.2f, delta / 120f), MinZoom, MaxZoom);
        // Already at 100% (or fully zoomed in) and scrolling further: nothing to do.
        if (next == targetZoom && !zooming) return;
        targetZoom = next;
        zoomAnchorScreen = p;
        zoomAnchorWorld = ScreenToWorld(p);
        zooming = true;
        StartAnimation();
    }
    void EndGesture(System.Drawing.Point? upLocation)
    {
        switch (mode)
        {
            case Mode.Draw: EndStroke(upLocation); break;
            case Mode.Erase: EndErase(); break;
            case Mode.Pan: EndPan(); break;
            case Mode.PressText:
                if (upLocation != null && pressedText != null)
                    StartEdit(pressedText, textInk.IndexAt(pressedText, ScreenToWorld(cursor)));
                pressedText = null;
                break;
            case Mode.MoveText: EndMoveText(); break;
            case Mode.ResizeText: EndResize(); break;
        }
        mode = Mode.None;
        UpdateCursor();
        Invalidate();
    }

    // Every raw mouse position since the last message, oldest first, in client DIPs.
    List<Vector2> CollectMoves(System.Drawing.Point clientPx)
    {
        // The desktop hook already sees every single mouse event, so there's nothing to recover.
        if (wallpaper) return [ToDip(clientPx)];
        var result = new List<Vector2>();
        var screen = PointToScreen(clientPx);
        var input = new Native.MOUSEMOVEPOINT
        {
            X = screen.X & 0xFFFF,
            Y = screen.Y & 0xFFFF,
            Time = (uint)Native.GetMessageTime(),
        };
        int n = Native.GetMouseMovePointsEx((uint)Marshal.SizeOf<Native.MOUSEMOVEPOINT>(), ref input,
            moveBuffer, moveBuffer.Length, Native.GMMP_USE_DISPLAY_POINTS);

        // Only trust the history when its newest point is this very message and it still contains
        // the last point we used. Otherwise (synthetic input, gaps) fall back to the message alone,
        // rather than risk splicing in positions from somewhere else.
        bool matches = n > 0 && moveBuffer[0].X == input.X && moveBuffer[0].Y == input.Y;
        if (matches && lastMove is { } last)
        {
            int end = -1;
            for (int i = 0; i < n; i++)
                if (moveBuffer[i].X == last.X && moveBuffer[i].Y == last.Y && moveBuffer[i].Time == last.Time) { end = i; break; }
            if (end < 0) end = 1;   // last point fell out of the history: just this message's point

            var origin = PointToScreen(System.Drawing.Point.Empty);
            for (int i = end - 1; i >= 0; i--)
            {
                // Display points wrap negative coordinates (monitors left of / above the main one).
                int x = moveBuffer[i].X > 32767 ? moveBuffer[i].X - 65536 : moveBuffer[i].X;
                int y = moveBuffer[i].Y > 32767 ? moveBuffer[i].Y - 65536 : moveBuffer[i].Y;
                result.Add(new Vector2(x - origin.X, y - origin.Y) / DpiScale);
            }
        }
        lastMove = matches ? moveBuffer[0] : null;
        if (result.Count == 0) result.Add(ToDip(clientPx));
        return result;
    }

    // ---------- pen ----------

    void BeginStroke()
    {
        CommitTextEdit();
        SettleZoom();
        coasting = false;
        mode = Mode.Draw;
        modeButton = MouseButtons.Left;
        lastMove = null;
        active = new Stroke
        {
            Color = Palette[colorIndex],
            // Pen size is constant on screen, so zoomed out you write finer in world terms.
            Size = ToolSizes[(int)Tool.Pen][sizeIndex[(int)Tool.Pen]] / zoom,
            Smoothing = PenSmoothing / zoom,
        };
        active.Points.Add(ScreenToWorld(cursor));
        activeDirty = true;
        Invalidate();
    }

    void AddStrokePoint(Vector2 dip)
    {
        if (active == null) return;
        var w = ScreenToWorld(dip);
        if (w == active.Points[^1]) return;
        active.Points.Add(w);
        activeDirty = true;
    }

    void EndStroke(System.Drawing.Point? upLocation)
    {
        if (active == null) return;
        if (upLocation is { } up)
            foreach (var p in CollectMoves(up)) AddStrokePoint(p);
        Ink.Finish(factory, active);
        board.Add(active);
        AddToCache(active);
        active = null;
        activeGeometry?.Dispose();
        activeGeometry = null;
    }

    // ---------- eraser ----------

    void BeginErase(MouseButtons button)
    {
        CommitTextEdit();
        SettleZoom();
        coasting = false;
        mode = Mode.Erase;
        modeButton = button;
        eraseWorking = board.Items.ToList();
        eraseCopies.Clear();
        eraseChanged = false;
        lastErase = cursor;
        EraseSegment(cursor, cursor);
        Invalidate();
    }

    void EraseAlong(Vector2 to)
    {
        EraseSegment(lastErase, to);
        lastErase = to;
    }

    // Cuts the eraser's exact shape, swept from one mouse position to the next, out of the strokes
    // under it. Strokes are copied the first time they're touched, so the originals stay intact
    // for undo. Text is never erased; you delete it by emptying the text box.
    void EraseSegment(Vector2 fromDip, Vector2 toDip)
    {
        var a = ScreenToWorld(fromDip);
        var b = ScreenToWorld(toDip);
        float radius = EraserRadius / zoom;
        var items = eraseWorking!;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            switch (items[i])
            {

                case Stroke s when eraseCopies.Contains(s):
                    if (Ink.Erase(factory, s, a, b, radius)) eraseChanged = cacheDirty = true;
                    break;
                case Stroke s when Ink.Touches(factory, s, a, b, radius):
                    var copy = s.CopyForErasing();
                    Ink.Finish(factory, copy);
                    Ink.Erase(factory, copy, a, b, radius);
                    items[i] = copy;
                    eraseCopies.Add(copy);
                    eraseChanged = cacheDirty = true;
                    break;
            }
        }
    }

    void EndErase()
    {
        if (eraseChanged && eraseWorking != null)
        {
            // Strokes rubbed out down to a few crumbs are removed entirely.
            foreach (var s in eraseCopies)
            {
                float crumb = s.Size * 0.35f;
                if (Ink.Area(s) >= crumb * crumb) continue;
                eraseWorking.Remove(s);
                s.Geometry?.Dispose();
            }
            board.Commit(eraseWorking);
        }
        eraseWorking = null;
        eraseCopies.Clear();
        cacheDirty = true;
    }
    // ---------- text ----------

    TextItem? TextAt(Vector2 world)
    {
        float pad = 6 / zoom;
        for (int i = board.Items.Count - 1; i >= 0; i--)
            if (board.Items[i] is TextItem t && RectangleF.Inflate(t.Bounds, pad, pad).Contains(world.X, world.Y))
                return t;
        return null;
    }

    void TextMouseDown()
    {
        var world = ScreenToWorld(cursor);
        var target = editing ?? hoverText;
        var handle = HandleAt(target, cursor);
        if (handle != Grip.None)
        {
            BeginResize(target!, handle);
            return;
        }
        if (editing != null)
        {
            float pad = 6 / zoom;
            if (RectangleF.Inflate(editing.Bounds, pad, pad).Contains(world.X, world.Y))
            {
                caret = textInk.IndexAt(editing, world);
                RestartCaretBlink();
                Invalidate();
            }
            else CommitTextEdit();   // clicking elsewhere finishes the text; the next click starts a new one
            return;
        }

        var hit = TextAt(world);
        if (hit != null)
        {
            // Click opens it for editing, drag moves it; which one is decided once the mouse moves or lifts.
            mode = Mode.PressText;
            modeButton = MouseButtons.Left;
            pressedText = hit;
            pressStart = cursor;
            return;
        }

        var t = new TextItem
        {
            Color = Palette[colorIndex],
            FontSize = ToolSizes[(int)Tool.Text][sizeIndex[(int)Tool.Text]] / zoom,
            Position = world,
        };
        textInk.Finish(t);
        // Centre the first line on the click so the caret appears right where you clicked.
        t.Position.Y -= t.Bounds.Height / 2;
        textInk.Finish(t);
        BeginEditing(t, original: null, caretAt: 0);
    }

    void StartEdit(TextItem original, int caretAt)
    {
        var copy = original.Copy();
        textInk.Finish(copy);
        BeginEditing(copy, original, caretAt);
    }

    void BeginEditing(TextItem working, TextItem? original, int caretAt)
    {
        editing = working;
        editOriginal = original;
        caret = Math.Clamp(caretAt, 0, working.Text.Length);
        editHistory.Clear();
        hoverText = null;
        HiddenInCache = original;
        cacheDirty = true;
        EditingChanged();
        RestartCaretBlink();
        UpdateCursor();
        Invalidate();
    }

    void CommitTextEdit()
    {
        if (editing == null) return;
        var t = editing;
        var original = editOriginal;
        editing = null;
        editOriginal = null;
        HiddenInCache = null;
        caretTimer.Stop();
        cacheDirty = true;
        EditingChanged();

        bool empty = string.IsNullOrWhiteSpace(t.Text);
        if (original == null)
        {
            if (!empty) board.Add(t);
            else t.Layout?.Dispose();
        }
        else if (empty) board.Replace(original, null);
        else if (t.Text != original.Text || t.Color != original.Color || t.FontSize != original.FontSize ||
                 t.Width != original.Width || t.Position != original.Position)
            board.Replace(original, t);
        else t.Layout?.Dispose();
        Invalidate();
    }

    void RestartCaretBlink()
    {
        caretOn = true;
        caretTimer.Stop();
        caretTimer.Start();
    }

    void SetEditText(string text, int newCaret, bool record = true)
    {
        if (editing == null) return;
        if (record) editHistory.Push((editing.Text, caret));
        editing.Text = text;
        caret = Math.Clamp(newCaret, 0, text.Length);
        textInk.Finish(editing);
        RestartCaretBlink();
        Invalidate();
    }

    void InsertText(string s)
    {
        if (editing == null) return;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        s = new string(s.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray());
        // Text is only ever drawn, never run or opened, but a huge paste would freeze the layout.
        int room = BoardStore.MaxTextLength - editing.Text.Length;
        if (s.Length > room) s = s[..Math.Max(0, room)];
        if (s.Length == 0) return;
        SetEditText(editing.Text.Insert(caret, s), caret + s.Length);
    }

    void MoveCaret(int index)
    {
        caret = Math.Clamp(index, 0, editing!.Text.Length);
        RestartCaretBlink();
        Invalidate();
    }

    // Step over a whole emoji (two UTF-16 chars) instead of splitting it.
    int PrevIndex(int i) => i >= 2 && char.IsLowSurrogate(editing!.Text[i - 1]) ? i - 2 : Math.Max(0, i - 1);
    int NextIndex(int i) => i + 2 <= editing!.Text.Length && char.IsHighSurrogate(editing.Text[i]) ? i + 2 : Math.Min(editing.Text.Length, i + 1);

    void MoveCaretVertically(int lines)
    {
        var (top, height) = textInk.Caret(editing!, caret);
        var target = top + new Vector2(0, height * (lines > 0 ? 1.5f : -0.5f));
        if (target.Y < editing!.Bounds.Top || target.Y > editing.Bounds.Bottom) return;
        MoveCaret(textInk.IndexAt(editing, target));
    }

    void BeginMoveText()
    {
        if (pressedText == null) return;
        mode = Mode.MoveText;
        movingText = pressedText.Copy();
        textInk.Finish(movingText);
        HiddenInCache = pressedText;
        cacheDirty = true;
        SetPointer(Cursors.SizeAll);
    }

    void MoveText()
    {
        if (movingText == null || pressedText == null) return;
        movingText.Position = pressedText.Position + (cursor - pressStart) / zoom;
        movingText.Bounds.Location = new PointF(movingText.Position.X, movingText.Position.Y);
    }

    void EndMoveText()
    {
        if (movingText != null && pressedText != null) board.Replace(pressedText, movingText);
        movingText = null;
        pressedText = null;
        HiddenInCache = null;
        cacheDirty = true;
    }

    void BeginResize(TextItem target, Grip handle)
    {
        SettleZoom();
        coasting = false;
        if (target == editing)
        {
            resizeItem = editing;
            resizeOriginal = null;
        }
        else
        {
            resizeOriginal = target;
            resizeItem = target.Copy();
            textInk.Finish(resizeItem);
            HiddenInCache = target;
            cacheDirty = true;
        }
        resizeHandle = handle;
        resizeStartBounds = resizeItem.Bounds;
        resizeStartPosition = resizeItem.Position;
        resizeStartFontSize = resizeItem.FontSize;
        resizeStartWidth = resizeItem.Width;
        mode = Mode.ResizeText;
        modeButton = MouseButtons.Left;
        Invalidate();
    }

    void UpdateResize()
    {
        var t = resizeItem!;
        var w = ScreenToWorld(cursor);
        var b = resizeStartBounds;
        if (resizeHandle is Grip.Left or Grip.Right)
        {
            // Side edges set the line width; the text wraps to fit and the box grows downwards.
            float minWidth = resizeStartFontSize * 1.2f;
            if (resizeHandle == Grip.Right)
            {
                t.Width = MathF.Max(minWidth, w.X - b.Left);
            }
            else
            {
                float left = MathF.Min(w.X, b.Right - minWidth);
                t.Width = b.Right - left;
                t.Position = new Vector2(left, resizeStartPosition.Y);
            }
        }
        else
        {
            // Corners scale everything around the opposite corner, which stays put.
            var corner = resizeHandle switch
            {
                Grip.TopLeft => new Vector2(b.Left, b.Top),
                Grip.TopRight => new Vector2(b.Right, b.Top),
                Grip.BottomLeft => new Vector2(b.Left, b.Bottom),
                _ => new Vector2(b.Right, b.Bottom),
            };
            var anchor = new Vector2(b.Left + b.Right, b.Top + b.Bottom) - corner;
            var diagonal = corner - anchor;
            float ratio = Vector2.Dot(w - anchor, diagonal) / diagonal.LengthSquared();
            // Keep the text between 8 and 300 DIPs tall on screen.
            float screenSize = resizeStartFontSize * zoom;
            ratio = Math.Clamp(ratio, 8 / screenSize, 300 / screenSize);
            t.FontSize = resizeStartFontSize * ratio;
            t.Width = resizeStartWidth * ratio;
            t.Position = anchor + (resizeStartPosition - anchor) * ratio;
        }
        textInk.Finish(t);
    }

    void EndResize()
    {
        if (resizeOriginal != null && resizeItem != null)
        {
            board.Replace(resizeOriginal, resizeItem);
            hoverText = resizeItem;
            HiddenInCache = null;
            cacheDirty = true;
        }
        else if (editing != null) RestartCaretBlink();
        resizeItem = null;
        resizeOriginal = null;
    }

    void PasteAtCursor()
    {
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (ExternalException) { }   // another app is holding the clipboard
        if (string.IsNullOrEmpty(text)) return;

        if (editing != null) { InsertText(text); return; }
        if (tool != Tool.Text) SetTool(Tool.Text);
        var t = new TextItem
        {
            Color = Palette[colorIndex],
            FontSize = ToolSizes[(int)Tool.Text][sizeIndex[(int)Tool.Text]] / zoom,
            Position = ScreenToWorld(cursor),
        };
        textInk.Finish(t);
        BeginEditing(t, original: null, caretAt: 0);
        InsertText(text);
        editHistory.Clear();
    }

    // ---------- panning ----------

    void BeginPan(MouseButtons button)
    {
        SettleZoom();
        coasting = false;
        mode = Mode.Pan;
        modeButton = button;
        panLast = cursor;
        panSamples.Clear();
        RecordPanSample();
        SetPointer(Cursors.SizeAll);
    }

    void RecordPanSample()
    {
        long now = Stopwatch.GetTimestamp();
        panSamples.Enqueue((now, offset));
        while (panSamples.Count > 2 && Stopwatch.GetElapsedTime(panSamples.Peek().Tick, now).TotalMilliseconds > 80)
            panSamples.Dequeue();
    }

    void EndPan()
    {
        if (panSamples.Count < 2) return;
        var first = panSamples.Peek();
        long now = Stopwatch.GetTimestamp();
        // If the mouse had stopped before release, don't fling.
        var lastSample = panSamples.Last();
        if (Stopwatch.GetElapsedTime(lastSample.Tick, now).TotalMilliseconds > 40) return;
        float span = (float)Stopwatch.GetElapsedTime(first.Tick, lastSample.Tick).TotalSeconds;
        if (span <= 0.005f) return;
        panVelocity = (lastSample.Offset - first.Offset) / span;
        if (panVelocity.Length() < 80) return;
        coasting = true;
        StartAnimation();
    }

    // ---------- keyboard: typing into a text box, plus Ctrl+Z / Ctrl+V ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (wallpaper || !HandleKey(e.KeyCode, e.Control, e.Shift)) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (wallpaper || editing == null || char.IsControl(e.KeyChar)) return;
        InsertText(e.KeyChar.ToString());
        e.Handled = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Typing only ever goes to the board while you're actively on it.
        if (!wallpaper) CommitTextEdit();
    }

    // Editing and shortcut keys. Returns true if the key was used.
    bool HandleKey(Keys key, bool control, bool shift)
    {
        if (editing != null)
        {
            var text = editing.Text;
            switch (key)
            {
                case Keys.Back when caret > 0:
                    int from = PrevIndex(caret);
                    SetEditText(text.Remove(from, caret - from), from);
                    return true;
                case Keys.Delete when caret < text.Length:
                    SetEditText(text.Remove(caret, NextIndex(caret) - caret), caret);
                    return true;
                case Keys.Left: MoveCaret(PrevIndex(caret)); return true;
                case Keys.Right: MoveCaret(NextIndex(caret)); return true;
                case Keys.Up: MoveCaretVertically(-1); return true;
                case Keys.Down: MoveCaretVertically(1); return true;
                case Keys.Home: MoveCaret(text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1); return true;
                case Keys.End:
                    int end = text.IndexOf('\n', caret);
                    MoveCaret(end < 0 ? text.Length : end);
                    return true;
                case Keys.Enter: InsertText("\n"); return true;
                case Keys.Escape: CommitTextEdit(); return true;
                case Keys.V when control: PasteAtCursor(); return true;
                case Keys.Z when control: UndoAction(); return true;
                case Keys.Back or Keys.Delete: return true;
                default: return false;
            }
        }
        switch (key)
        {
            case Keys.Z when control && shift:
            case Keys.Y when control:
                RedoAction();
                return true;
            case Keys.Z when control: UndoAction(); return true;
            case Keys.V when control: PasteAtCursor(); return true;
            default: return false;
        }
    }
    // ---------- colours ----------

    static Color4 Argb(uint c) =>
        new(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, ((c >> 24) & 0xFF) / 255f);

    static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, a);

    static DRect ToDRect(RectangleF r) => new(r.X, r.Y, r.Width, r.Height);
}
