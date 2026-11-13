using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--screenshot-main", StringComparer.Ordinal)) return RunScreenshot(args);
        if (args.Any(a => LauncherShotFlags.Contains(a, StringComparer.Ordinal))) return RunLauncherScreenshot(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static readonly string[] LauncherShotFlags = { "--screenshot-launcher", "--screenshot-offline", "--screenshot-update-bar" };

    /// <summary>
    /// <c>&lt;vmod&gt; [--screenshot-launcher &lt;png&gt;] [--screenshot-offline &lt;png&gt;] [--screenshot-update-bar &lt;png&gt;]</c> (WinForms LauncherShots):
    /// the pre-game windows drawn headless. These runs write a module library, saves and settings, so they refuse unless
    /// <c>QUARTERMASTER_LAYOUT_ROOT</c> points at a scratch folder. The module is listed; the offline picture also gets an autosave (turn 3,
    /// Movement) and a recent save; the update-bar picture runs the launcher's normal check against a TEST folder feed offering 9.9.9 written
    /// under the scratch root (dummy assets, never installable; nothing reaches the network). Returns 0, or 1 on any failure (message on stderr).
    /// </summary>
    internal static int RunLauncherScreenshot(string[] args)
    {
        string? module = null, launcherPng = null, offlinePng = null, barPng = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot-launcher" when i + 1 < args.Length: launcherPng = args[++i]; break;
                case "--screenshot-offline" when i + 1 < args.Length: offlinePng = args[++i]; break;
                case "--screenshot-update-bar" when i + 1 < args.Length: barPng = args[++i]; break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) && module == null) module = args[i];
                    else Console.Error.WriteLine("Ignored argument: " + args[i]);
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRoot.EnvVar)))
        {
            Console.Error.WriteLine(AppDataRoot.EnvVar + " must be set for launcher screenshots.");   // before anything is read or written
            return 1;
        }
        if (module == null || (launcherPng ?? offlinePng ?? barPng) == null)
        {
            Console.Error.WriteLine("Usage: <vmod> [--screenshot-launcher <png>] [--screenshot-offline <png>] [--screenshot-update-bar <png>]");
            return 1;
        }
        if (Application.Current == null)
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        try
        {
            var root = AppDataRoot.Path;
            var store = new SettingsStore(root);
            store.Load(null);
            SeedLauncher(store, root, module, offlinePng != null);
            var ok = true;
            if (launcherPng != null) ok &= ShotLauncher(store, root, launcherPng, null);
            if (offlinePng != null) ok &= ShotOffline(store, root, offlinePng);
            if (barPng != null) ok &= ShotUpdateBar(store, root, barPng);
            return ok ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or DllNotFoundException or TypeInitializationException or TimeoutException or NotSupportedException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("launcher screenshot failed: " + e.Message);
            return 1;
        }
    }

    /// <summary>Screenshot fixture (WinForms LauncherShots.Seed): lists the module; for the offline window also an autosave and a recent save.</summary>
    static void SeedLauncher(SettingsStore store, string root, string modulePath, bool withGames)
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

    static bool ShotLauncher(SettingsStore store, string root, string png, Updates.DesktopUpdates? updates)
    {
        var w = new Launcher.LauncherWindow(store, root, new QuietDialogs(), null, updates);
        try
        {
            w.Show();
            if (!PumpUntil(() => w.RefreshTask.IsCompleted && (updates == null || updates.CheckTask.IsCompleted), 30000))
            {
                Console.Error.WriteLine("Timed out waiting for the launcher.");
                return false;
            }
            if (updates != null)
            {
                var text = updates.Bar?.Message;
                var actions = updates.Bar == null ? "" : string.Join(" | ", updates.Bar.Actions);
                if (text != "Quartermaster 9.9.9 is available." || actions != "What's new | Update now | Later | Skip this version" || updates.LastCheckUtc == null)
                {
                    Console.Error.WriteLine($"update bar FAILED ('{text}' [{actions}])");
                    return false;
                }
            }
            return Save(w, png);
        }
        finally { w.Close(); }
    }

    static bool ShotOffline(SettingsStore store, string root, string png)
    {
        var lib = new ModuleLibrary(store);
        Task.Run(() => lib.RefreshAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
        if (lib.Rows.FirstOrDefault(r => !r.Missing) is not { } row) { Console.Error.WriteLine("No module in the library."); return false; }
        var w = new Launcher.PlayOfflineWindow(row, lib, new RecentGames(store, root), store, new QuietDialogs(), Path.Combine(root, "cache"));
        try
        {
            w.Show();
            PumpUntil(() => false, 300);
            return Save(w, png);
        }
        finally { w.Close(); }
    }

    /// <summary>A TEST folder feed offering 9.9.9 under the scratch root, pointed to by QUARTERMASTER_UPDATE_FEED for this run (as WinForms
    /// ShotUpdateBar); the launcher runs its normal automatic check against it and is drawn with the bar. The variable is put back afterwards.</summary>
    static bool ShotUpdateBar(SettingsStore store, string root, string png)
    {
        var feed = Path.Combine(root, "test-update-feed");
        WriteTestFeed(feed, "9.9.9");
        var envVar = Quartermaster.Render.Update.UpdateFeeds.EnvVar;
        var previous = Environment.GetEnvironmentVariable(envVar);
        Environment.SetEnvironmentVariable(envVar, feed);
        try
        {
            store.Update(s => s with { CheckUpdatesOnStart = true });
            var updates = Updates.DesktopUpdates.FromEnvironment(root);
            if (updates.Feed == null) { Console.Error.WriteLine("The test feed was not picked up."); return false; }
            return ShotLauncher(store, root, png, updates);
        }
        finally { Environment.SetEnvironmentVariable(envVar, previous); }
    }

    /// <summary>A release.json in the GitHub API shape plus a dummy zip and its .sha256 (TEST content, not a real build; WinForms LauncherShots.WriteTestFeed).</summary>
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

    static bool Save(Avalonia.Controls.Window w, string png)
    {
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }   // first frame lays out the final size
        Dispatcher.UIThread.RunJobs();
        using var frame = w.CaptureRenderedFrame();
        if (frame == null) { Console.Error.WriteLine("No frame was rendered."); return false; }
        if (Path.GetDirectoryName(Path.GetFullPath(png)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        frame.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return File.Exists(png) && new FileInfo(png).Length > 0;
    }

    static bool PumpUntil(Func<bool> done, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return done();
    }

    /// <summary>The app builder; also used by the Avalonia designer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    /// <summary>
    /// <c>--screenshot-main &lt;png&gt;</c>: draws the main window headless (Avalonia.Headless with Skia drawing; no real window on any OS)
    /// for <c>&lt;vmod&gt; [--setup N|--new]</c> (default: a new game), writes the PNG and returns 0 (1 on any failure, message on stderr).
    /// Runs inside an already running Avalonia app (tests) or starts its own headless one.
    /// </summary>
    internal static int RunScreenshot(string[] args)
    {
        var warnings = new List<string>();
        var startup = StartupArgs.Parse(args, warnings.Add);
        foreach (var w in warnings) Console.Error.WriteLine(w);
        if (startup.ScreenshotPath is not { } png || startup.ModulePath is not { } module)
        {
            Console.Error.WriteLine("Usage: <vmod> --screenshot-main <png> [--setup N|--new] [--rules <qrules>] [--screenshot-state menu|hover|los|log|rules|phases|nextphase|stepback|formula|cheatsheet|batch|alerts|tree|floatlog|floatphases|tour|handover|players-page|settings-players|player-menu]");
            return 1;
        }
        if (Application.Current == null)
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();

        var cache = Path.Combine(Path.GetTempPath(), "qm-shot-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(new QuietDialogs(), cache, null) { Width = 1280, Height = 800 };
            window.Show();
            if (!window.OpenModule(module)) return 1;
            // No chooser without a window to answer it: the default is a new game from the module.
            if (startup.RulesPath != null) window.Session?.OpenRules(startup.RulesPath);
            else if (startup.NewRulesName != null) window.Session?.NewRules(startup.NewRulesName);
            if (startup.SetupIndex is { } idx) window.StartSetup(idx); else window.StartSetup(null);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!window.MapTabs.WhenTiled().IsCompleted)
            {
                if (DateTime.UtcNow > until) { Console.Error.WriteLine("Timed out preparing the map."); return 1; }
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame()) { } // first frame lays out the final size
            Dispatcher.UIThread.RunJobs();
            if (startup.ScreenshotState is { } state && ScreenshotStates.Apply(state, window) is { } problem) { Console.Error.WriteLine(problem); return 1; }
            using var frame = (ScreenshotStates.Target ?? window).CaptureRenderedFrame();
            if (frame == null) { Console.Error.WriteLine("No frame was rendered."); return 1; }
            if (Path.GetDirectoryName(Path.GetFullPath(png)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            frame.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return File.Exists(png) && new FileInfo(png).Length > 0 ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
            or DllNotFoundException or TypeInitializationException or TimeoutException or NotSupportedException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        finally
        {
            if (window != null) ScreenshotStates.Cleanup(window);
            window?.ForceClose();
            try { Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Dialogs for the screenshot run: nothing is ever shown; warnings go to stderr, questions are answered No.</summary>
    sealed class QuietDialogs : IDialogs
    {
        public Task<string?> OpenFileAsync(Avalonia.Controls.Window owner, string title, IReadOnlyList<FileFilter> filters, string? initialFolder = null) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> OpenFilesAsync(Avalonia.Controls.Window owner, string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> AskShortcutAsync(Avalonia.Controls.Window owner, string commandLabel) => Task.FromResult<string?>(null);
        public Task<string?> AskColourAsync(Avalonia.Controls.Window owner, string initialHex) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(Avalonia.Controls.Window owner, string title, FileFilter filter, string defaultExtension, string? suggestedFileName = null) => Task.FromResult<string?>(null);
        public Task<bool> AskYesNoAsync(Avalonia.Controls.Window owner, string message, string caption, bool defaultNo = true) => Task.FromResult(false);
        public Task<IReadOnlyList<string>?> AskColumnsAsync(Avalonia.Controls.Window owner, IReadOnlyList<string> keys) => Task.FromResult<IReadOnlyList<string>?>(null);
        /// <summary>A scanned grid (tsvOnly) is confirmed with the default checkboxes, as the WinForms smoke run; a pasted one is cancelled.</summary>
        public Task<Quartermaster.Rules.Tables.PastedGrid?> PasteTableAsync(Avalonia.Controls.Window owner, string text, string? title = null, string? targetDescription = null, bool tsvOnly = false)
        {
            if (!tsvOnly) return Task.FromResult<Quartermaster.Rules.Tables.PastedGrid?>(null);
            var g = Quartermaster.Rules.Tables.TablePaste.Parse(text, true, true, tsvOnly: true);
            var ok = g.Cells.Count > 0 && Quartermaster.Rules.Tables.TablePaste.Problem(g, true) == null;
            return Task.FromResult<Quartermaster.Rules.Tables.PastedGrid?>(ok ? g : null);
        }
        public Task<(int Count, string Label)?> AskBlockAsync(Avalonia.Controls.Window owner) => Task.FromResult<(int Count, string Label)?>(null);
        public Task<bool?> AskImportTargetAsync(Avalonia.Controls.Window owner, string selectedTable) => Task.FromResult<bool?>(false);   // a new table
        public Task<string?> GetClipboardTextAsync(Avalonia.Controls.Window owner) => Task.FromResult<string?>(null);
        public Task SetClipboardTextAsync(Avalonia.Controls.Window owner, string text) => Task.CompletedTask;
        public Task ShowInfoAsync(Avalonia.Controls.Window owner, string message, string caption)
        {
            Console.Error.WriteLine(message);
            return Task.CompletedTask;
        }
        public Task<bool?> AskYesNoCancelAsync(Avalonia.Controls.Window owner, string message, string caption) => Task.FromResult<bool?>(null);
        public Task<string?> AskTextAsync(Avalonia.Controls.Window owner, string title, string prompt, string initial) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>?> EditPropertySheetAsync(Avalonia.Controls.Window owner, string title,
            IReadOnlyList<(string Name, string Value)> rows, Func<string, string?>? managedValue) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task ShowWarningAsync(Avalonia.Controls.Window owner, string message, string caption)
        {
            Console.Error.WriteLine(message);
            return Task.CompletedTask;
        }
        public Task<SetupChoice?> ChooseSetupAsync(Avalonia.Controls.Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty,
            Quartermaster.Rules.RulesPack? pack) => Task.FromResult<SetupChoice?>(null);
        public Task<NewGameRequest?> ChooseOnlineSetupAsync(Avalonia.Controls.Window owner, ModuleModel model, Difficulty difficulty,
            Quartermaster.Rules.RulesPack pack, string hostName, string lobbyName) => Task.FromResult<NewGameRequest?>(null);
        public Task<DiceChoice?> AskDiceAsync(Avalonia.Controls.Window owner, string title, bool multi) => Task.FromResult<DiceChoice?>(null);
        public Task<TurnChoice?> AskTurnAsync(Avalonia.Controls.Window owner, TurnState current) => Task.FromResult<TurnChoice?>(null);
        public Task<bool> ConfirmOpenDecisionsAsync(Avalonia.Controls.Window owner, IReadOnlyList<Decision> open, Action<Decision>? jumpTo = null) =>
            Task.FromResult(false);   // Go back
        public Task<Quartermaster.Render.MissingInputResult> AskMissingAsync(Avalonia.Controls.Window owner, Quartermaster.Render.ModuleSession s, Quartermaster.Render.IActionPort port, Quartermaster.Core.PendingRun run) => Task.FromResult(Quartermaster.Render.MissingInputResult.Cancel);
        public Task<Quartermaster.Render.CheckpointRow?> PickCheckpointAsync(Avalonia.Controls.Window owner, IReadOnlyList<Quartermaster.Render.CheckpointRow> rows, bool confirmOnly) =>
            Task.FromResult<Quartermaster.Render.CheckpointRow?>(null);   // Cancel
        public Task<ExportChoice?> AskExportChoiceAsync(Avalonia.Controls.Window owner, string mapName) => Task.FromResult<ExportChoice?>(null);
        public Task<IReadOnlyList<Quartermaster.Core.UserLedgerTab>?> EditLedgerTabsAsync(Avalonia.Controls.Window owner, IReadOnlyList<Quartermaster.Core.UserLedgerTab> tabs,
            Quartermaster.Rules.RulesPack? pack, int select = 0, bool addNew = false) => Task.FromResult<IReadOnlyList<Quartermaster.Core.UserLedgerTab>?>(null);
        public void OpenInBrowser(Avalonia.Controls.TopLevel? owner, string path) { }
        public Task<IReadOnlyList<string>?> ChooseSidesAsync(Avalonia.Controls.Window owner, string title, string prompt, IReadOnlyList<Quartermaster.Rules.SideDef> sides,
            IReadOnlyCollection<string>? ticked = null, string? note = null) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<Quartermaster.Desktop.Online.JoinChoice?> AskJoinAsync(Avalonia.Controls.Window owner, Quartermaster.Render.LobbyRow row, string playerName) =>
            Task.FromResult<Quartermaster.Desktop.Online.JoinChoice?>(null);
        public Task<Quartermaster.Desktop.Online.HostSavedChoice?> AskHostSavedAsync(Avalonia.Controls.Window owner, Quartermaster.Rules.RulesPack pack, string playerName,
            string lobbyName, IReadOnlyCollection<string> hostSides) => Task.FromResult<Quartermaster.Desktop.Online.HostSavedChoice?>(null);
        public Task ShowSeatsAsync(Avalonia.Controls.Window owner, Quartermaster.Render.HostSession host, Quartermaster.Rules.RulesPack? pack) => Task.CompletedTask;
        public Task<Quartermaster.Desktop.Turns.SendTurnChoice?> AskSendTurnAsync(Avalonia.Controls.Window owner, string fromName, IReadOnlyList<Quartermaster.Core.PlayerDef> players,
            string localPlayerId, Quartermaster.Rules.RulesPack? pack, Quartermaster.Render.MailboxValues mailbox) => Task.FromResult<Quartermaster.Desktop.Turns.SendTurnChoice?>(null);
        public Task<Quartermaster.Desktop.Turns.WhoChoice?> AskWhoAreYouAsync(Avalonia.Controls.Window owner, string prompt, IReadOnlyList<Quartermaster.Core.PlayerDef> players,
            Quartermaster.Rules.RulesPack? pack, string? preselect, Quartermaster.Render.MailboxValues? mailbox) => Task.FromResult<Quartermaster.Desktop.Turns.WhoChoice?>(null);
        public Task<int> ChooseAsync(Avalonia.Controls.Window owner, string message, string caption, IReadOnlyList<string> buttons, int defaultIndex) => Task.FromResult(-1);
        public Task<string?> AskPasswordAsync(Avalonia.Controls.Window owner, string caption, string prompt) => Task.FromResult<string?>(null);
        public Task<bool> WaitForAsync(Avalonia.Controls.Window owner, Task work, TimeSpan limit) => Task.FromResult(false);
    }
}
