using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4b task 3: the settings window and its plain pages (General, Game, Display, Map, Dice &amp; Log, Toolbar, Folders). Headless only.</summary>
public sealed class SettingsWindowTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-settings-" + Guid.NewGuid().ToString("N"));
    readonly FakeDialogs _dialogs = new();
    readonly List<string> _notes = new();

    public SettingsWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    sealed class StubPage(string title) : SettingsPage
    {
        public string? Problem;
        public int Applied;
        public override string Title => title;
        public override void Load(Settings s) { }
        public override Settings Collect(Settings s) => s;
        public override string? Validate() => Problem;
        public override void AfterApply() => Applied++;
    }

    static readonly (string Id, string Text)[] Items = { ("save", "Save"), ("undo", "Undo"), ("dice", "Dice") };

    SettingsContext Ctx(Func<Difficulty?>? game = null, Action<Difficulty>? set = null, Func<string?>? open = null) =>
        new(Items, _root, open ?? (() => null), t => { _notes.Add(t); return Task.CompletedTask; }, _dialogs, game, set);

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    SettingsWindow Open(Settings s, Action<Settings>? apply = null, Func<Settings>? latest = null, SettingsContext? ctx = null, params SettingsPage[] extra)
    {
        var pages = SettingsWindow.StandardPages(ctx ?? Ctx(), new StubPage("Keyboard"));
        var w = new SettingsWindow(s, apply ?? (_ => { }), extra.Length > 0 ? pages.Concat(extra).ToList() : pages, latest, _dialogs);
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void Window_shape_and_page_order()
    {
        var w = Open(new Settings());
        Assert.Equal("Settings", w.Title);
        Assert.Equal((560, 400), (w.Width, w.Height));
        Assert.False(w.CanResize);
        var tabs = w.GetVisualDescendantsOf<TabItem>().Select(t => (string)t.Header!).ToArray();
        Assert.Equal(new[] { "General", "Game", "Display", "Map", "Dice & Log", "Toolbar", "Keyboard", "Folders" }, tabs);
        Assert.Equal(0, w.SelectedPage);
        w.SelectPage(3);
        Assert.Equal(3, w.SelectedPage);
        w.Close();
    }

    [AvaloniaFact]
    public void Round_trip_every_page()
    {
        var general = new Settings { ReopenLastModule = false, AutosaveMinutes = 17, ConfirmDelete = false, GameMasterMode = true, CheckUpdatesOnStart = false };
        var p1 = new GeneralPage();
        p1.Load(general);
        Assert.Equal(general, p1.Collect(new Settings()));

        var display = new Settings { GridDefault = GridMode.Never, LabelFontSize = 13, HighlightColor = "#112233", TabMinWidth = 120, TabMaxWidth = 300, AlertBarVisible = false };
        var p2 = new DisplayPage(Ctx());
        p2.Load(display);
        Assert.Equal(display, p2.Collect(new Settings()));

        var map = new Settings { WheelZoomStep = 1.35, SnapOnDrop = false, DragThresholdPx = 9, HoverDelayMs = 850 };
        var p3 = new MapPage();
        p3.Load(map);
        Assert.Equal(map, p3.Collect(new Settings()));

        var dice = new Settings { PhysicalDiceDefault = true, ShowLastRollInStatus = false, LogShowMoves = false, LogTimeFormat = "yyyy-MM-dd HH:mm" };
        var p4 = new DiceLogPage();
        p4.Load(dice);
        Assert.Equal(dice, p4.Collect(new Settings()));

        var toolbar = new Settings { ToolbarVisible = new Dictionary<string, bool> { ["save"] = false, ["undo"] = true, ["dice"] = false } };
        var p5 = new ToolbarPage(Items);
        p5.Load(toolbar);
        Assert.Equal(toolbar.ToolbarVisible.OrderBy(k => k.Key), p5.Collect(new Settings()).ToolbarVisible.OrderBy(k => k.Key));

        var game = new Settings { LastDifficulty = Difficulty.Hardcore };
        var p6 = new GamePage(Ctx(() => Difficulty.SemiAuto));
        p6.Load(game); // an open game shows its own difficulty
        Assert.Equal(Difficulty.SemiAuto, p6.Collect(new Settings()).LastDifficulty);
        var p7 = new GamePage(Ctx(() => Difficulty.FullyAuto));
        p7.Load(game);
        Assert.Equal(Difficulty.FullyAuto, p7.Collect(new Settings()).LastDifficulty);
    }

    [AvaloniaFact]
    public void Labels_are_the_winforms_texts()
    {
        var g = new GeneralPage();
        Assert.Equal("Select the last module in the launcher", g.Reopen.Content);
        Assert.Equal("Ask before deleting pieces", g.ConfirmDelete.Content);
        Assert.Equal("Game master mode (shows the counter palette)", g.GameMaster.Content);
        Assert.Equal("Check for updates on start (at most once a day, in the launcher)", g.Updates.Content);
        var d = new DiceLogPage();
        Assert.Equal("Physical dice: ask me for each die instead of rolling", d.Physical.Content);
        Assert.Equal("Show the last roll in the status bar", d.LastRoll.Content);
        Assert.Equal("Show move, place and delete rows in the log panel", d.Moves.Content);
        Assert.Equal("Show alert bar", new DisplayPage(Ctx()).AlertBar.Content);
        Assert.Equal("Snap pieces to the grid when dropped", new MapPage().Snap.Content);
    }

    [AvaloniaFact]
    public void Ranges_clamp_like_winforms()
    {
        var g = new GeneralPage();
        g.Load(new Settings { AutosaveMinutes = 999 });
        Assert.Equal(240, g.Autosave.Value);
        Assert.Equal(240, g.Collect(new Settings()).AutosaveMinutes);

        var m = new MapPage();
        Assert.Equal((1.05m, 2m, 0.05m), (m.Zoom.Minimum, m.Zoom.Maximum, m.Zoom.Increment));
        Assert.Equal((0m, 50m), (m.Drag.Minimum, m.Drag.Maximum));
        Assert.Equal((0m, 5000m, 50m), (m.Hover.Minimum, m.Hover.Maximum, m.Hover.Increment));
        m.Load(new Settings { WheelZoomStep = 9, DragThresholdPx = 99, HoverDelayMs = -4 });
        Assert.Equal((2m, 50m, 0m), (m.Zoom.Value, m.Drag.Value, m.Hover.Value));

        var d = new DisplayPage(Ctx());
        Assert.Equal((6m, 20m), (d.Label.Minimum, d.Label.Maximum));
        Assert.Equal((40m, 400m), (d.TabMin.Minimum, d.TabMin.Maximum));
        Assert.Equal((60m, 600m), (d.TabMax.Minimum, d.TabMax.Maximum));
        d.Load(new Settings { LabelFontSize = 99, TabMinWidth = 5, TabMaxWidth = 1000 });
        Assert.Equal((20m, 40m, 600m), (d.Label.Value, d.TabMin.Value, d.TabMax.Value));

        d.Load(new Settings());
        d.TabMin.Value = 200;
        d.TabMax.Value = 100;
        var c = d.Collect(new Settings());
        Assert.Equal((200, 200), (c.TabMinWidth, c.TabMaxWidth));
    }

    [AvaloniaFact]
    public void Invalid_time_format_keeps_old()
    {
        var p = new DiceLogPage();
        var w = new Window { Content = p };
        w.Show();
        var old = new Settings { LogTimeFormat = "HH:mm" };
        p.Load(old);
        Assert.NotEqual("(not a valid time format)", p.Preview.Text);
        p.Format.Text = "%%q";
        Assert.Equal("(not a valid time format)", p.Preview.Text);
        Assert.Equal("HH:mm", p.Collect(old).LogTimeFormat);
        p.Format.Text = "HH";
        Assert.Equal("HH", p.Collect(old).LogTimeFormat);
        w.Close();
    }

    [AvaloniaFact]
    public void Game_page_without_game_disabled()
    {
        var p = new GamePage(Ctx());
        p.Load(new Settings { LastDifficulty = Difficulty.FullyAuto });
        Assert.False(p.DifficultyBox.IsEnabled);
        Assert.Equal("Open a game first", p.Hint.Text);
        p.DifficultyBox.SelectedIndex = 0;
        Assert.Equal(Difficulty.SemiAuto, p.Collect(new Settings()).LastDifficulty);
        Assert.Equal("Players: comes with multiplayer (stage 5)", p.PlayersNote.Text);
    }

    [AvaloniaFact]
    public void Game_page_with_game_switches_after_apply()
    {
        var switched = new List<Difficulty>();
        var p = new GamePage(Ctx(() => Difficulty.SemiAuto, switched.Add));
        p.Load(new Settings());
        Assert.True(p.DifficultyBox.IsEnabled);
        Assert.Equal("A change takes effect from the next calculation.", p.Hint.Text);
        Assert.Equal(1, p.DifficultyBox.SelectedIndex);
        p.DifficultyBox.SelectedIndex = 2;
        Assert.Equal(Difficulty.FullyAuto, p.Collect(new Settings()).LastDifficulty);
        Assert.Empty(switched);
        p.AfterApply();
        Assert.Equal(new[] { Difficulty.FullyAuto }, switched);
    }

    [AvaloniaFact]
    public void Display_choose_uses_the_colour_dialog()
    {
        var w = Open(new Settings { HighlightColor = "#112233" });
        w.SelectPage(2);
        Dispatcher.UIThread.RunJobs();
        var d = w.GetVisualDescendantsOf<DisplayPage>().Single();
        _dialogs.Colours.Enqueue("#ABCDEF");
        Click(d.Choose);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "#112233" }, _dialogs.ColourPrompts);
        Assert.Equal("#ABCDEF", d.Collect(new Settings()).HighlightColor);
        _dialogs.Colours.Enqueue(null);
        Click(d.Choose);
        Assert.Equal("#ABCDEF", d.Colour);
        w.Close();
    }

    [AvaloniaFact]
    public void Restore_defaults_resets_only_current_page()
    {
        var s = new Settings { AutosaveMinutes = 17, WheelZoomStep = 1.5 };
        Settings? applied = null;
        var w = Open(s, x => applied = x);
        w.SelectPage(3); // Map
        Click(w.RestoreButton);
        var c = w.CollectForTest();
        Assert.Equal(1.2, c.WheelZoomStep);
        Assert.Equal(17, c.AutosaveMinutes);
        Assert.Null(applied); // takes effect when applied
        Click(w.ApplyButton);
        Assert.Equal(1.2, applied!.WheelZoomStep);
        w.Close();
    }

    [AvaloniaFact]
    public void Ok_applies_and_closes()
    {
        Settings? applied = null;
        var w = Open(new Settings(), x => applied = x);
        var closed = false;
        w.Closed += (_, _) => closed = true;
        w.GetVisualDescendantsOf<GeneralPage>().Single().Autosave.Value = 30;
        Click(w.OkButton);
        WaitFor(() => closed);
        Assert.Equal(30, applied!.AutosaveMinutes);
    }

    [AvaloniaFact]
    public void Apply_stays_open()
    {
        var applied = new List<Settings>();
        var w = Open(new Settings(), applied.Add);
        var closed = false;
        w.Closed += (_, _) => closed = true;
        Click(w.ApplyButton);
        WaitFor(() => applied.Count == 1);
        Assert.False(closed);
        Assert.True(w.IsVisible);
        w.Close();
    }

    [AvaloniaFact]
    public void Cancel_after_apply_keeps_applied()
    {
        var applied = new List<Settings>();
        var w = Open(new Settings(), applied.Add);
        w.GetVisualDescendantsOf<GeneralPage>().Single().Autosave.Value = 41;
        Click(w.ApplyButton);
        WaitFor(() => applied.Count == 1);
        w.GetVisualDescendantsOf<GeneralPage>().Single().Autosave.Value = 99;
        var closed = false;
        w.Closed += (_, _) => closed = true;
        Click(w.CancelButton);
        WaitFor(() => closed);
        Assert.Single(applied);
        Assert.Equal(41, applied[0].AutosaveMinutes);
    }

    [AvaloniaFact]
    public async Task Commit_merges_onto_latest()
    {
        var live = new Settings { RecentSaves = new Dictionary<string, IReadOnlyList<string>> { ["abc"] = new[] { "a.sav" } } };
        Settings? applied = null;
        var w = Open(new Settings(), x => applied = x, () => live);
        Assert.True(await w.CommitAsync());
        Assert.Equal(new[] { "a.sav" }, applied!.RecentSaves["abc"]);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Validate_problem_blocks_commit_and_selects_page()
    {
        var bad = new StubPage("Bad") { Problem = "Two commands share Ctrl+Q." };
        var applied = 0;
        var w = Open(new Settings(), _ => applied++, extra: bad);
        Assert.False(await w.CommitAsync());
        Assert.Equal(8, w.SelectedPage);
        Assert.Equal(0, applied);
        Assert.Equal(0, bad.Applied);
        Assert.Equal(new[] { ("Two commands share Ctrl+Q.", "Settings") }, _dialogs.Warnings);
        bad.Problem = null;
        Assert.True(await w.CommitAsync());
        Assert.Equal((1, 1), (applied, bad.Applied));
        w.Close();
    }

    string MakeModuleCache(char c, string sub, int bytes)
    {
        var dir = Path.Combine(_root, new string(c, 64));
        Directory.CreateDirectory(Path.Combine(dir, sub));
        File.WriteAllBytes(Path.Combine(dir, sub, "f.bin"), new byte[bytes]);
        return dir;
    }

    [AvaloniaFact]
    public void Folders_sizes_load_off_ui_thread()
    {
        MakeModuleCache('a', "tiles", 2 * 1048576);
        MakeModuleCache('b', "docs", 1048576 + 524288);
        var w = Open(new Settings());
        var f = w.GetVisualDescendantsOf<FoldersPage>().SingleOrDefault();
        Assert.Null(f); // not shown yet: not computed yet
        w.SelectPage(7);
        Dispatcher.UIThread.RunJobs();
        f = w.GetVisualDescendantsOf<FoldersPage>().Single();
        WaitFor(() => f.SizesLoaded);
        Assert.Equal("Tile cache: 2.0 MB     Document cache: 1.5 MB", f.Sizes.Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Folders_clear_skips_open_module()
    {
        var open = MakeModuleCache('a', "tiles", 1048576);
        MakeModuleCache('b', "tiles", 1048576);
        var w = Open(new Settings(), ctx: Ctx(open: () => open));
        w.SelectPage(7);
        Dispatcher.UIThread.RunJobs();
        var f = w.GetVisualDescendantsOf<FoldersPage>().Single();
        WaitFor(() => f.SizesLoaded);
        Click(f.ClearTiles);
        WaitFor(() => _notes.Count == 1);
        Assert.Equal("Cleared 1.0 MB of cached tiles.\nThe open module's files are in use and were left alone; close the module to clear them.", _notes[0]);
        Assert.True(Directory.Exists(Path.Combine(open, "tiles")));
        Assert.False(Directory.Exists(Path.Combine(_root, new string('b', 64), "tiles")));
        WaitFor(() => f.SizesLoaded && f.Sizes.Text == "Tile cache: 1.0 MB     Document cache: 0.0 MB");
        w.Close();
    }

    [AvaloniaFact]
    public void Folders_result_dropped_after_close()
    {
        var gate = new ManualResetEventSlim();
        var folders = new FoldersPage(Ctx()) { SizesOf = _ => { gate.Wait(); return (1048576, 0); } };
        var w = Open(new Settings(), extra: folders);
        w.SelectPage(8);
        Dispatcher.UIThread.RunJobs();
        w.Close();
        gate.Set();
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();
        Assert.False(folders.SizesLoaded);
        Assert.Equal("Calculating sizes...", folders.Sizes.Text);
    }
}

static class VisualExt
{
    public static IEnumerable<T> GetVisualDescendantsOf<T>(this Avalonia.Visual v) where T : class =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v).OfType<T>();
}
