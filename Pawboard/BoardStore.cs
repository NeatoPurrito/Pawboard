using System.Numerics;
using System.Text.Json;

namespace Pawboard;

// Saves the board to %LOCALAPPDATA%\Pawboard\board.json. Writes go to a temp file that's flushed
// to disk and then replaces the old one, so a crash or power cut mid-save never leaves a
// half-written board behind.
public static class BoardStore
{
    // One entry per item, in drawing order. Strokes use Size/Smoothing/Points, texts use Text/X/Y/FontSize.
    public sealed class SavedItem
    {
        public string Kind { get; set; } = "stroke";
        public uint Color { get; set; }
        public float Size { get; set; }
        public float Smoothing { get; set; }
        public float[]? Points { get; set; }
        public string? Text { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float FontSize { get; set; }
        public float Width { get; set; }
        public List<SavedRun?>? Erasures { get; set; }
    }

    public sealed class SavedRun
    {
        public float Radius { get; set; }
        public float[]? Points { get; set; }
    }

    public sealed class SavedBoard
    {
        public int Version { get; set; } = 2;
        public float ViewX { get; set; }
        public float ViewY { get; set; }
        public float Zoom { get; set; } = 1;
        public List<SavedItem?> Items { get; set; } = new();
        // Version 1 boards kept strokes here; read for old files, never written.
        public List<SavedItem?>? Strokes { get; set; }
    }

    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pawboard");
    // Where boards were kept while the app was still called DeskBoard.
    static readonly string OldFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBoard");
    static string FilePath => Path.Combine(Folder, "board.json");

    // Something about the saved board the user should know (shown on the board). Null if all is well.
    public static string? Problem { get; private set; }

    // Set when an unreadable board couldn't be moved aside; saving would overwrite it.
    public static bool SavingBlocked { get; private set; }

    // Board files are plain data: numbers, colours and text, read into the fixed classes above.
    // Nothing in a file is ever run, loaded as code or used as a path. These limits only stop a
    // broken or deliberately silly file from freezing the app; real boards stay far below them.
    const long MaxFileBytes = 64L * 1024 * 1024;
    const int MaxItems = 200_000;
    const int MaxPointsPerLine = 200_000;
    const int MaxErasePerLine = 500;
    const long MaxPointsOnBoard = 4_000_000;    // all lines and eraser paths together
    const long MaxTextOnBoard = 2_000_000;      // characters, all texts together
    public const int MaxTextLength = 20_000;
    const float MaxCoordinate = 10_000_000;
    const float MaxSize = 10_000;
    const float MinSize = 0.05f;
    const int BackupsKept = 10;

    // First start after the rename: bring the board and settings over. Copies, so the old
    // folder stays as a backup.
    static void TakeOverOldFolder()
    {
        if (Directory.Exists(Folder) || !Directory.Exists(OldFolder)) return;
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var name in new[] { "board.json", "settings.json" })
            {
                var from = Path.Combine(OldFolder, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(Folder, name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static SavedBoard Load()
    {
        TakeOverOldFolder();
        if (!File.Exists(FilePath)) return new SavedBoard();
        SavedBoard data;
        try
        {
            data = ReadChecked(FilePath);
        }
        catch (Exception)
        {
            // Keep the unreadable file aside rather than overwriting it on the next save.
            var aside = $"board.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            try
            {
                File.Move(FilePath, Path.Combine(Folder, aside));
                Problem = $"Your saved board couldn't be read. It was kept as {aside} in {Folder}; starting with an empty board.";
            }
            catch (Exception)
            {
                SavingBlocked = true;
                Problem = $"Your saved board in {Folder} couldn't be read, so saving is paused to keep it safe.";
            }
            return new SavedBoard();
        }
        // Rescaling an old zoomed-out board: keep the file as it was, in case anything looks off.
        if (AdoptSavedView(data)) Backup();
        return data;
    }

    // A board file you picked yourself (Open button). Null if it can't be read; the file is never touched.
    public static SavedBoard? ReadFile(string path)
    {
        try
        {
            var data = ReadChecked(path);
            AdoptSavedView(data);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    // Keeps a copy of the autosaved board (before Open, Clear or a rescale), newest 10 only.
    public static void Backup()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            File.Copy(FilePath, Path.Combine(Folder, $"board.backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"));
            var old = new DirectoryInfo(Folder).GetFiles("board.backup-*.json")
                .OrderByDescending(f => f.Name)
                .Skip(BackupsKept);
            foreach (var f in old) f.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static SavedBoard ReadChecked(string path)
    {
        var data = JsonSerializer.Deserialize<SavedBoard>(ReadLimited(path)) ?? throw new JsonException("empty board file");
        if (data.Strokes != null)
        {
            data.Items.AddRange(data.Strokes);
            data.Strokes = null;
        }
        data.Items ??= new();
        data.Items.RemoveAll(i => i == null);
        if (data.Items.Count > MaxItems) data.Items.RemoveRange(MaxItems, data.Items.Count - MaxItems);
        if (!Coordinate(data.ViewX) || !Coordinate(data.ViewY)) data.ViewX = data.ViewY = 0;
        if (!float.IsFinite(data.Zoom) || data.Zoom <= 0) data.Zoom = 1;
        KeepWithinBudget(data);
        return data;
    }

    // Reads at most MaxFileBytes; a bigger file isn't a board.
    static byte[] ReadLimited(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxFileBytes) throw new JsonException("file too large to be a board");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = file.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > MaxFileBytes) throw new JsonException("file too large to be a board");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    // Trims what no real board needs: overly long lines, piles of eraser paths, and anything past a
    // total size for the whole board.
    static void KeepWithinBudget(SavedBoard data)
    {
        long points = 0, text = 0;
        int keep = 0;
        foreach (var item in data.Items)
        {
            if (item!.Points is { Length: > MaxPointsPerLine * 2 }) item.Points = item.Points[..(MaxPointsPerLine * 2)];
            if (item.Erasures != null)
            {
                item.Erasures.RemoveAll(r => r?.Points == null);
                if (item.Erasures.Count > MaxErasePerLine) item.Erasures.RemoveRange(MaxErasePerLine, item.Erasures.Count - MaxErasePerLine);
                foreach (var run in item.Erasures)
                    if (run!.Points!.Length > MaxPointsPerLine * 2) run.Points = run.Points[..(MaxPointsPerLine * 2)];
            }
            points += (item.Points?.Length ?? 0) / 2 + (item.Erasures?.Sum(r => r!.Points!.Length / 2) ?? 0);
            text += item.Text?.Length ?? 0;
            if (points > MaxPointsOnBoard || text > MaxTextOnBoard) break;
            keep++;
        }
        if (keep < data.Items.Count) data.Items.RemoveRange(keep, data.Items.Count - keep);
    }

    // Boards from before the board was fixed to the screen could be saved zoomed out. Make the
    // saved view the 100% start view: everything keeps its place on screen, at the right scale.
    // Returns true if anything changed.
    static bool AdoptSavedView(SavedBoard data)
    {
        float zoom = data.Zoom;
        if (zoom >= 1) return false;
        var offset = new Vector2(data.ViewX, data.ViewY);
        foreach (var item in data.Items)
        {
            if (item!.Kind == "text")
            {
                var p = new Vector2(item.X, item.Y) * zoom + offset;
                (item.X, item.Y) = (p.X, p.Y);
                item.FontSize *= zoom;
                item.Width *= zoom;
                continue;
            }
            Transform(item.Points, zoom, offset);
            item.Size *= zoom;
            item.Smoothing *= zoom;
            foreach (var run in item.Erasures ?? [])
            {
                Transform(run!.Points, zoom, offset);
                run.Radius *= zoom;
            }
        }
        data.ViewX = data.ViewY = 0;
        data.Zoom = 1;
        return true;
    }

    static void Transform(float[]? values, float zoom, Vector2 offset)
    {
        if (values == null) return;
        for (int i = 0; i + 1 < values.Length; i += 2)
        {
            values[i] = values[i] * zoom + offset.X;
            values[i + 1] = values[i + 1] * zoom + offset.Y;
        }
    }

    static bool Coordinate(float v) => float.IsFinite(v) && MathF.Abs(v) <= MaxCoordinate;
    static bool ValidSize(float v) => float.IsFinite(v) && v >= MinSize && v <= MaxSize;

    public static void Save(Board board, Vector2 viewOffset, float zoom)
    {
        if (SavingBlocked) return;
        WriteFile(FilePath, board, viewOffset, zoom);
    }

    // Writes the board to any path: to a temp file first, flushed all the way to disk, which then
    // replaces the old file in one step.
    public static void WriteFile(string path, Board board, Vector2 viewOffset, float zoom)
    {
        var data = new SavedBoard
        {
            ViewX = viewOffset.X,
            ViewY = viewOffset.Y,
            Zoom = zoom,
            Items = board.Items.Select(ToSaved).ToList()!,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, data, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            file.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    // App preferences, kept apart from the board so a saved board file doesn't carry them.
    public sealed class Settings
    {
        public bool Dark { get; set; }
        public string Background { get; set; } = "Dots";   // Dots, Lines, Squares or Plain
        public float PatternStrength { get; set; } = 0.5f;   // 0 faint, 0.5 normal, 1 strong
        public bool ToolbarHidden { get; set; }
        public bool ZoomLocked { get; set; }
        public bool ShowWallpaper { get; set; }
        public float WallpaperVeil { get; set; } = 0.6f;     // 0 the picture as is, 1 plain board colour
    }

    static string SettingsPath => Path.Combine(Folder, "settings.json");

    public static Settings LoadSettings()
    {
        TakeOverOldFolder();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new Settings(); }
    }

    public static void SaveSettings(Settings settings)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
    }

    static SavedItem? ToSaved(Item item) => item switch
    {
        Stroke s => new SavedItem
        {
            Kind = "stroke",
            Color = s.Color,
            Size = s.Size,
            Smoothing = s.Smoothing,
            Points = Flatten(s.Points),
            Erasures = s.Erasures.Count == 0 ? null
                : s.Erasures.Select(r => (SavedRun?)new SavedRun { Radius = r.Radius, Points = Flatten(r.Points) }).ToList(),
        },
        TextItem t => new SavedItem
        {
            Kind = "text",
            Color = t.Color,
            Text = t.Text,
            X = t.Position.X,
            Y = t.Position.Y,
            FontSize = t.FontSize,
            Width = t.Width,
        },
        _ => throw new NotSupportedException(item.GetType().Name),
    };

    // Null for entries that can't become an item: unknown kind, missing or out-of-range values.
    public static Item? ToItem(SavedItem? s)
    {
        if (s == null) return null;
        if (s.Kind == "text")
        {
            if (string.IsNullOrEmpty(s.Text) || !ValidSize(s.FontSize) || !Coordinate(s.X) || !Coordinate(s.Y)) return null;
            var text = s.Text.Length > MaxTextLength ? s.Text[..MaxTextLength] : s.Text;
            float width = float.IsFinite(s.Width) && s.Width > 0 && s.Width <= MaxCoordinate ? s.Width : 0;
            return new TextItem { Color = s.Color, Text = text, Position = new Vector2(s.X, s.Y), FontSize = s.FontSize, Width = width };
        }
        if (s.Kind != "stroke" || !ValidSize(s.Size) || !ValidPoints(s.Points)) return null;
        var stroke = new Stroke
        {
            Color = s.Color,
            Size = s.Size,
            // Smoothing only ever needs to be a fraction of the line width.
            Smoothing = float.IsFinite(s.Smoothing) && s.Smoothing >= 0 ? MathF.Min(s.Smoothing, s.Size * 2) : 0,
            Points = Unflatten(s.Points!),
        };
        foreach (var run in s.Erasures ?? [])
            if (run != null && ValidSize(run.Radius) && ValidPoints(run.Points))
                stroke.Erasures.Add(new EraseRun { Radius = run.Radius, Points = Unflatten(run.Points!) });
        return stroke;
    }

    static bool ValidPoints(float[]? values) =>
        values is { Length: >= 2 } && values.Length % 2 == 0 && values.All(Coordinate);

    static float[] Flatten(List<Vector2> points) =>
        points.SelectMany(p => new[] { MathF.Round(p.X, 2), MathF.Round(p.Y, 2) }).ToArray();

    static List<Vector2> Unflatten(float[] values)
    {
        var points = new List<Vector2>(values.Length / 2);
        for (int i = 0; i + 1 < values.Length; i += 2) points.Add(new Vector2(values[i], values[i + 1]));
        return points;
    }
}
