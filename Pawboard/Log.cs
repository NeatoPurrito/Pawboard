namespace Pawboard;

// A small log next to the exe (pawboard.log), for finding out why the wallpaper went away.
// Kept to a few lines per run: start, attach, exit reason, and any error.
static class Log
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "pawboard.log");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
