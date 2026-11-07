using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Screenshot runs for the launcher windows (--screenshot-launcher / --screenshot-offline). The windows are shown off-screen and drawn to a PNG.</summary>
static class LauncherShots
{
    public static void Run(StartupOptions opts)
    {
        try
        {
            RequireScratchRoot(); // before anything is read or written: these runs write a library, saves and settings
            var root = SettingsStore.DefaultRoot;
            var store = new SettingsStore(root);
            store.Load(null);
            if (opts.ModulePath != null) Seed(store, root, opts.ModulePath, opts.ScreenshotOffline != null);
            if (opts.ScreenshotLauncher is { } l) ShotLauncher(store, root, l);
            if (opts.ScreenshotOffline is { } o) ShotOffline(store, root, o);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher screenshot failed: " + ex.Message);
            Program.ExitCode = 1;
        }
    }

    /// <summary>Screenshot fixture: lists the given module plus a copy that is then deleted (shown as missing); for the offline window also writes an autosave and a recent save.</summary>
    static void Seed(SettingsStore store, string root, string modulePath, bool withGames)
    {
        var lib = new ModuleLibrary(store);
        lib.Add(modulePath);
        var gone = Path.Combine(root, "gone", "Vanished Module.vmod");
        Directory.CreateDirectory(Path.GetDirectoryName(gone)!);
        File.Copy(modulePath, gone, true);
        lib.Add(gone);
        File.Delete(gone);
        Task.Run(() => lib.RefreshAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
        if (!withGames) return;
        var sha = lib.Rows.First(r => !r.Missing).Entry.Sha256!;
        var dir = Path.Combine(Autosaver.AutosaveRoot(root), sha);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "autosave-20260101-000000000-t3.qsave"),
            "{\"format\":7,\"moduleSha256\":\"" + sha + "\",\"snapshot\":{\"turn\":{\"turn\":3,\"phase\":\"Movement\"},\"pieces\":{}},\"log\":[]}");
        var save = Path.Combine(root, "saves", "Campaign one.qsave");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllText(save, "{}");
        new RecentGames(store, root).Add(sha, save);
        new RecentGames(store, root).Add(sha, Path.Combine(root, "saves", "Old game.qsave")); // not on disk: shown as not found
    }

    /// <summary>--smoke-launcher: the first launcher visit starts a blank new game on the module, the second one checks that it lists a last-played time and ends the app.</summary>
    public static Func<LaunchRequest?> SmokeHook(string modulePath)
    {
        RequireScratchRoot();
        var visits = 0;
        return () =>
        {
            var root = SettingsStore.DefaultRoot;
            var store = new SettingsStore(root);
            store.Load(null);
            if (++visits == 1)
            {
                var lib = new ModuleLibrary(store);
                lib.Add(modulePath);
                Task.Run(() => lib.RefreshAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
                Program.AppLog("smoke: launcher listed " + lib.Rows.Count + " module(s); starting a new game");
                return new LaunchRequest(Path.GetFullPath(modulePath), null, LaunchKind.NewGame,
                    new NewGameRequest(GameMode.Offline, null, store.Current.LastDifficulty, null, null, null), null);
            }
            using var form = new LauncherForm(store, root);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show();
            Pump(() => form.RefreshTask.IsCompleted);
            var text = form.FirstLastPlayedText;
            var ok = !string.IsNullOrWhiteSpace(text);
            Program.AppLog($"smoke: launcher after the game {(ok ? "ok" : "FAILED")} (last played '{text}')");
            if (!ok) Program.ExitCode = 1;
            form.Hide();
            return null;
        };
    }

    /// <summary>These runs write a library, saves and settings: refuse unless the layout root was redirected, so the real one is never touched.</summary>
    static void RequireScratchRoot()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT")))
            throw new InvalidOperationException("QUARTERMASTER_LAYOUT_ROOT must be set for launcher smokes and screenshots.");
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
