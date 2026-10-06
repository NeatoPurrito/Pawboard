using System.Runtime.InteropServices;

namespace Pawboard;

// Wallpaper mode: the board lives behind the desktop icons and takes input through system-wide
// hooks, because the icon layer on top receives every click.
//
// The rules: right-click always belongs to Windows (the desktop menu). Clicks on icons and on app
// windows are never touched. In the Desktop tool nothing is claimed except toolbar clicks. With
// Pen, Eraser or Text, a press on empty desktop belongs to the board until it's released. Keys
// are only claimed while a text box is open. While a fullscreen app is in front, the mouse hook
// is switched off entirely.
public sealed partial class BoardForm
{
    InputHooks? hooks;
    NotifyIcon? tray;
    nint foregroundHook;
    WinEventProc? foregroundProc;                       // kept alive: Windows holds a pointer to it
    readonly System.Windows.Forms.Timer fullscreenRecheck = new() { Interval = 2000 };
    long shellMenuClosedAt = long.MinValue / 2;   // Environment.TickCount64 when we last closed the desktop menu

    // Clicks the board takes never reach Explorer, so it can't close its own right-click menu.
    void CloseShellMenu()
    {
        if (Desktop.CloseShellMenu()) shellMenuClosedAt = Environment.TickCount64;
    }

    // Desktop icon positions (screen pixels), so clicks on icons can be left to Windows.
    List<Rectangle> iconRects = new();
    readonly List<FileSystemWatcher> desktopWatchers = new();
    readonly System.Windows.Forms.Timer iconRefresh = new() { Interval = 400 };

    // The hook fields below are used on the hook thread (see InputHooks); the UI thread only
    // resets them. Everything the hook thread reads from the board is a plain field it can read
    // at any time; actual changes to the board are always posted to the UI thread.
    volatile MouseButtons hookCapture;   // a press the board claimed; its release is the board's too
    // A right press on the board is held back until it's clear what it is: dragging erases,
    // letting go without dragging hands the click to Windows so the desktop menu still opens.
    volatile bool rightUndecided;
    nint boardWindow;                    // this window's handle, for the hook thread
    float hookDpiScale = 1;              // DPI scale, for the hook thread

    // Mouse moves arrive up to 1000 times a second. They're queued and handled in one go on the
    // UI thread: every point while drawing, only the latest one for panning and hovering.
    readonly System.Collections.Concurrent.ConcurrentQueue<Point> moveQueue = new();
    int moveDrainPosted;
    Point rightPressScreen;
    Point rightPressClient;
    bool cursorOnDesktop;
    string? pendingDeadKey;         // an accent key (´ ` ^) waiting for the letter it goes on
    readonly HashSet<uint> claimedKeys = new();

    // True when the user chose Exit; false when the window went away because Explorer restarted.
    public bool ExitRequested { get; private set; }

    IWin32Window? DialogOwner => wallpaper ? null : this;

    (Tool, string)[] ToolButtons => wallpaper
        ? [(Tool.Desktop, DesktopIcon), (Tool.Pen, PenIcon), (Tool.Eraser, EraserIcon), (Tool.Text, TextIcon)]
        : [(Tool.Pen, PenIcon), (Tool.Eraser, EraserIcon), (Tool.Text, TextIcon)];

    // Where the toolbar sits, in client DIPs: the window, or above the taskbar on the main monitor.
    RectangleF ToolbarArea
    {
        get
        {
            if (!wallpaper) return new RectangleF(0, 0, ClientDips.X, ClientDips.Y);
            var work = Screen.PrimaryScreen!.WorkingArea;
            var topLeft = PointToClient(work.Location);
            return new RectangleF(topLeft.X / DpiScale, topLeft.Y / DpiScale, work.Width / DpiScale, work.Height / DpiScale);
        }
    }

    bool attached;                  // in the wallpaper layer; from here on the window's styles are guarded

    // Windows Forms re-applies its own window styles at times (e.g. when the window is shown).
    // That would drop the layered flag, and without it nothing of the board reaches the screen on
    // Windows 11 24H2+, or turn it back into a top-level window. Veto those changes.
    protected override void WndProc(ref Message m)
    {
        const int WM_STYLECHANGING = 0x7C, GWL_STYLE = -16, GWL_EXSTYLE = -20;
        if (wallpaper && attached && m.Msg == WM_STYLECHANGING)
        {
            int which = (int)m.WParam;
            int newStyle = Marshal.ReadInt32(m.LParam, 4);   // STYLESTRUCT.styleNew
            if (which == GWL_EXSTYLE) newStyle = (newStyle | Desktop.LayeredExStyle) & ~Desktop.AppWindowExStyle;
            if (which == GWL_STYLE) newStyle = Desktop.ChildStyle;
            Marshal.WriteInt32(m.LParam, 4, newStyle);
        }
        base.WndProc(ref m);
    }
    void AttachToDesktop()
    {
        attached = Desktop.AttachBehindIcons(Handle);
        boardWindow = Handle;
        hookDpiScale = DpiScale;
        Log.Write($"attach: {attached}, window 0x{Handle:X}, size {ClientSize}, dpi {DeviceDpi}, {Desktop.Describe(Handle)}");
        if (!attached)
        {
            MessageBox.Show("Pawboard couldn't find the Windows desktop, so it can't sit behind your icons.",
                "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ExitRequested = true;
            BeginInvoke(Close);
            return;
        }

        hooks = new InputHooks
        {
            Mouse = OnHookMouse,
            Keyboard = OnHookKeyboard,
            // Typing state starts fresh with each text box (runs on the hook thread, like its users).
            KeyboardStarted = () => { pendingDeadKey = null; claimedKeys.Clear(); },
        };
        hooks.MouseEnabled = !Desktop.IsFullscreen(GetForegroundWindow());

        // Switch the mouse hook off while a fullscreen game or video is in front.
        foregroundProc = (_, _, hwnd, _, _, _, _) =>
        {
            try
            {
                // Another window came to the front (a chat, a login prompt...): close any open
                // text box at once, so nothing typed for that window ends up on the board.
                // Not when it's only Windows moving the focus back after we closed its desktop menu.
                bool fromOurMenuClose = Environment.TickCount64 - shellMenuClosedAt < 1000;
                if (editing != null && !fromOurMenuClose) CommitTextEdit();
                UpdateFullscreen(hwnd);
                fullscreenRecheck.Start();
            }
            catch (Exception ex) { Log.Write($"foreground handler error: {ex.GetType().Name}: {ex.Message}"); }
        };
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Games often go fullscreen a moment after coming to the front; look once more.
        fullscreenRecheck.Tick += (_, _) => { fullscreenRecheck.Stop(); UpdateFullscreen(GetForegroundWindow()); };

        // Icon positions change when files land on the desktop or the screen layout changes.
        iconRefresh.Tick += (_, _) => { iconRefresh.Stop(); ReadIconPositions(); };
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            var path = Environment.GetFolderPath(folder);
            if (!Directory.Exists(path)) continue;
            var watcher = new FileSystemWatcher(path) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            watcher.Created += (_, _) => Post(RefreshIconsSoon);
            watcher.Deleted += (_, _) => Post(RefreshIconsSoon);
            watcher.Renamed += (_, _) => Post(RefreshIconsSoon);
            desktopWatchers.Add(watcher);
        }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // If Pawboard was moved since Start with Windows was switched on, keep the shortcut working.
        Autostart.RepairIfBroken();
        ReadIconPositions();

        tray = new NotifyIcon { Text = "Pawboard", Icon = PawIcon(SystemInformation.SmallIconSize), Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Items.Add("Clear board…", null, (_, _) => ConfirmClear());
        tray.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("Exit Pawboard", null, (_, _) => { ExitRequested = true; Close(); });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!wallpaper || ExitRequested) return;
        // Showing a form can re-apply its own size and position; put it back over all monitors.
        Desktop.AttachBehindIcons(Handle);
    }

    // Runs an action on the UI thread from any thread; quietly skipped once the board is closing.
    void Post(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch (InvalidOperationException) { }
    }

    void ConfirmClear()
    {
        CommitTextEdit();
        if (board.Items.Count == 0) return;
        var answer = MessageBox.Show("Clear everything on the board?\n\nUndo on the toolbar can bring it back until Pawboard closes, and a backup copy is kept either way.",
            "Pawboard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        SaveBoard();
        BoardStore.Backup();              // Undo only lasts until Pawboard closes; the backup stays
        board.Commit(new List<Item>());   // one undo step, like any other change
        hoverText = null;
        cacheDirty = true;
        Invalidate();
    }

    void DetachFromDesktop()
    {
        hooks?.Dispose();
        hooks = null;
        if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
        foregroundHook = 0;
        fullscreenRecheck.Dispose();
        iconRefresh.Dispose();
        foreach (var w in desktopWatchers) w.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Log.Write($"window destroyed (exit requested: {ExitRequested}, recreating: {RecreatingHandle})");
        // Explorer restarting takes the wallpaper layer, and this window, with it. Keep the board.
        if (wallpaper && !ExitRequested && !RecreatingHandle)
        {
            try
            {
                CommitTextEdit();
                SaveBoard();
            }
            finally
            {
                DetachFromDesktop();
                ReleaseResources();
            }
        }
        base.OnHandleDestroyed(e);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitors added, removed or rearranged: cover the new layout and re-read the icons.
        // (Raised on a system thread, so hop to the UI thread first.)
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            Desktop.AttachBehindIcons(Handle);
            cacheDirty = true;
            Invalidate();
            RefreshIconsSoon();
        });
    }

    void UpdateFullscreen(nint foreground)
    {
        if (hooks == null) return;
        bool fullscreen = Desktop.IsFullscreen(foreground);
        if (fullscreen)
        {
            hookCapture = MouseButtons.None;
            rightUndecided = false;
            if (mode != Mode.None) EndGesture(null);
        }
        hooks.MouseEnabled = !fullscreen;
    }

    void EditingChanged()
    {
        if (hooks == null) return;
        hooks.KeyboardEnabled = editing != null;
    }

    void RefreshIconsSoon()
    {
        iconRefresh.Stop();
        iconRefresh.Start();
    }

    void ReadIconPositions()
    {
        // Asking Explorer takes a moment; do it off the UI thread so drawing never waits on it.
        Task.Run(Desktop.IconRects).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && IsHandleCreated) BeginInvoke(() => iconRects = t.Result);
        });
    }

    bool OverIcon(Point screen) => iconRects.Any(r => r.Contains(screen));

    void QueueMove(Point client)
    {
        moveQueue.Enqueue(client);
        if (Interlocked.Exchange(ref moveDrainPosted, 1) == 0) BeginInvoke(DrainMoves);
    }

    void DrainMoves()
    {
        Volatile.Write(ref moveDrainPosted, 0);
        Point? latest = null;
        while (moveQueue.TryDequeue(out var p))
        {
            if (mode == Mode.Draw) PointerMove(p);   // a stroke wants every point
            else latest = p;
        }
        if (latest is { } last) PointerMove(last);
    }

    // ---------- mouse ----------

    bool OnHookMouse(int message, Point screen, int wheel)
    {
        // Handlers run later on the UI thread (BeginInvoke); this only decides who gets the event.
        // Plain moves are never claimed, or the pointer itself would stop moving.
        var client = Desktop.ToClient(boardWindow, screen);

        // Any button press while a gesture is held means that gesture is over, or its release was
        // missed somehow (another program, a lost event). Drop it and treat this press normally,
        // so a stuck gesture can never keep eating clicks.
        if (hookCapture != MouseButtons.None &&
            message is InputHooks.WM_LBUTTONDOWN or InputHooks.WM_MBUTTONDOWN or InputHooks.WM_RBUTTONDOWN)
        {
            hookCapture = MouseButtons.None;
            rightUndecided = false;
            BeginInvoke(() => { if (mode != Mode.None) EndGesture(null); });
        }

        if (hookCapture != MouseButtons.None)
        {
            switch (message)
            {
                case InputHooks.WM_MOUSEMOVE when rightUndecided:
                {
                    // Moved far enough with the right button held: it's an eraser drag.
                    var d = new Size(screen.X - rightPressScreen.X, screen.Y - rightPressScreen.Y);
                    if (d.Width * d.Width + d.Height * d.Height <= 4 * 4) return false;
                    rightUndecided = false;
                    var start = rightPressClient;
                    BeginInvoke(() => { PointerDown(MouseButtons.Right, start); PointerMove(client); });
                    return false;
                }
                case InputHooks.WM_MOUSEMOVE:
                    QueueMove(client);
                    return false;
                case InputHooks.WM_RBUTTONUP when hookCapture == MouseButtons.Right && rightUndecided:
                    // Never dragged: it was a plain right-click. Hand it to Windows for the desktop menu.
                    hookCapture = MouseButtons.None;
                    rightUndecided = false;
                    BeginInvoke(InputHooks.ClickRight);
                    return true;
                case InputHooks.WM_LBUTTONUP when hookCapture == MouseButtons.Left:
                case InputHooks.WM_MBUTTONUP when hookCapture == MouseButtons.Middle:
                case InputHooks.WM_RBUTTONUP when hookCapture == MouseButtons.Right:
                    var button = hookCapture;
                    hookCapture = MouseButtons.None;
                    BeginInvoke(() => PointerUp(button, client));
                    return true;
                default:
                    return false;  // the wheel and other releases go on to Windows as usual
            }
        }

        switch (message)
        {
            case InputHooks.WM_MOUSEMOVE:
            {
                // Only the eraser circle and text hover boxes need to follow an idle mouse.
                if (tool is not (Tool.Eraser or Tool.Text)) return false;
                bool onDesktop = Desktop.IsDesktopAt(screen);
                if (onDesktop) QueueMove(client);
                else if (cursorOnDesktop) BeginInvoke(PointerLeave);
                cursorOnDesktop = onDesktop;
                return false;
            }
            case InputHooks.WM_LBUTTONDOWN:
            {
                if (!Desktop.IsDesktopAt(screen))
                {
                    // Clicked into an app: an open text box or menu is done.
                    if (editing != null) BeginInvoke(CommitTextEdit);
                    if (menuOpen) BeginInvoke(CloseMenu);
                    return false;
                }
                var dip = new System.Numerics.Vector2(client.X, client.Y) / hookDpiScale;
                // While the menu is open, any click on the desktop is the board's (it closes the menu).
                bool onToolbar = toolbarRect.Contains(dip.X, dip.Y) || menuOpen;
                if (!onToolbar && (tool == Tool.Desktop || OverIcon(screen)))
                {
                    if (editing != null) BeginInvoke(CommitTextEdit);
                    if (tool == Tool.Desktop) BeginInvoke(RefreshIconsSoon);   // icons may get dragged around
                    return false;
                }
                hookCapture = MouseButtons.Left;
                BeginInvoke(CloseShellMenu);
                BeginInvoke(() => PointerDown(MouseButtons.Left, client));
                return true;
            }
            case InputHooks.WM_MBUTTONDOWN:
            {
                if (zoomLocked || !Desktop.IsDesktopAt(screen) || OverIcon(screen)) return false;
                hookCapture = MouseButtons.Middle;
                BeginInvoke(CloseShellMenu);
                BeginInvoke(() => PointerDown(MouseButtons.Middle, client));
                return true;
            }
            case InputHooks.WM_RBUTTONDOWN:
            {
                if (editing != null) BeginInvoke(CommitTextEdit);
                if (menuOpen) BeginInvoke(CloseMenu);
                // In the Desktop tool, on icons and on apps, right-click is Windows' as always.
                if (tool == Tool.Desktop || !Desktop.IsDesktopAt(screen) || OverIcon(screen)) return false;
                var dip = new System.Numerics.Vector2(client.X, client.Y) / hookDpiScale;
                if (toolbarRect.Contains(dip.X, dip.Y)) return false;
                hookCapture = MouseButtons.Right;
                BeginInvoke(CloseShellMenu);
                rightUndecided = true;
                rightPressScreen = screen;
                rightPressClient = client;
                return true;
            }
            case InputHooks.WM_MOUSEWHEEL:
            {
                // Ctrl+wheel stays with Windows: it resizes the desktop icons.
                if (zoomLocked || InputHooks.IsDown(0x11) || !Desktop.IsDesktopAt(screen)) return false;
                BeginInvoke(() => PointerWheel(wheel, client));
                return true;
            }
            default:
                return false;
        }
    }

    // ---------- keyboard (only hooked while a text box is open) ----------

    bool OnHookKeyboard(int message, InputHooks.KBDLLHOOKSTRUCT key)
    {
        if (editing == null) return false;
        uint vk = key.vkCode;
        bool down = message is InputHooks.WM_KEYDOWN or InputHooks.WM_SYSKEYDOWN;
        if (!down) return claimedKeys.Remove(vk);   // swallow the release of keys we took

        // Shift, Ctrl, Alt, Win and Caps Lock always pass, so Windows never sees a key stuck down.
        if (vk is 0x10 or 0x11 or 0x12 or 0x14 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C) return false;

        bool ctrl = InputHooks.IsDown(0x11), alt = InputHooks.IsDown(0x12), shift = InputHooks.IsDown(0x10);
        bool win = InputHooks.IsDown(0x5B) || InputHooks.IsDown(0x5C);
        bool altGr = ctrl && alt;   // AltGr arrives as Ctrl+Alt and types @ € { } on many layouts
        if (win || (alt && !ctrl))
        {
            // Alt+Tab, Win+anything: you're leaving the board.
            BeginInvoke(CommitTextEdit);
            return false;
        }

        var keys = (Keys)vk;
        bool editKey = keys is Keys.Back or Keys.Delete or Keys.Left or Keys.Right or Keys.Up or Keys.Down
            or Keys.Home or Keys.End or Keys.Enter or Keys.Escape;
        bool shortcut = ctrl && !altGr && keys is Keys.V or Keys.Z or Keys.Y;
        if (editKey || shortcut)
        {
            claimedKeys.Add(vk);
            BeginInvoke(() => HandleKey(keys, ctrl && !altGr, shift));
            return true;
        }
        if (ctrl && !altGr) return false;   // other shortcuts go to whatever is in front

        var text = InputHooks.Translate(key, out bool dead);
        if (text.Length == 0) return false;
        claimedKeys.Add(vk);
        if (dead)
        {
            pendingDeadKey = text;
            return true;
        }
        if (pendingDeadKey != null)
        {
            text = Compose(pendingDeadKey, text);
            pendingDeadKey = null;
        }
        BeginInvoke(() => InsertText(text));
        return true;
    }

    // ´ + e = é, ^ + a = â ... and an accent followed by anything else types both.
    static string Compose(string accent, string letter)
    {
        if (letter == " ") return accent;
        char? mark = accent switch
        {
            "´" => '́', "`" => '̀', "^" => '̂', "~" => '̃', "¨" => '̈', "¸" => '̧', "°" => '̊',
            _ => null,
        };
        if (mark == null) return accent + letter;
        var combined = (letter + mark).Normalize(System.Text.NormalizationForm.FormC);
        return combined.Length == letter.Length ? combined : accent + letter;
    }

    // The paw icon (pawboard.ico, built into the exe) at a given size.
    static Icon PawIcon(Size size)
    {
        using var stream = typeof(BoardForm).Assembly.GetManifestResourceStream("pawboard.ico")!;
        return new Icon(stream, size);
    }
    // ---------- Win32 ----------

    delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(nint hook);
}
