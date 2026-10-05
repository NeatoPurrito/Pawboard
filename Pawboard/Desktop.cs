using System.Runtime.InteropServices;
using System.Text;

namespace Pawboard;

// Everything about living on the Windows desktop: putting a window into the wallpaper layer
// (behind the icons, like Wallpaper Engine and Lively do), telling the desktop apart from app
// windows, and reading where the desktop icons are.
static class Desktop
{
    // Window styles for the board in the wallpaper layer: a visible child window...
    public const int ChildStyle = 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000;   // CHILD, VISIBLE, CLIPSIBLINGS, CLIPCHILDREN
    // ...that's layered. Since Windows 11 24H2 the desktop has no drawing surface of its own for
    // child windows, so an ordinary child never reaches the screen. A layered window gets its own
    // surface; that's how the icon layer shows up too.
    public const int LayeredExStyle = 0x80000;
    public const int AppWindowExStyle = 0x40000;

    // The whole virtual screen (all monitors), in the host window's client coordinates.
    public static Rectangle VirtualScreenIn(nint host)
    {
        var screen = SystemInformation.VirtualScreen;
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(host, ref origin);
        return new Rectangle(origin.X, origin.Y, screen.Width, screen.Height);
    }

    // Makes sure a board window sits behind the desktop icons, opaque and covering every monitor.
    // The window is normally created there already (see BoardForm.CreateParams); this also moves it
    // back if it was created elsewhere, and resizes it when monitors change. Returns false if the
    // desktop couldn't be found (e.g. Explorer isn't running).
    public static bool AttachBehindIcons(nint hwnd)
    {
        var host = FindWallpaperHost();
        if (host == 0) return false;

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        SetWindowLongPtr(hwnd, GWL_STYLE, ChildStyle);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(hwnd, GWL_EXSTYLE) | LayeredExStyle));
        if (GetParent(hwnd) != host) SetParent(hwnd, host);
        // A layered window shows nothing until it's told how opaque it is: fully.
        const uint LWA_ALPHA = 2;
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);

        var area = VirtualScreenIn(host);
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
        SetWindowPos(hwnd, 0, area.X, area.Y, area.Width, area.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        return true;
    }

    // A screen point in a window's client pixels. Plain Win32, so it's safe from any thread.
    public static Point ToClient(nint hwnd, Point screen)
    {
        var p = new POINT { X = screen.X, Y = screen.Y };
        ScreenToClient(hwnd, ref p);
        return new Point(p.X, p.Y);
    }

    // For checking it's still set up right (logged when the board starts).
    public static string Describe(nint hwnd) =>
        $"parent 0x{GetParent(hwnd):X}, style 0x{GetWindowLongPtr(hwnd, -16):X}, exstyle 0x{GetWindowLongPtr(hwnd, -20):X}";

    // Repaints the normal wallpaper after the board is gone.
    public static void RepaintWallpaper()
    {
        const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100;
        var host = FindWallpaperHost(spawn: false);
        if (host != 0) RedrawWindow(host, 0, 0, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    // The window behind the desktop icons that wallpapers draw into.
    // Windows 11 24H2 and later: a WorkerW inside Progman. Older: a top-level WorkerW right after
    // the one that holds the icons.
    public static nint FindWallpaperHost(bool spawn = true)
    {
        var progman = FindWindow("Progman", null);
        if (progman == 0) return 0;
        // Asks Progman to create the wallpaper WorkerW; does nothing if it already exists.
        if (spawn) SendMessageTimeout(progman, 0x052C, 0xD, 0x1, 0 /* SMTO_NORMAL */, 1000, out _);

        var inside = FindWindowEx(progman, 0, "WorkerW", null);
        if (inside != 0) return inside;

        nint result = 0;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, 0, "SHELLDLL_DefView", null) == 0) return true;
            result = FindWindowEx(0, top, "WorkerW", null);
            return false;
        }, 0);
        return result;
    }

    // The list view that shows the desktop icons.
    public static nint IconListView()
    {
        var progman = FindWindow("Progman", null);
        var defView = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        if (defView == 0)
        {
            EnumWindows((top, _) =>
            {
                defView = FindWindowEx(top, 0, "SHELLDLL_DefView", null);
                return defView == 0;
            }, 0);
        }
        return defView == 0 ? 0 : FindWindowEx(defView, 0, "SysListView32", null);
    }

    // True if the window under this screen point is the desktop itself (icons, wallpaper, the
    // board), not an app window.
    public static bool IsDesktopAt(Point screen)
    {
        var hwnd = WindowFromPoint(new POINT { X = screen.X, Y = screen.Y });
        return IsDesktopWindow(hwnd);
    }

    public static bool IsDesktopWindow(nint hwnd)
    {
        if (hwnd == 0) return false;
        const uint GA_ROOT = 2;
        var cls = ClassName(GetAncestor(hwnd, GA_ROOT));
        return cls is "Progman" or "WorkerW";
    }

    // True if this window fills its whole monitor (a game, a fullscreen video, a presentation).
    public static bool IsFullscreen(nint hwnd)
    {
        if (hwnd == 0 || IsDesktopWindow(hwnd)) return false;
        if (ClassName(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!GetWindowRect(hwnd, out var r)) return false;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    // Screen rectangles of the desktop icons, read through the shell's documented IFolderView
    // interface (the same one Explorer uses to place them). Slow-ish (talks to Explorer), so call
    // it off the UI thread. Empty if the icons are hidden or the desktop can't be reached.
    public static List<Rectangle> IconRects()
    {
        var rects = new List<Rectangle>();
        var listView = IconListView();
        if (listView == 0 || !IsWindowVisible(listView)) return rects;

        object? windows = null;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            object loc = 0;            // CSIDL_DESKTOP
            object root = null!;       // VT_EMPTY
            const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
            var dispatch = ((IShellWindows)windows!).FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            var browser = (IShellBrowser)((IServiceProvider)dispatch).QueryService(ref sidTopLevelBrowser, ref iidShellBrowser);
            var view = (IFolderView)browser.QueryActiveShellView();

            view.GetSpacing(out var spacing);
            var origin = new POINT();
            ClientToScreen(listView, ref origin);
            const uint SVGIO_ALLVIEW = 2;
            int count = view.ItemCount(SVGIO_ALLVIEW);
            for (int i = 0; i < count; i++)
            {
                view.Item(i, out var pidl);
                try
                {
                    view.GetItemPosition(pidl, out var p);
                    rects.Add(new Rectangle(origin.X + p.X, origin.Y + p.Y, spacing.X, spacing.Y));
                }
                finally { Marshal.FreeCoTaskMem(pidl); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Explorer busy or restarting: no icon positions this time.
        }
        finally
        {
            if (windows != null) Marshal.ReleaseComObject(windows);
        }
        return rects;
    }

    // ---------- shell COM interfaces (only the slots before the methods we call matter) ----------

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
            int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryActiveShellView();
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        void Item(int index, out nint pidl);
        int ItemCount(uint flags);
        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();
        void GetItemPosition(nint pidl, out POINT point);
        void GetSpacing(out POINT spacing);
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] static extern nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern long GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);
}
