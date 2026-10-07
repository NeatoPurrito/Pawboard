using System.Numerics;
using Vortice.Direct2D1;
using Vortice.WIC;
using Color4 = Vortice.Mathematics.Color4;
using DRect = Vortice.Mathematics.Rect;

namespace Pawboard;

// "Save as picture…": the whole board as it looks at 100% (wallpaper, pattern, ink and text) as
// a PNG. Drawn at twice the screen's resolution so small writing stays sharp when zoomed into.
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
        };
        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK) return;
        pictureFolder = Path.GetDirectoryName(dialog.FileName) ?? pictureFolder;
        try
        {
            WritePicture(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Write($"save as picture failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(DialogOwner, $"Couldn't save the picture:\n{ex.Message}", "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void WritePicture(string path)
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

        // Straight from the picture into the file, without a second copy in memory.
        using var stream = wic.CreateStream(path, FileAccess.Write);
        using var encoder = wic.CreateEncoder(ContainerFormat.Png, stream);
        using var frame = encoder.CreateNewFrame(out var options);
        frame.Initialize(options);
        frame.SetSize(width, height);
        var format = Vortice.WIC.PixelFormat.Format32bppBGRA;
        frame.SetPixelFormat(ref format);
        frame.WriteSource(bitmap);
        frame.Commit();
        encoder.Commit();
    }
}
