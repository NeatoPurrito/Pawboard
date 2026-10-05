using System.Numerics;
using System.Text.Json;

namespace Pawboard;

// Saves the board to %LOCALAPPDATA%\Pawboard\board.json. Writes go to a temp file first and
// then replace the old one, so a crash mid-save never leaves a half-written board behind.
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
        public List<SavedRun>? Erasures { get; set; }
    }

    public sealed class SavedRun
    {
        public float Radius { get; set; }
        public float[] Points { get; set; } = [];
    }

    public sealed class SavedBoard
    {
        public int Version { get; set; } = 2;
        public float ViewX { get; set; }
        public float ViewY { get; set; }
        public float Zoom { get; set; } = 1;
        public List<SavedItem> Items { get; set; } = new();
        // Version 1 boards kept strokes here; read for old files, never written.
        public List<SavedItem>? Strokes { get; set; }
    }

    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pawboard");
    // Where boards were kept while the app was still called DeskBoard.
    static readonly string OldFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskBoard");

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
    static string FilePath => Path.Combine(Folder, "board.json");

    // Set when an unreadable board couldn't be moved aside; saving would overwrite it.
    public static bool SavingBlocked { get; private set; }

    // Board files are plain data: numbers, colours and text, read into the fixed classes above.
    // Nothing in a file is ever run, loaded as code or used as a path. These limits only stop a
    // broken or deliberately silly file from freezing the app; real boards stay far below them.
    const long MaxFileBytes = 64L * 1024 * 1024;
    const int MaxItems = 200_000;
    const int MaxPointsPerLine = 200_000;
    public const int MaxTextLength = 20_000;
    const float MaxCoordinate = 10_000_000;
    const float MaxSize = 10_000;

    public static SavedBoard Load()
    {
        TakeOverOldFolder();
        if (!File.Exists(FilePath)) return new SavedBoard();
        try
        {
            return ReadChecked(FilePath);
        }
        catch (Exception)
        {
            // Keep the unreadable file aside rather than overwriting it on the next save.
            try { File.Move(FilePath, Path.Combine(Folder, $"board.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json")); }
            catch (IOException) { SavingBlocked = true; }
            return new SavedBoard();
        }
    }

    // A board file you picked yourself (Open button). Null if it can't be read; the file is never touched.
    public static SavedBoard? ReadFile(string path)
    {
        try { return ReadChecked(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static SavedBoard ReadChecked(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new JsonException("file too large to be a board");
        var data = JsonSerializer.Deserialize<SavedBoard>(File.ReadAllText(path)) ?? throw new JsonException("empty board file");
        if (data.Strokes != null)
        {
            data.Items.AddRange(data.Strokes);
            data.Strokes = null;
        }
        if (data.Items.Count > MaxItems) data.Items.RemoveRange(MaxItems, data.Items.Count - MaxItems);
        if (!Coordinate(data.ViewX) || !Coordinate(data.ViewY)) data.ViewX = data.ViewY = 0;
        if (!float.IsFinite(data.Zoom) || data.Zoom <= 0) data.Zoom = 1;
        return data;
    }

    static bool Coordinate(float v) => float.IsFinite(v) && MathF.Abs(v) <= MaxCoordinate;
    static bool ValidSize(float v) => float.IsFinite(v) && v > 0 && v <= MaxSize;

    public static void Save(Board board, Vector2 viewOffset, float zoom)
    {
        if (SavingBlocked) return;
        WriteFile(FilePath, board, viewOffset, zoom);
    }

    // Writes the board to any path, via a temp file so an existing file is replaced in one step.
    public static void WriteFile(string path, Board board, Vector2 viewOffset, float zoom)
    {
        var data = new SavedBoard
        {
            ViewX = viewOffset.X,
            ViewY = viewOffset.Y,
            Zoom = zoom,
            Items = board.Items.Select(ToSaved).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        File.Move(tmp, path, overwrite: true);
    }

    // App preferences, kept apart from the board so a saved board file doesn't carry them.
    public sealed class Settings
    {
        public bool Dark { get; set; }
        public string Background { get; set; } = "Dots";   // Dots, Lines, Squares or Plain
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

    static SavedItem ToSaved(Item item) => item switch
    {
        Stroke s => new SavedItem
        {
            Kind = "stroke",
            Color = s.Color,
            Size = s.Size,
            Smoothing = s.Smoothing,
            Points = Flatten(s.Points),
            Erasures = s.Erasures.Count == 0 ? null
                : s.Erasures.Select(r => new SavedRun { Radius = r.Radius, Points = Flatten(r.Points) }).ToList(),
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
    public static Item? ToItem(SavedItem s)
    {
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
            Smoothing = float.IsFinite(s.Smoothing) && s.Smoothing is >= 0 and <= MaxSize ? s.Smoothing : 0,
            Points = Unflatten(s.Points!),
        };
        foreach (var run in s.Erasures ?? [])
            if (ValidSize(run.Radius) && ValidPoints(run.Points))
                stroke.Erasures.Add(new EraseRun { Radius = run.Radius, Points = Unflatten(run.Points) });
        return stroke;
    }

    static bool ValidPoints(float[]? values) =>
        values is { Length: >= 2 } && values.Length % 2 == 0 && values.Length <= MaxPointsPerLine * 2 && values.All(Coordinate);

    static float[] Flatten(List<Vector2> points) =>
        points.SelectMany(p => new[] { MathF.Round(p.X, 2), MathF.Round(p.Y, 2) }).ToArray();

    static List<Vector2> Unflatten(float[] values)
    {
        var points = new List<Vector2>(values.Length / 2);
        for (int i = 0; i + 1 < values.Length; i += 2) points.Add(new Vector2(values[i], values[i + 1]));
        return points;
    }
}
