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
    // Pen, Eraser, Text and Highlighter come first: they index ToolSizes.
    enum Tool { Pen, Eraser, Text, Highlighter, Desktop, Lasso }

    static readonly uint[] Palette = [0xFF1E1E1E, 0xFF1971C2, 0xFFE03131, 0xFF2F9E44, 0xFFF08C00];
    // Yellow, green, pink, blue, orange: a black highlighter would be no use.
    static readonly uint[] HighlightPalette = [0xFFFFD400, 0xFF40DC6E, 0xFFFF5C8D, 0xFF4DABF7, 0xFFFF922B];
    // Per tool, five sizes in screen DIPs: pen width, eraser radius, text height, highlighter width.
    static readonly float[][] ToolSizes = [[3, 5, 8, 13, 20], [6, 10, 16, 26, 40], [16, 22, 30, 40, 56], [10, 16, 24, 34, 48]];

    const float DotSpacing = 24;     // world units at zoom 1
    // Screen DIPs: mouse wiggles shorter than about this get ironed out of the line. Measured on
    // screen, so it feels the same at every zoom.
    const float PenSmoothing = 2f;
    // 100% is as far out as it goes: the board is exactly your screen (see BoardForm.Bounds.cs).
    const float MinZoom = 1f, MaxZoom = 8f;
    const string IconFont = "Segoe Fluent Icons";
    const string PenIcon = "\uE70F", EraserIcon = "\uE75C", TextIcon = "\uE8D2", UndoIcon = "\uE7A7", RedoIcon = "\uE7A6";
    const string DesktopIcon = "\uE8B0", HideIcon = "\uE70D", ExpandIcon = "\uE70E", TrashIcon = "\uE74D";
    const string LassoIcon = "";     // drawn by DrawLassoIcon
    const string HighlighterIcon = "\uED64";
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
    int highlightColorIndex;         // the highlighter keeps its own colour
    readonly int[] sizeIndex = [1, 1, 1, 1];

    enum Mode { None, Draw, Erase, Pan, PressText, MoveText, ResizeText, Slide, SelectText, Lasso, MovePicked }
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
    int anchor;                      // the other end of the selection; equal to caret when nothing is selected
    readonly List<(string Text, int Caret)> editHistory = new();   // capped, see SetEditText
    readonly List<(string Text, int Caret)> editRedo = new();
    long lastTextClick;              // for double-click to select a word
    Vector2 lastTextClickAt;
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

    // Grab handles on a text box: corners scale the text, the side edges set where lines wrap,
    // and while typing, a grip above (or below) the box moves it.
    enum Grip { None, TopLeft, TopRight, BottomLeft, BottomRight, Left, Right, Move }
    bool moveGripBelow;              // no room above the box on its screen; only changes when not dragging
    Vector2 resizeStartCursor;       // world point where a resize or move started
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
    RectangleF toolbarRect;          // while hidden: the mini bar
    bool toolbarHidden;

    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1500 };
    string? notice;                  // shown across the bottom of the board (save trouble, an unreadable board...)
    bool saveFailed;
    bool saveAfterLoad;
    readonly System.Windows.Forms.Timer noticeTimer = new();
    int renderFailures;

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
        patternStrength = float.IsFinite(settings.PatternStrength) ? Math.Clamp(settings.PatternStrength, 0, 1) : 0.5f;
        toolbarHidden = settings.ToolbarHidden;
        zoomLocked = settings.ZoomLocked;
        showDesktopImage = settings.ShowWallpaper;
        imageVeil = float.IsFinite(settings.WallpaperVeil) ? Math.Clamp(settings.WallpaperVeil, 0, 1) : 0.6f;
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
        CreateLassoResources();

        LoadBoard();
        board.Changed += () =>
        {
            contentVersion++;
            picked = null;   // the lasso's picks may be gone or replaced now (undo, erasing...)
            saveTimer.Stop();
            saveTimer.Start();
        };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveBoard(); };
        caretTimer.Tick += (_, _) => { caretOn = !caretOn; Invalidate(); };
        noticeTimer.Tick += (_, _) => HideNotice();
        StartDesktopImageWatch();
        if (saveAfterLoad) saveTimer.Start();
    }

    // ---------- persistence ----------

    void LoadBoard()
    {
        var data = BoardStore.Load();
        offset = new Vector2(data.ViewX, data.ViewY);
        zoom = targetZoom = Math.Clamp(data.Zoom, MinZoom, MaxZoom);
        if (zoomLocked) { zoom = targetZoom = 1; offset = Vector2.Zero; }
        board.Items.AddRange(LoadItems(data.Items, out bool cleaned));
        // Erased ink found in the saved board is gone now; save soon so the file is clean too.
        saveAfterLoad = cleaned;
        if (BoardStore.Problem != null) ShowNotice(BoardStore.Problem, seconds: 30);
    }

    // Turns saved entries into board items. Entries that can't be read are skipped rather than
    // stopping the whole board from loading, and erased ink is removed for good (see Ink.Bake),
    // which also cleans boards saved before that existed.
    List<Item> LoadItems(IEnumerable<BoardStore.SavedItem?> saved, out bool cleaned)
    {
        var items = new List<Item>();
        cleaned = false;
        foreach (var entry in saved)
        {
            try
            {
                var item = BoardStore.ToItem(entry);
                if (item == null) continue;
                Finish(item);
                if (item is not Stroke { Erasures.Count: > 0 } stroke) { items.Add(item); continue; }
                var pieces = Ink.Bake(factory, stroke);
                if (pieces.Count != 1 || pieces[0] != stroke)
                {
                    cleaned = true;
                    stroke.Release();
                }
                items.AddRange(pieces);
            }
            catch (Exception ex)
            {
                Log.Write($"skipped a board item: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return items;
    }

    void SaveBoard()
    {
        try
        {
            BoardStore.Save(board, offset, zoom);
            if (saveFailed) { saveFailed = false; HideNotice(); }
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the board keeps working; it just says so until a save succeeds.
            Log.Write($"save error: {ex.GetType().Name}: {ex.Message}");
            saveFailed = true;
            ShowNotice($"Couldn't save the board: {ex.Message}", seconds: 0);
        }
    }

    // A message across the bottom of the board. seconds = 0 keeps it until it's replaced or hidden.
    void ShowNotice(string text, int seconds)
    {
        notice = text;
        noticeTimer.Stop();
        if (seconds > 0)
        {
            noticeTimer.Interval = seconds * 1000;
            noticeTimer.Start();
        }
        Invalidate();
    }

    void HideNotice()
    {
        noticeTimer.Stop();
        notice = null;
        Invalidate();
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
        ReleaseResources();
        base.OnFormClosed(e);
    }

    bool released;

    void ReleaseResources()
    {
        if (released) return;
        released = true;
        caretTimer.Dispose();
        noticeTimer.Dispose();
        saveTimer.Dispose();
        StopDesktopImageWatch();
        DiscardDevice();
        activeGeometry?.Dispose();
        board.ReleaseAll();
        textInk.Dispose();
        iconFont.Dispose();
        foreach (var f in sizeLetterFonts) f.Dispose();
        bannerFont.Dispose();
        DisposeMenuFonts();
        dashStyle?.Dispose();
        dwrite.Dispose();
        factory.Dispose();
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
        desktopImageLayer?.Dispose(); desktopImageLayer = null;
        desktopImageDirty = true;
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
        // The screen layout may have changed: read the wallpaper's monitors again.
        desktopImageLayer?.Dispose(); desktopImageLayer = null;
        desktopImageDirty = true;
        desktopImageInfo = null;
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
        try
        {
            Render();
            renderFailures = 0;
        }
        catch (Exception ex)
        {
            // Windows Forms stops painting a window for good after an exception in OnPaint. Start
            // the graphics over instead and try again (a few times, so a lasting error can't spin).
            Log.Write($"render error: {ex.GetType().Name}: {ex.Message}");
            DiscardDevice();
            if (++renderFailures <= 3) Invalidate();
            return;
        }
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
        EnsureDesktopImageLayer();
        if (rt == null) { Invalidate(); return; }   // the device was lost painting the wallpaper
        if (cache == null)
        {
            cache = rt!.CreateCompatibleRenderTarget(null, null, null, CompatibleRenderTargetOptions.None);
            cache.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            cacheDirty = true;
        }
        bool scalePicture = zooming;   // still zooming after this frame's animation step
        if (scalePicture) EnsureBoardPicture();
        else if (cacheDirty) RebuildCache();

        // Losing the graphics device while painting the cache throws everything away: start over.
        if (rt == null || cache == null || (scalePicture && boardPicture == null))
        {
            Invalidate();
            return;
        }

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
            // The wallpaper stays put; only the board on top of it (see-through then) zooms.
            if (ShowingDesktopImage) DrawDesktopImage(r);
            r.Transform = ViewTransform;
            r.DrawBitmap(boardPicture!.Bitmap, 1f, BitmapInterpolationMode.Linear);
            r.Transform = Matrix3x2.Identity;
        }
        else r.DrawBitmap(cache.Bitmap, 1f, BitmapInterpolationMode.NearestNeighbor);

        if (active != null && activeGeometry != null)
        {
            r.Transform = ViewTransform;
            brush!.Color = InkColor(active);
            r.FillGeometry(activeGeometry, brush);
            r.Transform = Matrix3x2.Identity;
        }

        DrawTextOverlay(r);
        DrawLasso(r);
        DrawEraserCursor(r);
        DrawToolbar(r);
        DrawMenu(r);
        if (notice != null) DrawBanner(r, notice);

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
        bool painted = PaintBoard(boardPicture, seeThrough: ShowingDesktopImage);
        (offset, zoom) = (viewOffset, viewZoom);
        if (painted) boardPictureVersion = contentVersion;
    }

    // Background, pattern and every visible item, at the current view. False if the device was lost.
    // seeThrough leaves out the background (the wallpaper goes under it separately).
    bool PaintBoard(ID2D1BitmapRenderTarget c, bool seeThrough = false)
    {
        c.BeginDraw();
        c.Transform = Matrix3x2.Identity;
        c.Clear(seeThrough ? new Color4(0, 0, 0, 0) : Colors.Background);
        if (!seeThrough && ShowingDesktopImage) DrawDesktopImage(c);
        DrawBackdrop(c);

        var view = VisibleWorldRect();
        foreach (var item in PaintOrder(eraseWorking ?? board.Items))
        {
            if (item == HiddenInCache || pickedHidden.Contains(item) || !item.Bounds.IntersectsWith(view)) continue;
            DrawItem(c, item);
        }
        c.Transform = Matrix3x2.Identity;
        if (c.EndDraw().Failure) { DiscardDevice(); return false; }
        return true;
    }

    // Highlights first, so they lie under the other ink and text whenever they were drawn.
    static IEnumerable<Item> PaintOrder(List<Item> items) =>
        items.Where(i => i is Stroke { Highlight: true }).Concat(items.Where(i => i is not Stroke { Highlight: true }));

    Color4 InkColor(Item item)
    {
        var color = Argb(Colors.Display(item.Color));
        return item is Stroke { Highlight: true } ? WithAlpha(color, Colors.HighlightAlpha) : color;
    }

    // moved: an extra world-space transform, for items being dragged with the lasso.
    void DrawItem(ID2D1RenderTarget target, Item item, Matrix3x2? moved = null)
    {
        brush!.Color = InkColor(item);
        var view = moved is { } m ? m * ViewTransform : ViewTransform;
        switch (item)
        {
            case Stroke { Geometry: { } geometry }:
                target.Transform = view;
                target.FillGeometry(geometry, brush);
                break;
            case TextItem { Layout: { } layout } t:
                target.Transform = Matrix3x2.CreateScale(t.Scale) * Matrix3x2.CreateTranslation(t.Position) * view;
                target.DrawTextLayout(Vector2.Zero, layout, brush, DrawTextOptions.NoSnap);
                break;
        }
        target.Transform = Matrix3x2.Identity;
    }

    // A new stroke only needs painting on top of the cache, not a full rebuild.
    void AddToCache(Item item)
    {
        // A highlight goes under the ink already there, so that needs a full repaint.
        if (cache == null || cacheDirty || item is Stroke { Highlight: true }) { cacheDirty = true; return; }
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
            var color = PatternColor(1);
            if (!coarsePass)
            {
                if (fade <= 0.01f) break;
                color = WithAlpha(color, color.A * fade);
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
            if (anchor != caret)
            {
                brush!.Color = WithAlpha(Colors.Accent, dark ? 0.32f : 0.22f);
                foreach (var rect in textInk.SelectionRects(editing, Math.Min(anchor, caret), Math.Max(anchor, caret)))
                    r.FillRectangle(ToDRect(ToScreen(rect, 0)), brush);
            }
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
        if (hoverText != null && hoverText != editOriginal && tool == Tool.Text && mode == Mode.None && movingText == null)
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
            if (handle == Grip.Move)
            {
                DrawMoveGrip(r, box, p, color);
                continue;
            }
            var rect = handle is Grip.Left or Grip.Right
                ? new RectangleF(p.X - 2.5f, p.Y - 8, 5, 16)
                : new RectangleF(p.X - 4, p.Y - 4, 8, 8);
            brush.Color = Colors.Panel;
            r.FillRoundedRectangle(new RoundedRectangle(rect, 2.5f, 2.5f), brush);
            brush.Color = color;
            r.DrawRoundedRectangle(new RoundedRectangle(rect, 2.5f, 2.5f), brush, 1.2f);
        }
    }

    // A little pill with a dot pattern, on a short stem from the box.
    void DrawMoveGrip(ID2D1RenderTarget r, RectangleF box, Vector2 p, Color4 color)
    {
        const float w = 26, h = 16;
        brush!.Color = color;
        float edge = p.Y < box.Top ? box.Top : box.Bottom;
        float end = p.Y < box.Top ? p.Y + h / 2 : p.Y - h / 2;
        r.DrawLine(new Vector2(p.X, edge), new Vector2(p.X, end), brush, 1.2f);
        var pill = new RoundedRectangle(new RectangleF(p.X - w / 2, p.Y - h / 2, w, h), h / 2, h / 2);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(pill, brush);
        brush.Color = color;
        r.DrawRoundedRectangle(pill, brush, 1.2f);
        for (int i = -1; i <= 1; i++)
            for (int j = 0; j < 2; j++)
                r.FillEllipse(new Ellipse(new Vector2(p.X + i * 5, p.Y - 2.5f + j * 5), 1.3f, 1.3f), brush);
    }

    // Handle positions in screen DIPs, on the corners and side midpoints of the text's box, plus
    // the move grip while it's being typed in.
    (Grip, Vector2)[] HandlePoints(TextItem t)
    {
        var b = ToScreen(t.Bounds, 6);
        float midY = (b.Top + b.Bottom) / 2;
        (Grip, Vector2)[] edges =
        [
            (Grip.TopLeft, new(b.Left, b.Top)), (Grip.TopRight, new(b.Right, b.Top)),
            (Grip.BottomLeft, new(b.Left, b.Bottom)), (Grip.BottomRight, new(b.Right, b.Bottom)),
            (Grip.Left, new(b.Left, midY)), (Grip.Right, new(b.Right, midY)),
        ];
        return t == editing ? [.. edges, (Grip.Move, MoveGripPoint(b))] : edges;
    }

    // Above the box, or below it when the box is too close to the top of its screen to grab
    // it there. It doesn't flip mid-drag, so it never jumps out from under the mouse.
    Vector2 MoveGripPoint(RectangleF box)
    {
        const float reach = 22;   // box edge to the grip's centre
        float x = (box.Left + box.Right) / 2;
        if (!(mode == Mode.ResizeText && resizeHandle == Grip.Move))
            moveGripBelow = box.Top - reach - 10 < ScreenTopAt(x, box.Top);
        return new Vector2(x, moveGripBelow ? box.Bottom + reach : box.Top - reach);
    }

    // The top of the usable screen area at this point, in client DIPs (monitors can sit at
    // different heights, and a taskbar can be at the top).
    float ScreenTopAt(float x, float y)
    {
        if (!wallpaper) return 0;
        var px = PointToScreen(new System.Drawing.Point((int)(x * DpiScale), (int)(y * DpiScale)));
        var work = Screen.FromPoint(px).WorkingArea;
        return PointToClient(new System.Drawing.Point(px.X, work.Top)).Y / DpiScale;
    }

    Grip HandleAt(TextItem? t, Vector2 screen)
    {
        if (t == null) return Grip.None;
        foreach (var (handle, p) in HandlePoints(t))
            if (Vector2.Distance(p, screen) <= (handle == Grip.Move ? 14 : 9)) return handle;
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
        var area = ToolbarArea;
        if (toolbarHidden)
        {
            DrawMiniToolbar(r, area);
            return;
        }
        const float h = 48, toolW = 40, swatch = 28, sizeSlot = 30, pad = 8, gap = 18;
        var tools = ToolButtons;
        float w = pad + tools.Length * toolW + gap + Palette.Length * swatch + gap + 5 * sizeSlot + gap + 2 * toolW + gap + 2 * toolW + pad;
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
            if (t == Tool.Lasso) DrawLassoIcon(r, rect, brush.Color);
            else r.DrawText(icon, iconFont, ToDRect(rect), brush);
            var chosen = t;
            toolbarButtons.Add((new RectangleF(cx, y, toolW, h), () => SetTool(chosen)));
            cx += toolW;
        }
        Divider(r, ref cx, y, h, gap);

        // Colours (the eraser has none, so they fade while it's selected; with the lasso they
        // recolour what it picked up)
        float colorAlpha = tool is Tool.Eraser or Tool.Desktop || (tool == Tool.Lasso && picked == null) ? 0.3f : 1f;
        // The highlighter shows its own colours.
        bool highlighter = tool == Tool.Highlighter;
        var palette = highlighter ? HighlightPalette : Palette;
        int chosenColor = highlighter ? highlightColorIndex : colorIndex;
        for (int i = 0; i < palette.Length; i++)
        {
            var center = new Vector2(cx + swatch / 2, cy);
            if (i == chosenColor && tool is Tool.Pen or Tool.Text or Tool.Highlighter)
            {
                brush.Color = WithAlpha(Argb(Colors.Display(palette[i])), 0.35f);
                r.DrawEllipse(new Ellipse(center, 12, 12), brush, 2f);
            }
            brush.Color = WithAlpha(Argb(Colors.Display(palette[i])), colorAlpha);
            r.FillEllipse(new Ellipse(center, 8, 8), brush);
            int index = i;
            toolbarButtons.Add((new RectangleF(cx, y, swatch, h), () => SetColor(index)));
            cx += swatch;
        }
        Divider(r, ref cx, y, h, gap);

        // Sizes for the current tool
        int ti = tool is Tool.Desktop or Tool.Lasso ? (int)Tool.Pen : (int)tool;   // these show the pen's sizes
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
                case Tool.Highlighter:
                    // A short see-through band as tall as the highlighter is wide (scaled down).
                    float band = MathF.Min(18, 3 + ToolSizes[ti][i] * 0.32f);
                    brush.Color = WithAlpha(Argb(Colors.Display(HighlightPalette[highlightColorIndex])), MathF.Min(1, Colors.HighlightAlpha * 1.6f));
                    r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(center.X - 9, center.Y - band / 2, 18, band), band / 2, band / 2), brush);
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
        toolbarButtons.Add((menuButtonRect, ToggleMenu));
        cx += toolW;

        // Hide: the toolbar shrinks to a small tab
        brush.Color = Colors.Icon;
        r.DrawText(HideIcon, iconFont, ToDRect(new RectangleF(cx + 3, y + 7, toolW - 6, h - 14)), brush);
        toolbarButtons.Add((new RectangleF(cx, y, toolW, h), HideToolbar));
    }

    // The hidden toolbar: a mini bar with just the arrow and the pen (so it's always clear which
    // one is on), plus a button that brings the full toolbar back.
    (Tool, string)[] MiniTools => ToolButtons.Where(b => b.Item1 is Tool.Desktop or Tool.Pen).ToArray();

    void DrawMiniToolbar(ID2D1RenderTarget r, RectangleF area)
    {
        const float h = 40, toolW = 36, pad = 5, gap = 10;
        var tools = MiniTools;
        float w = pad + tools.Length * toolW + gap + toolW + pad;
        float x = MathF.Round(area.Left + (area.Width - w) / 2), y = area.Bottom - h - 14;
        toolbarRect = new RectangleF(x, y, w, h);

        brush!.Color = Colors.PanelShadow;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y + 2, w, h), 12, 12), brush);
        brush.Color = Colors.Panel;
        r.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y, w, h), 12, 12), brush);
        brush.Color = Colors.PanelBorder;
        r.DrawRoundedRectangle(new RoundedRectangle(new RectangleF(x + 0.5f, y + 0.5f, w - 1, h - 1), 12, 12), brush, 1f);

        float cx = x + pad;
        foreach (var (t, icon) in tools)
        {
            var rect = new RectangleF(cx + 3, y + 5, toolW - 6, h - 10);
            bool selected = tool == t;
            if (selected)
            {
                brush.Color = WithAlpha(Colors.Accent, dark ? 0.16f : 0.13f);
                r.FillRoundedRectangle(new RoundedRectangle(rect, 8, 8), brush);
            }
            brush.Color = selected ? Colors.Accent : Colors.Icon;
            r.DrawText(icon, iconFont, ToDRect(rect), brush);
            var chosen = t;
            toolbarButtons.Add((new RectangleF(cx, y, toolW, h), () => SetTool(chosen)));
            cx += toolW;
        }
        Divider(r, ref cx, y, h, gap);

        brush.Color = Colors.Icon;
        r.DrawText(ExpandIcon, iconFont, ToDRect(new RectangleF(cx + 3, y + 5, toolW - 6, h - 10)), brush);
        toolbarButtons.Add((new RectangleF(cx, y, toolW, h), ShowToolbar));
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
        var items = LoadItems(data.Items, out _);
        // Replacing the board is a single undo step, so Undo brings back what was there before
        // (until Pawboard closes); the backup keeps it after that.
        SaveBoard();
        BoardStore.Backup();
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
        ClearPicked();
        tool = t;
        hoverText = null;
        UpdateCursor();
        Invalidate();
    }

    // The pen stays on when the toolbar shrinks; any other tool steps aside to the arrow (on the
    // wallpaper), since the mini bar couldn't show it.
    void HideToolbar()
    {
        CommitTextEdit();
        CloseMenu();
        toolbarHidden = true;
        if (!MiniTools.Any(b => b.Item1 == tool)) SetTool(MiniTools[0].Item1);
        SaveSettings();
        Invalidate();
    }

    void ShowToolbar()
    {
        toolbarHidden = false;
        SaveSettings();
        Invalidate();
    }

    void SetColor(int index)
    {
        if (tool == Tool.Highlighter)
        {
            highlightColorIndex = index;
            Invalidate();
            return;
        }
        colorIndex = index;
        if (tool == Tool.Lasso && picked != null)
        {
            RecolorPicked(index);
            return;
        }
        if (tool is Tool.Eraser or Tool.Desktop or Tool.Lasso) SetTool(Tool.Pen);   // picking a colour means you want to draw
        if (editing != null)
        {
            editing.Color = Palette[index];
            Invalidate();
        }
    }

    void SetSize(int index)
    {
        if (tool is Tool.Desktop or Tool.Lasso) SetTool(Tool.Pen);
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
            if (editHistory.Count > 0)
            {
                var (text, c) = editHistory[^1];
                editHistory.RemoveAt(editHistory.Count - 1);
                editRedo.Add((editing.Text, caret));
                SetEditText(text, c, record: false);
            }
            else CommitTextEdit();
            return;
        }
        if (mode == Mode.None && board.Undo()) cacheDirty = true;
        hoverText = null;
        Invalidate();
    }

    // Ctrl+Y in a text box: puts back typing that Ctrl+Z took out.
    void RedoTyping()
    {
        if (editing == null || editRedo.Count == 0) return;
        var (text, c) = editRedo[^1];
        editRedo.RemoveAt(editRedo.Count - 1);
        editHistory.Add((editing.Text, caret));
        SetEditText(text, c, record: false);
    }

    void RedoAction()
    {
        CommitTextEdit();
        if (mode == Mode.None && board.Redo()) cacheDirty = true;
        hoverText = null;
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
            Tool.Text when handle is Grip.Move => Cursors.SizeAll,
            Tool.Text when hoverText != null => Cursors.SizeAll,
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

        if (button == MouseButtons.Middle) { if (!zoomLocked) BeginPan(button); }
        else if (button == MouseButtons.Right) BeginErase(button);   // right-drag erases with any tool
        else if (button == MouseButtons.Left)
        {
            switch (tool)
            {
                case Tool.Pen or Tool.Highlighter: BeginStroke(); break;
                case Tool.Eraser: BeginErase(button); break;
                case Tool.Text: TextMouseDown(); break;
                case Tool.Lasso: LassoMouseDown(); break;
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
            case Mode.Slide:
                SlideTo(cursor.X);
                break;
            case Mode.SelectText:
                if (editing != null) MoveCaret(textInk.IndexAt(editing, ScreenToWorld(cursor)), extend: true);
                break;
            case Mode.Lasso:
                ExtendLasso();
                break;
            case Mode.MovePicked:
                DragPicked();
                break;
            default:
                if (tool == Tool.Eraser) Invalidate();   // the eraser circle follows the mouse
                if (tool == Tool.Text)
                {
                    // Shows what a click would pick up, also while typing in another text box.
                    // Keep the box while the mouse is on one of its handles, which stick out past the text.
                    var world = ScreenToWorld(cursor);
                    var hit = OverEditBox(world) ? null
                        : HandleAt(hoverText, cursor) != Grip.None ? hoverText : TextAt(world);
                    if (hit == editOriginal) hit = null;   // that's the one being typed in
                    if (hit != hoverText) { hoverText = hit; Invalidate(); }
                    var editGrip = HandleAt(editing, cursor);
                    hoverHandle = editGrip != Grip.None ? editGrip : HandleAt(hoverText, cursor);
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
        if (zoomLocked || (mode != Mode.None && mode != Mode.Pan)) return;
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
            case Mode.Slide: SaveSettings(); break;
            case Mode.Lasso: FinishLasso(); break;
            case Mode.MovePicked: FinishPickedDrag(); break;
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
        // The highlighter is a pen with its own colours and sizes, an even width, and twice the
        // smoothing: highlights should come out calm, not shaky.
        bool highlight = tool == Tool.Highlighter;
        var kind = highlight ? Tool.Highlighter : Tool.Pen;
        active = new Stroke
        {
            Color = highlight ? HighlightPalette[highlightColorIndex] : Palette[colorIndex],
            // Pen size is constant on screen, so zoomed out you write finer in world terms.
            Size = ToolSizes[(int)kind][sizeIndex[(int)kind]] / zoom,
            Smoothing = (highlight ? 2 : 1) * PenSmoothing / zoom,
            Highlight = highlight,
        };
        active.Points.Add(ScreenToWorld(cursor));
        activeDirty = true;
        Invalidate();
    }

    void AddStrokePoint(Vector2 dip)
    {
        if (active == null || active.Points.Count >= 200_000) return;
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
            for (int i = eraseWorking.Count - 1; i >= 0; i--)
            {
                if (eraseWorking[i] is not Stroke s || !eraseCopies.Contains(s)) continue;
                var pieces = Ink.Bake(factory, s);
                if (pieces.Count == 1 && pieces[0] == s) continue;
                s.Release();
                eraseWorking.RemoveAt(i);
                eraseWorking.InsertRange(i, pieces);
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

    // While typing: on the text box itself or one of its handles (the box being typed in comes
    // first, even where it overlaps another text).
    bool OverEditBox(Vector2 world)
    {
        if (editing == null) return false;
        float pad = 6 / zoom;
        return RectangleF.Inflate(editing.Bounds, pad, pad).Contains(world.X, world.Y) || HandleAt(editing, cursor) != Grip.None;
    }

    void TextMouseDown()
    {
        var world = ScreenToWorld(cursor);
        if (editing != null)
        {
            var editHandle = HandleAt(editing, cursor);
            if (editHandle != Grip.None)
            {
                BeginResize(editing, editHandle);
                return;
            }
            if (OverEditBox(world))
            {
                int at = textInk.IndexAt(editing, world);
                long now = Environment.TickCount64;
                var near = SystemInformation.DoubleClickSize;
                bool doubleClick = now - lastTextClick <= SystemInformation.DoubleClickTime &&
                    MathF.Abs(cursor.X - lastTextClickAt.X) <= near.Width && MathF.Abs(cursor.Y - lastTextClickAt.Y) <= near.Height;
                lastTextClick = doubleClick ? 0 : now;   // a third click starts over
                lastTextClickAt = cursor;
                if (doubleClick) SelectWordAt(at);
                else
                {
                    // Click puts the caret there (Shift+click selects up to it); dragging selects.
                    MoveCaret(at, extend: InputHooks.IsDown(0x10));
                    mode = Mode.SelectText;
                    modeButton = MouseButtons.Left;
                }
                RestartCaretBlink();
                Invalidate();
                return;
            }
            // Clicking elsewhere finishes the text. On another text, the same click goes on to
            // pick that one up; on empty space it only finishes (the next click starts a new one).
            CommitTextEdit();
            if (hoverText == null) return;
        }

        var handle = HandleAt(hoverText, cursor);
        if (handle != Grip.None)
        {
            BeginResize(hoverText!, handle);
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
        caret = anchor = Math.Clamp(caretAt, 0, working.Text.Length);
        editHistory.Clear();
        editRedo.Clear();
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
        if (mode == Mode.ResizeText && resizeOriginal == null)
        {
            mode = Mode.None;
            resizeItem = null;
        }
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
        if (record)
        {
            editHistory.Add((editing.Text, caret));
            if (editHistory.Count > 200) editHistory.RemoveAt(0);
            editRedo.Clear();
        }
        editing.Text = text;
        caret = anchor = Math.Clamp(newCaret, 0, text.Length);
        textInk.Finish(editing);
        RestartCaretBlink();
        Invalidate();
    }

    (int Start, int End) Selection => (Math.Min(anchor, caret), Math.Max(anchor, caret));

    // Types (or pastes) over the selection, if there is one.
    void InsertText(string s)
    {
        if (editing == null) return;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        s = new string(s.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray());
        var (start, end) = Selection;
        // Text is only ever drawn, never run or opened, but a huge paste would freeze the layout.
        int room = BoardStore.MaxTextLength - (editing.Text.Length - (end - start));
        if (s.Length > room) s = s[..Math.Max(0, room)];
        if (s.Length == 0 && start == end) return;
        SetEditText(editing.Text.Remove(start, end - start).Insert(start, s), start + s.Length);
    }

    // Removes the selected text. False if nothing was selected.
    bool DeleteSelection()
    {
        var (start, end) = Selection;
        if (start == end) return false;
        SetEditText(editing!.Text.Remove(start, end - start), start);
        return true;
    }

    // Moves the caret; with extend (Shift held), the selection stretches along with it.
    void MoveCaret(int index, bool extend = false)
    {
        caret = Math.Clamp(index, 0, editing!.Text.Length);
        if (!extend) anchor = caret;
        RestartCaretBlink();
        Invalidate();
    }

    void SelectAll()
    {
        anchor = 0;
        MoveCaret(editing!.Text.Length, extend: true);
    }

    void SelectWordAt(int index)
    {
        var text = editing!.Text;
        int start = index, end = index;
        if (index < text.Length && IsWordChar(text[index]) || index > 0 && IsWordChar(text[index - 1]))
        {
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            while (end < text.Length && IsWordChar(text[end])) end++;
        }
        else if (index < text.Length) end = NextIndex(index);
        anchor = start;
        MoveCaret(end, extend: true);
    }

    void CopySelection()
    {
        var (start, end) = Selection;
        if (start == end) return;
        try { Clipboard.SetText(editing!.Text[start..end]); }
        catch (ExternalException) { }   // another app is holding the clipboard
    }

    // Letters, digits and emoji count as a word; spaces and punctuation separate words.
    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;

    // Ctrl+Left: to the start of this word, or of the one before.
    int WordLeft(int i)
    {
        var text = editing!.Text;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        if (i > 0 && IsWordChar(text[i - 1])) { while (i > 0 && IsWordChar(text[i - 1])) i--; }
        else if (i > 0) i = PrevIndex(i);
        return i;
    }

    // Ctrl+Right: past this word and the spaces after it, to the start of the next one.
    int WordRight(int i)
    {
        var text = editing!.Text;
        if (i < text.Length && IsWordChar(text[i])) { while (i < text.Length && IsWordChar(text[i])) i++; }
        else if (i < text.Length) i = NextIndex(i);   // a space, line break or punctuation
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return i;
    }

    // Step over a whole emoji (two UTF-16 chars) instead of splitting it.
    int PrevIndex(int i) => i >= 2 && char.IsLowSurrogate(editing!.Text[i - 1]) ? i - 2 : Math.Max(0, i - 1);
    int NextIndex(int i) => i + 2 <= editing!.Text.Length && char.IsHighSurrogate(editing.Text[i]) ? i + 2 : Math.Min(editing.Text.Length, i + 1);

    void MoveCaretVertically(int lines, bool extend)
    {
        var (top, height) = textInk.Caret(editing!, caret);
        var target = top + new Vector2(0, height * (lines > 0 ? 1.5f : -0.5f));
        // Past the first or last line: to the very start or end, like other editors.
        if (target.Y < editing!.Bounds.Top) { MoveCaret(0, extend); return; }
        if (target.Y > editing.Bounds.Bottom) { MoveCaret(editing.Text.Length, extend); return; }
        MoveCaret(textInk.IndexAt(editing, target), extend);
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
        resizeStartCursor = ScreenToWorld(cursor);
        mode = Mode.ResizeText;
        modeButton = MouseButtons.Left;
        Invalidate();
    }

    void UpdateResize()
    {
        var t = resizeItem!;
        var w = ScreenToWorld(cursor);
        var b = resizeStartBounds;
        if (resizeHandle == Grip.Move)
        {
            t.Position = resizeStartPosition + (w - resizeStartCursor);
        }
        else if (resizeHandle is Grip.Left or Grip.Right)
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
            if (diagonal.LengthSquared() < 1e-6f) return;
            float ratio = Vector2.Dot(w - anchor, diagonal) / diagonal.LengthSquared();
            if (!float.IsFinite(ratio)) return;
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
            var (start, end) = Selection;
            switch (key)
            {
                case Keys.Back or Keys.Delete when DeleteSelection(): return true;
                case Keys.Back when caret > 0:
                    int from = control ? WordLeft(caret) : PrevIndex(caret);
                    SetEditText(text.Remove(from, caret - from), from);
                    return true;
                case Keys.Delete when caret < text.Length:
                    int to = control ? WordRight(caret) : NextIndex(caret);
                    SetEditText(text.Remove(caret, to - caret), caret);
                    return true;
                // Without Shift, Left / Right first just drop the selection at its start / end.
                case Keys.Left when !shift && !control && start != end: MoveCaret(start); return true;
                case Keys.Right when !shift && !control && start != end: MoveCaret(end); return true;
                case Keys.Left: MoveCaret(control ? WordLeft(caret) : PrevIndex(caret), shift); return true;
                case Keys.Right: MoveCaret(control ? WordRight(caret) : NextIndex(caret), shift); return true;
                case Keys.Up: MoveCaretVertically(-1, shift); return true;
                case Keys.Down: MoveCaretVertically(1, shift); return true;
                case Keys.Home when control: MoveCaret(0, shift); return true;
                case Keys.End when control: MoveCaret(text.Length, shift); return true;
                case Keys.Home: MoveCaret(text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1, shift); return true;
                case Keys.End:
                    int lineEnd = text.IndexOf('\n', caret);
                    MoveCaret(lineEnd < 0 ? text.Length : lineEnd, shift);
                    return true;
                case Keys.Enter: InsertText("\n"); return true;
                case Keys.Escape: CommitTextEdit(); return true;
                case Keys.A when control: SelectAll(); return true;
                case Keys.C when control: CopySelection(); return true;
                case Keys.X when control:
                    CopySelection();
                    DeleteSelection();
                    return true;
                case Keys.V when control: PasteAtCursor(); return true;
                case Keys.Z when control && shift:
                case Keys.Y when control:
                    RedoTyping();
                    return true;
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
