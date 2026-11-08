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
            if (AppUpdates.ScreenshotBarPath is { } u) ShotUpdateBar(store, root, u);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher screenshot failed: " + ex.Message);
            Program.ExitCode = 1;
        }
    }

    /// <summary>Screenshot fixture: lists the given module; for the offline window also writes an autosave and a recent save.</summary>
    static void Seed(SettingsStore store, string root, string modulePath, bool withGames)
    {
        var lib = new ModuleLibrary(store);
        lib.Add(modulePath);
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
            using var form = new LauncherForm(store, root, checkForUpdates: false);
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
        using var form = new LauncherForm(store, root, checkForUpdates: false);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Pump(() => form.RefreshTask.IsCompleted);
        Shot(form, path);
        form.Hide();
    }

    /// <summary>
    /// --screenshot-update-bar: a TEST folder feed offering 9.9.9 (dummy assets, never installable) under the scratch root, pointed to by
    /// QUARTERMASTER_UPDATE_FEED for this process; the launcher runs its normal check against it and is drawn with the bar.
    /// </summary>
    static void ShotUpdateBar(SettingsStore store, string root, string path)
    {
        var feed = Path.Combine(root, "test-update-feed");
        WriteTestFeed(feed, "9.9.9");
        Environment.SetEnvironmentVariable(Quartermaster.Render.Update.UpdateFeeds.EnvVar, feed);
        store.Update(s => s with { CheckUpdatesOnStart = true, LastUpdateCheckUtc = null, SkippedVersion = null });
        using var form = new LauncherForm(store, root);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Show();
        Pump(() => form.RefreshTask.IsCompleted && form.UpdateCheckTask.IsCompleted);
        var text = form.UpdateBarText;
        var actions = string.Join(" | ", form.UpdateBarActions);
        var ok = text == "Quartermaster 9.9.9 is available." && actions == "What's new | Update now | Later | Skip this version"
                 && store.Current.LastUpdateCheckUtc != null;
        Program.AppLog($"smoke: update bar {(ok ? "ok" : "FAILED")} ('{text}' [{actions}])");
        if (!ok) Program.ExitCode = 1;
        Shot(form, path);
        form.Hide();
    }

    /// <summary>A release.json in the GitHub API shape plus a dummy zip and its .sha256 (TEST content, not a real build).</summary>
    static void WriteTestFeed(string dir, string version)
    {
        Directory.CreateDirectory(dir);
        var zipName = $"Quartermaster-{version}-win-x64.zip";
        var zip = System.Text.Encoding.UTF8.GetBytes("TEST dummy update asset - not a Quartermaster build");
        File.WriteAllBytes(Path.Combine(dir, zipName), zip);
        File.WriteAllText(Path.Combine(dir, zipName + ".sha256"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(zip)) + "  " + zipName + "\n");
        var tag = "quartermaster-v" + version;
        var download = $"https://github.com/EmojiPlay88/Quartermaster/releases/download/{tag}/";
        var release = new Dictionary<string, object>
        {
            ["tag_name"] = tag,
            ["name"] = "TEST " + version,
            ["draft"] = false,
            ["prerelease"] = false,
            ["html_url"] = "https://github.com/EmojiPlay88/Quartermaster/releases/tag/" + tag,
            ["body"] = "TEST release notes for the update bar screenshot.\n\n- TEST: an invented change\n- TEST: another invented change",
            ["assets"] = new object[]
            {
                new Dictionary<string, object> { ["name"] = zipName, ["size"] = zip.Length, ["browser_download_url"] = download + zipName },
                new Dictionary<string, object> { ["name"] = zipName + ".sha256", ["size"] = 64 + 2 + zipName.Length + 1, ["browser_download_url"] = download + zipName + ".sha256" },
            },
        };
        File.WriteAllText(Path.Combine(dir, "release.json"), System.Text.Json.JsonSerializer.Serialize(release));
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
