using System.Runtime.InteropServices;
using System.Text;

namespace Pawboard;

// "Start with Windows": a plain shortcut in the user's Startup folder, the same thing you'd make
// by hand. No registry; it shows up (and can be switched off) in Task Manager's Startup apps too.
static class Autostart
{
    const string ShortcutName = "Pawboard.lnk";

    static string ShortcutPath(string? folder = null) =>
        Path.Combine(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public static bool IsOn(string? folder = null) => File.Exists(ShortcutPath(folder));

    // Turns it on or off. Throws IOException / UnauthorizedAccessException / COMException if the
    // Startup folder can't be written.
    public static void Set(bool on, string? folder = null)
    {
        var path = ShortcutPath(folder);
        if (!on)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var exe = Environment.ProcessPath ?? throw new IOException("can't tell where Pawboard.exe is");
        Write(path, exe);
    }

    // If the exe was moved or deleted since the shortcut was made, point the shortcut at this one.
    // A shortcut that still works is left alone, even if it points at another copy.
    public static void RepairIfBroken(string? folder = null)
    {
        var path = ShortcutPath(folder);
        if (!File.Exists(path)) return;
        try
        {
            var target = Read(path);
            if (target != null && File.Exists(target)) return;
            var exe = Environment.ProcessPath;
            if (exe != null) Write(path, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Log.Write($"couldn't check the startup shortcut: {ex.GetType().Name}");
        }
    }

    static void Write(string path, string exe)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription("Pawboard: a whiteboard on your desktop");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // The exe a shortcut points at, or null if it can't be read.
    public static string? Read(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            const int STGM_READ = 0;
            ((IPersistFile)link).Load(path, STGM_READ);
            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, 0, 0);
            return target.Length > 0 ? target.ToString() : null;
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // ---------- shell link COM (only the slots up to the methods we call matter) ----------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, nint findData, uint flags);
        void GetIDList();
        void SetIDList();
        void GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory();
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments();
        void SetArguments();
        void GetHotkey();
        void SetHotkey();
        void GetShowCmd();
        void SetShowCmd();
        void GetIconLocation();
        void SetIconLocation();
        void SetRelativePath();
        void Resolve();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    }
}
