namespace Pawboard;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Draws sample strokes to a PNG without opening a window, to check stroke quality.
        if (args.Length == 2 && args[0] == "--render-test")
        {
            RenderTest.Run(args[1]);
            return;
        }
        // Checks that erased ink is really removed (see RenderTest.RunBakeTest).
        if (args.Length == 2 && args[0] == "--bake-test")
        {
            RenderTest.RunBakeTest(args[1]);
            return;
        }
        // Renders a board file as the start view shows it, to a PNG sized like the whole desktop.
        if (args.Length == 3 && args[0] == "--render-board")
        {
            var screen = SystemInformation.VirtualScreen;
            RenderTest.RenderBoard(args[1], args[2], screen.Width, screen.Height);
            return;
        }
        // Writes what the app can see of the desktop (wallpaper layer, icon positions) to a file.
        if (args.Length == 2 && args[0] == "--desktop-info")
        {
            var host = Desktop.FindWallpaperHost();
            var icons = Task.Run(Desktop.IconRects).Result;
            var center = Screen.PrimaryScreen!.Bounds.Location + new Size(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2);
            File.WriteAllLines(args[1],
            [
                $"wallpaper host: 0x{host:X}",
                $"icon list view: 0x{Desktop.IconListView():X}",
                $"virtual screen: {SystemInformation.VirtualScreen}",
                $"desktop at primary centre: {Desktop.IsDesktopAt(center)}",
                $"icons: {icons.Count}",
                .. icons.Take(10).Select(r => $"  {r}"),
            ]);
            return;
        }

        ApplicationConfiguration.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"crash: {e.ExceptionObject}");
        Application.ThreadException += (_, e) => Log.Write($"error: {e.Exception}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // One Pawboard at a time, wallpaper or window: two would overwrite each other's saves.
        using var single = new Mutex(true, @"Local\Pawboard", out bool first);
        bool window = args.Contains("--window");
        if (!first)
        {
            Log.Write("already running; this launch exits");
            if (window) MessageBox.Show("Pawboard is already running.", "Pawboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // No paths here: they'd put the Windows user name into a file people may share.
        Log.Write($"start {Application.ProductVersion}{(window ? " (window)" : "")}");

        // The board in an ordinary window.
        if (window)
        {
            Application.Run(new BoardForm());
            return;
        }

        // Wallpaper mode: the board behind the desktop icons.
        var restarts = new Queue<DateTime>();
        while (true)
        {
            var board = new BoardForm(wallpaper: true);
            Application.Run(board);
            Log.Write($"board ended, exit requested: {board.ExitRequested}");
            if (board.ExitRequested) break;

            // Explorer restarted and took the wallpaper layer with it: wait for the desktop to be
            // back, then come back too. If that keeps happening, something is wrong: stop instead.
            restarts.Enqueue(DateTime.UtcNow);
            while (restarts.Count > 0 && DateTime.UtcNow - restarts.Peek() > TimeSpan.FromMinutes(10)) restarts.Dequeue();
            if (restarts.Count > 5)
            {
                Log.Write("too many restarts in 10 minutes; stopping");
                break;
            }
            for (int i = 0; i < 60 && Desktop.FindWallpaperHost(spawn: false) == 0; i++) Thread.Sleep(1000);
            Thread.Sleep(2000);
        }
        Desktop.RepaintWallpaper();
    }
}
