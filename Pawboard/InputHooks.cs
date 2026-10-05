using System.Runtime.InteropServices;

namespace Pawboard;

// Low-level mouse and keyboard hooks. On the desktop, Windows sends every click to the icon
// layer, so the board watches input system-wide and claims only what's meant for it: clicks on
// empty desktop while a drawing tool is picked, and keys while a text box is open.
//
// Handlers must return fast (Windows drops a hook that stalls), so they only decide whether to
// claim an event; the actual work is posted to the UI thread. Both hooks vanish with the process.
sealed class InputHooks : IDisposable
{
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208,
        WM_MOUSEWHEEL = 0x20A, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

    // Returns true to claim the event (it then never reaches the desktop or any app).
    public Func<int, Point, int, bool>? Mouse;              // message, screen point, wheel delta
    public Func<int, KBDLLHOOKSTRUCT, bool>? Keyboard;       // message, key info

    readonly HookProc mouseProc, keyboardProc;               // kept alive: Windows holds pointers to them
    nint mouseHook, keyboardHook;

    public InputHooks()
    {
        mouseProc = OnMouse;
        keyboardProc = OnKeyboard;
    }

    public bool MouseEnabled
    {
        get => mouseHook != 0;
        set
        {
            if (value == MouseEnabled) return;
            if (value) mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
            else { UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
        }
    }

    public bool KeyboardEnabled
    {
        get => keyboardHook != 0;
        set
        {
            if (value == KeyboardEnabled) return;
            if (value) keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
            else { UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
        }
    }

    nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && Mouse != null)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Clicks made by programs (including the right-click the board hands back to Windows)
            // are never the board's business.
            const uint LLMHF_INJECTED = 1;
            if ((info.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(0, code, wParam, lParam);
            int wheel = (short)(info.mouseData >> 16);
            if (Mouse((int)wParam, new Point(info.pt.X, info.pt.Y), wheel)) return 1;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && Keyboard != null)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (Keyboard((int)wParam, info)) return 1;
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        MouseEnabled = false;
        KeyboardEnabled = false;
    }

    // Turns a key press into the text it types, using the keyboard layout of the window in front.
    // Dead keys (´ ` ^ ~ ¨ on many layouts) are combined with the next letter by the caller.
    public static string Translate(KBDLLHOOKSTRUCT key, out bool deadKey)
    {
        var state = new byte[256];
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })   // shift, ctrl, alt (both sides)
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) state[vk] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 1;                                   // caps lock toggled

        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var buffer = new char[8];
        // Flag 4: don't disturb the system's own dead-key state (Windows 10 1607+).
        int n = ToUnicodeEx(key.vkCode, key.scanCode, state, buffer, buffer.Length, 4, layout);
        // For a dead key Windows returns -1 and puts the accent on its own (´) in the buffer.
        deadKey = n < 0;
        if (deadKey) n = 1;
        return n > 0 ? new string(buffer, 0, n) : "";
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Clicks the right mouse button where the pointer is, as if by hand; used to hand a plain
    // right-click back to the desktop so its menu opens.
    public static void ClickRight()
    {
        const uint INPUT_MOUSE = 0, MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10;
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }

    // INPUT is a union of mouse/keyboard/hardware input; the mouse part is the largest, so it sets the size.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // ---------- Win32 ----------

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;

    delegate nint HookProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public Desktop.POINT pt; public uint mouseData, flags, time; public nint extra; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint extra; }

    [DllImport("user32.dll")] static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] buffer, int size, uint flags, nint layout);
}
