using System.Runtime.InteropServices;

namespace Pawboard;

static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEMOVEPOINT
    {
        public int X;
        public int Y;
        public uint Time;
        public nint ExtraInfo;
    }

    public const uint GMMP_USE_DISPLAY_POINTS = 1;

    // Windows merges mouse moves into one WM_MOUSEMOVE per message pump, but keeps the last 64
    // raw positions. Reading them back is what keeps fast strokes round instead of polygonal.
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetMouseMovePointsEx(uint cbSize, ref MOUSEMOVEPOINT lppt,
        [Out] MOUSEMOVEPOINT[] lpptBuf, int nBufPoints, uint resolution);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageTime();

    // Blocks until the compositor shows the next frame; paces animations to the monitor's refresh rate.
    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // Dark or light window title bar, to match the board.
    public static void UseDarkTitleBar(nint hwnd, bool dark)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
