using System.Numerics;
using Vortice.Direct2D1;
using Vortice.WIC;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// "Save as picture…": the board as it looks at 100% (wallpaper, pattern, ink and text) as a PNG
// per screen. Drawn at twice the screen's resolution so small writing stays sharp when zoomed into.
public sealed partial class BoardForm
{
    const float PictureSharpness = 2;
    string pictureFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    void SaveAsPicture()
    {
        CommitTextEdit();
        using var dialog = new SaveFileDialog
        {
            Title = "Save the board as a picture",
            Filter = "PNG picture (*.png)|*.png",
            DefaultExt = "png",
            FileName = $"Pawboard {DateTime.Now:yyyy-MM-dd}.png",
            InitialDirectory = pictureFolder,
            // With several screens the files get " - screen N" added; asked about below instead.
            OverwritePrompt = PictureScreens().Length == 1,
        };
        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
        pictureFolder = Path.GetDirectoryName(dialog.FileName) ?? pictureFolder;
        var files = PictureFiles(dialog.FileName);
        var existing = files.Where(File.Exists).Select(Path.GetFileName).ToList();
        if (files.Count > 1 && existing.Count > 0 &&
            MessageBox.Show(DialogOwner, $"Replace these pictures?\n\n{string.Join("\n", existing)}", "Pawboard",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            WritePictures(files);
        }
        catch (Exception ex)
        {
            Log.Write($"save as picture failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(DialogOwner, $"Couldn't save the picture:\n{ex.Message}", "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // The screens the board covers, left to right (the order the files are numbered in).
    Rectangle[] PictureScreens() => Screen.AllScreens.Select(s => s.Bounds).OrderBy(b => b.X).ThenBy(b => b.Y).ToArray();

    // One file per screen: the chosen name as it is with a single screen, else " - screen N" added.
    List<string> PictureFiles(string chosen)
    {
        int count = PictureScreens().Length;
        if (count == 1) return [chosen];
        var folder = Path.GetDirectoryName(chosen) ?? "";
        var name = Path.GetFileNameWithoutExtension(chosen);
        return Enumerable.Range(1, count).Select(i => Path.Combine(folder, $"{name} - screen {i}.png")).ToList();
    }

    void WritePictures(List<string> files)
    {
        // The board is the screen, so the picture is the screen's size times the sharpness
        // (a little less on huge setups, to stay within what an image can hold).
        float k = PictureSharpness;
        int longest = Math.Max(ClientSize.Width, ClientSize.Height);
        if (longest * k > 16384) k = 16384f / longest;
        uint width = (uint)Math.Max(1, (int)(ClientSize.Width * k)), height = (uint)Math.Max(1, (int)(ClientSize.Height * k));

        using var wic = new IWICImagingFactory();
        using var bitmap = wic.CreateBitmap(width, height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        float dpi = DeviceDpi * k;
        using (var target = factory.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties { DpiX = dpi, DpiY = dpi }))
        {
            target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            // The drawing code uses the screen's brush and view; lend it this picture's instead.
            var screenBrush = brush;
            var (viewOffset, viewZoom) = (offset, zoom);
            using var pictureBrush = target.CreateSolidColorBrush(new Color4(0, 0, 0, 1));
            brush = pictureBrush;
            (offset, zoom) = (Vector2.Zero, 1);
            try
            {
                target.BeginDraw();
                target.Transform = Matrix3x2.Identity;
                target.Clear(Colors.Background);
                if (showDesktopImage && (desktopImageInfo ??= DesktopImage.Read()) is { Monitors.Count: > 0 } info)
                {
                    PaintWallpaper(target, info);
                    brush.Color = WithAlpha(Colors.Background, imageVeil);
                    var size = ClientDips;
                    target.FillRectangle(new DRect(0, 0, size.X, size.Y), brush);
                }
                DrawBackdrop(target);
                foreach (var item in board.Items) DrawItem(target, item);
                target.EndDraw().CheckError();
            }
            finally
            {
                (offset, zoom) = (viewOffset, viewZoom);
                brush = screenBrush;
            }
        }

        // Each screen's part, cut out and written straight into its file (no copies in memory).
        var screens = PictureScreens();
        for (int i = 0; i < screens.Length && i < files.Count; i++)
        {
            var topLeft = PointToClient(screens[i].Location);
            var part = Rectangle.Intersect(
                new Rectangle((int)MathF.Round(topLeft.X * k), (int)MathF.Round(topLeft.Y * k),
                    (int)MathF.Round(screens[i].Width * k), (int)MathF.Round(screens[i].Height * k)),
                new Rectangle(0, 0, (int)width, (int)height));
            if (part.Width <= 0 || part.Height <= 0) continue;
            using var clipper = wic.CreateBitmapClipper();
            clipper.Initialize(bitmap, new Vortice.Mathematics.RectI(part.X, part.Y, part.Width, part.Height));
            using var stream = wic.CreateStream(files[i], FileAccess.Write);
            using var encoder = wic.CreateEncoder(ContainerFormat.Png, stream);
            using var frame = encoder.CreateNewFrame(out var options);
            frame.Initialize(options);
            frame.SetSize((uint)part.Width, (uint)part.Height);
            var format = Vortice.WIC.PixelFormat.Format32bppBGRA;
            frame.SetPixelFormat(ref format);
            frame.WriteSource(clipper);
            frame.Commit();
            encoder.Commit();
        }
    }
}
