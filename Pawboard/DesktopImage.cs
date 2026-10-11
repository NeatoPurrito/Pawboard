using System.Runtime.InteropServices;

namespace Pawboard;

// The picture Windows shows as your wallpaper, per monitor, read through the documented
// IDesktopWallpaper interface (the one Settings uses). Read only: Pawboard never changes it.
static class DesktopImage
{
    // How Windows fits the picture (DESKTOP_WALLPAPER_POSITION).
    public enum Fit { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    public sealed record Monitor(Rectangle Bounds, string? Path);   // screen pixels

    public sealed record Info(List<Monitor> Monitors, Fit Position, uint BackgroundColor)
    {
        // Changes when anything that affects the picture does (a new wallpaper, a slideshow step).
        public string Signature => $"{Position}|{BackgroundColor}|" + string.Join("|", Monitors.Select(m =>
            $"{m.Bounds}:{m.Path}:{(m.Path != null && File.Exists(m.Path) ? File.GetLastWriteTimeUtc(m.Path).Ticks : 0)}"));
    }

    // Null if the wallpaper settings can't be read at all.
    public static Info? Read()
    {
        object? com = null;
        try
        {
            com = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"))!);
            var wallpaper = (IDesktopWallpaper)com!;
            var monitors = new List<Monitor>();
            if (wallpaper.GetMonitorDevicePathCount(out uint count) < 0) return null;
            for (uint i = 0; i < Math.Min(count, 16u); i++)
            {
                if (wallpaper.GetMonitorDevicePathAt(i, out var id) < 0 || id == null) continue;
                // Monitors that are switched off are still listed, without a rectangle.
                if (wallpaper.GetMonitorRECT(id, out var r) != 0 || r.Right <= r.Left || r.Bottom <= r.Top) continue;
                wallpaper.GetWallpaper(id, out var path);
                monitors.Add(new Monitor(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), Usable(path, monitors.Count)));
            }
            if (wallpaper.GetPosition(out int position) < 0) position = (int)Fit.Fill;
            if (wallpaper.GetBackgroundColor(out uint color) < 0) color = 0;
            return new Info(monitors, Enum.IsDefined((Fit)position) ? (Fit)position : Fit.Fill, color);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Write($"wallpaper settings unavailable: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
    }

    // Windows Spotlight and some themes don't report a file; Windows keeps a copy of whatever it
    // shows in the Themes folder, so fall back to that. Null means a plain colour background.
    static string? Usable(string? path, int index)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        var themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
        foreach (var name in new[] { $"Transcoded_{index:000}", "TranscodedWallpaper" })
        {
            var copy = Path.Combine(themes, name);
            if (File.Exists(copy)) return copy;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    // Only the slots up to GetPosition; the setters are placeholders and never called.
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper();
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string? path);
        [PreserveSig] int GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string? monitorId);
        [PreserveSig] int GetMonitorDevicePathCount(out uint count);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT rect);
        void SetBackgroundColor();
        [PreserveSig] int GetBackgroundColor(out uint color);
        void SetPosition();
        [PreserveSig] int GetPosition(out int position);
    }
}
