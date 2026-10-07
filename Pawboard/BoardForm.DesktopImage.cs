using Vortice.Direct2D1;
using Vortice.WIC;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// "Show my wallpaper": your Windows wallpaper under the board, with the board's own colour laid
// over it like tinted glass (the veil, set by a slider) so ink stays readable. The picture is
// painted once into a screen-sized layer and stays put when you zoom, like a real wallpaper.
public sealed partial class BoardForm
{
    bool showDesktopImage;
    float imageVeil = 0.6f;                      // 0: the picture as is, 1: plain board colour
    ID2D1BitmapRenderTarget? desktopImageLayer;  // the wallpaper, painted at screen size
    bool desktopImageDirty = true;               // the layer needs painting (again)
    DesktopImage.Info? desktopImageInfo;
    readonly System.Windows.Forms.Timer desktopImageCheck = new() { Interval = 800 };
    FileSystemWatcher? themesWatcher;

    bool ShowingDesktopImage => showDesktopImage && desktopImageLayer != null;

    void StartDesktopImageWatch()
    {
        // Settings fires this when the wallpaper changes (also each slideshow step); wait for it
        // to settle, then look whether the picture really changed.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Not every way of changing the wallpaper sends that, but Windows always writes its own
        // copy of the new picture into its Themes folder (Settings, "Set as background",
        // slideshows, Spotlight), so watch for that too. Costs nothing until it changes.
        var themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes");
        try
        {
            if (Directory.Exists(themes))
            {
                themesWatcher = new FileSystemWatcher(themes, "Transcoded*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                themesWatcher.Changed += (_, _) => Post(CheckDesktopImageSoon);
                themesWatcher.Created += (_, _) => Post(CheckDesktopImageSoon);
                themesWatcher.Renamed += (_, _) => Post(CheckDesktopImageSoon);
                themesWatcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Write($"can't watch for wallpaper changes: {ex.GetType().Name}");
        }
        desktopImageCheck.Tick += (_, _) =>
        {
            desktopImageCheck.Stop();
            if (!showDesktopImage) return;
            var fresh = DesktopImage.Read();
            bool changed = fresh?.Signature != desktopImageInfo?.Signature;
            Log.Write($"wallpaper check: {(changed ? "changed, repainting" : "unchanged")}");
            if (!changed) return;
            desktopImageInfo = fresh;
            RepaintDesktopImage();
        };
    }

    void CheckDesktopImageSoon()
    {
        if (!showDesktopImage) return;
        desktopImageCheck.Stop();
        desktopImageCheck.Start();
    }

    void StopDesktopImageWatch()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        themesWatcher?.Dispose();
        themesWatcher = null;
        desktopImageCheck.Dispose();
        desktopImageLayer?.Dispose();
        desktopImageLayer = null;
    }

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop) CheckDesktopImageSoon();
    }

    void ToggleDesktopImage()
    {
        showDesktopImage = !showDesktopImage;
        desktopImageInfo = null;   // read fresh when it's switched on
        RepaintDesktopImage();
        if (!showDesktopImage)
        {
            desktopImageLayer?.Dispose();
            desktopImageLayer = null;
        }
        SaveSettings();
    }

    // The layer gets painted again on the next frame, and the board around it too.
    void RepaintDesktopImage()
    {
        desktopImageDirty = true;
        contentVersion++;
        cacheDirty = true;
        Invalidate();
    }

    // Called before the board is painted; cheap when nothing changed.
    void EnsureDesktopImageLayer()
    {
        if (!showDesktopImage || (!desktopImageDirty && desktopImageLayer != null) || rt == null) return;
        desktopImageDirty = false;
        desktopImageLayer?.Dispose();
        desktopImageLayer = null;
        try
        {
            desktopImageInfo ??= DesktopImage.Read();
            if (desktopImageInfo is not { Monitors.Count: > 0 } info)
            {
                ShowNotice("Couldn't read your Windows wallpaper.", seconds: 8);
                return;
            }
            var layer = rt.CreateCompatibleRenderTarget(null, null, null, CompatibleRenderTargetOptions.None);
            layer.BeginDraw();
            PaintWallpaper(layer, info);
            if (layer.EndDraw().Failure) { layer.Dispose(); DiscardDevice(); return; }
            desktopImageLayer = layer;
        }
        catch (Exception ex)
        {
            // An unreadable or odd picture just means no picture; the board works as before.
            Log.Write($"wallpaper picture failed: {ex.GetType().Name}: {ex.Message}");
            ShowNotice("Couldn't load your Windows wallpaper.", seconds: 8);
        }
    }

    // The wallpaper as Windows shows it, at screen size: the layer, or a saved picture of the board.
    void PaintWallpaper(ID2D1RenderTarget target, DesktopImage.Info info)
    {
        uint c = info.BackgroundColor;   // COLORREF: 0x00BBGGRR
        target.Clear(new Color4((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, 1));
        using var wic = new IWICImagingFactory();
        if (info.Position == DesktopImage.Fit.Span)
        {
            // One picture across all monitors.
            var all = info.Monitors.Select(m => m.Bounds).Aggregate(Rectangle.Union);
            var path = info.Monitors.FirstOrDefault(m => m.Path != null)?.Path;
            if (path != null) DrawWallpaperPicture(target, wic, path, all, DesktopImage.Fit.Fill);
        }
        else
        {
            foreach (var m in info.Monitors)
                if (m.Path != null) DrawWallpaperPicture(target, wic, m.Path, m.Bounds, info.Position);
        }
    }

    // One picture into one screen area (screen pixels), fitted the way Windows fits it.
    void DrawWallpaperPicture(ID2D1RenderTarget layer, IWICImagingFactory wic, string path, Rectangle area, DesktopImage.Fit fit)
    {
        // Wallpapers are ordinary pictures, but a broken or gigantic file shouldn't hang the board.
        if (new FileInfo(path).Length > 200L * 1024 * 1024) return;
        using var decoder = wic.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnDemand);
        using var frame = decoder.GetFrame(0);
        var size = frame.Size;
        int iw = (int)size.Width, ih = (int)size.Height;
        if (iw <= 0 || ih <= 0 || iw > 30000 || ih > 30000) return;

        // Where the picture goes, in screen pixels.
        float sx = (float)area.Width / iw, sy = (float)area.Height / ih;
        float scale = fit switch
        {
            DesktopImage.Fit.Fit => MathF.Min(sx, sy),
            DesktopImage.Fit.Center or DesktopImage.Fit.Tile => 1,
            _ => MathF.Max(sx, sy),   // Fill (and Stretch, handled below)
        };
        float w = fit == DesktopImage.Fit.Stretch ? area.Width : iw * scale;
        float h = fit == DesktopImage.Fit.Stretch ? area.Height : ih * scale;
        // Decode at the size it's shown (never more than the area), so a big photo doesn't
        // sit in graphics memory at full size.
        uint dw = (uint)Math.Clamp(MathF.Round(MathF.Min(w, area.Width * 2f)), 1, 16384);
        uint dh = (uint)Math.Clamp(MathF.Round(MathF.Min(h, area.Height * 2f)), 1, 16384);
        using var scaler = wic.CreateBitmapScaler();
        scaler.Initialize(frame, dw, dh, Vortice.WIC.BitmapInterpolationMode.Fant);
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(scaler, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        using var bitmap = layer.CreateBitmapFromWicBitmap(converter, null);

        var areaDip = ScreenPixelsToDips(area);
        float k = areaDip.Width / area.Width;   // screen pixels to DIPs
        layer.PushAxisAlignedClip(ToDRect(areaDip), AntialiasMode.Aliased);
        if (fit == DesktopImage.Fit.Tile)
        {
            for (float y = 0; y < area.Height; y += h)
                for (float x = 0; x < area.Width; x += w)
                    layer.DrawBitmap(bitmap, new DRect(areaDip.X + x * k, areaDip.Y + y * k, w * k, h * k), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
        }
        else
        {
            float x = (area.Width - w) / 2, y = (area.Height - h) / 2;   // centred
            layer.DrawBitmap(bitmap, new DRect(areaDip.X + x * k, areaDip.Y + y * k, w * k, h * k), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, null);
        }
        layer.PopAxisAlignedClip();
    }

    RectangleF ScreenPixelsToDips(Rectangle screen)
    {
        var topLeft = PointToClient(screen.Location);
        return new RectangleF(topLeft.X / DpiScale, topLeft.Y / DpiScale, screen.Width / DpiScale, screen.Height / DpiScale);
    }

    // The wallpaper with the veil over it, at screen size (it doesn't move with the view).
    void DrawDesktopImage(ID2D1RenderTarget r)
    {
        var old = r.Transform;
        r.Transform = System.Numerics.Matrix3x2.Identity;
        r.DrawBitmap(desktopImageLayer!.Bitmap, 1f, Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
        brush!.Color = WithAlpha(Colors.Background, imageVeil);
        var size = ClientDips;
        r.FillRectangle(new DRect(0, 0, size.X, size.Y), brush);
        r.Transform = old;
    }
}
