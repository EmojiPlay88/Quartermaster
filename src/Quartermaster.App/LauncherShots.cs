using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Screenshot runs for the launcher windows (--screenshot-launcher / --screenshot-offline). The windows are shown off-screen and drawn to a PNG.</summary>
static class LauncherShots
{
    public static void Run(StartupOptions opts)
    {
        try
        {
            var root = SettingsStore.DefaultRoot;
            var store = new SettingsStore(root);
            store.Load(null);
            if (opts.ScreenshotLauncher is { } l) ShotLauncher(store, root, l);
            if (opts.ScreenshotOffline is { } o) ShotOffline(store, root, o);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher screenshot failed: " + ex.Message);
            Program.ExitCode = 1;
        }
    }

    static void Pump(Func<bool> done, int maxMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        for (var i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }

    static void Shot(Form f, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: launcher screenshot saved to " + path);
    }

    static void ShotLauncher(SettingsStore store, string root, string path)
    {
        using var form = new LauncherForm(store, root);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Pump(() => form.RefreshTask.IsCompleted);
        Shot(form, path);
        form.Hide();
    }

    static void ShotOffline(SettingsStore store, string root, string path)
    {
        var lib = new ModuleLibrary(store);
        Task.Run(() => lib.RefreshAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
        var row = lib.Rows.FirstOrDefault(r => !r.Missing);
        if (row == null) { Program.AppLog("launcher screenshot: no module in the library"); Program.ExitCode = 1; return; }
        using var form = new PlayOfflineForm(row, lib, new RecentGames(store, root), store);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Pump(() => false, 200);
        Shot(form, path);
        form.Hide();
    }
}
