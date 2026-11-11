using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4b task 5: the main window's settings store (under the layout root), Tools > Settings and live application of every setting.</summary>
[Collection("MacFlag")]
public sealed class SettingsApplyTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-settings-apply-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public SettingsApplyTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static bool WaitFor(Func<bool> condition, int ms = 30000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Pump();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Pump();
        return condition();
    }

    void WriteSettings(string json)
    {
        Directory.CreateDirectory(LayoutRoot);
        File.WriteAllText(Path.Combine(LayoutRoot, "settings.json"), json);
    }

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>The minimal fixture with a second map (two map tabs) and a setup "Start" with piece p1 on Main Map.</summary>
    string TwoMaps()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.Map mapName=\"Second Map\"><VASSAL.build.module.map.BoardPicker>" +
            "<VASSAL.build.module.map.boardPicker.Board image=\"board.png\" name=\"Board Two\" reversible=\"false\"/>" +
            "</VASSAL.build.module.map.BoardPicker></VASSAL.build.module.Map>" +
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        var add = "+/" + SequenceEncoder.Encode(new[] { "p1", "piece;;;unit.svg;A", "Main Map;100;50;1;0" }, '/');
        var fx = new FixtureModule().WithBuildFile(xml).AddSave("s.vsav", new[] { "begin_save", "", add, "end_save" });
        _fixtures.Add(fx);
        return fx.Build();
    }

    MainWindow Game(string? module = null)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(module ?? TwoMaps()));
        w.Session!.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        w.StartSetup(0);
        Assert.True(WaitFor(() => w.MapTabs.WhenTiled().IsCompleted));
        Pump();
        return w;
    }

    static void Save(MainWindow w, Func<Settings, Settings> change)
    {
        w.SettingsStore.Save(change(w.SettingsStore.Current));
        Pump();
    }

    /// <summary>Collects the "apply settings: " trace lines written while it is alive.</summary>
    sealed class TraceLines : TraceListener
    {
        public readonly List<string> Lines = new();
        public TraceLines() => Trace.Listeners.Add(this);
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message?.StartsWith("apply settings: ", StringComparison.Ordinal) == true) lock (Lines) Lines.Add(message); }
        protected override void Dispose(bool disposing) { Trace.Listeners.Remove(this); base.Dispose(disposing); }
    }

    // ---------- the store ----------

    [AvaloniaFact]
    public void Store_loads_from_layout_root()
    {
        WriteSettings("""{ "WheelZoomStep": 1.7, "HoverDelayMs": 250 }""");
        var w = Game();
        Assert.Equal(LayoutRoot, w.SettingsStore.Root);
        Assert.Equal(1.7, w.SettingsStore.Current.WheelZoomStep);
        Assert.All(w.MapTabs.Views, v => Assert.Equal(1.7, v.ZoomStep));
        Assert.All(w.MapTabs.Views, v => Assert.Equal(250, v.HoverDelayMs));
    }

    [AvaloniaFact]
    public void Never_touches_app_data()
    {
        var appData = Path.Combine(AppDataRoot.Path, "settings.json");
        var before = File.Exists(appData) ? File.GetLastWriteTimeUtc(appData) : (DateTime?)null;
        var w = Game();
        SettingsWindow? shown = null;
        w.ShowSettingsHook = win => { shown = win; return Task.CompletedTask; };
        w.ClickMenu("&Settings...");
        Pump();
        Assert.NotNull(shown);
        Assert.True(shown!.CommitAsync().GetAwaiter().GetResult());
        Save(w, s => s with { AutosaveMinutes = 9 });
        Assert.True(File.Exists(Path.Combine(LayoutRoot, "settings.json")));
        Assert.Equal(before, File.Exists(appData) ? File.GetLastWriteTimeUtc(appData) : null);
    }

    // ---------- shortcuts ----------

    [AvaloniaFact]
    public void Override_rebinds_key_menu_and_tooltip()
    {
        WriteSettings("""{ "ShortcutOverrides": { "dice.1d6": "F9" } }""");
        var w = Game();
        var log = w.Session!.Game!.State.Log.Count;
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.Equal(log, w.Session.Game!.State.Log.Count);   // F5 is free now
        w.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
        Pump();
        Assert.Equal(log + 1, w.Session.Game!.State.Log.Count);
        Assert.Equal(LogKind.Dice, w.Session.Game.State.Log[^1].Kind);
        var d1 = w.Toolbar.DropDownItems("dice.menu").First(i => MenuText.ToWinForms((string)i.Header!) == "&1d6");
        Assert.Equal("F9", d1.InputGesture?.ToString());
        Assert.Equal("1d6 (F9)", ToolTip.GetTip(w.Toolbar.Find("dice.1d6")!));

        // Changed while running: the key, the main menu's gesture and the tooltip follow at once.
        Assert.Equal("Ctrl+D", w.FindMenuItem("&Diagnostics")!.InputGesture?.ToString());
        Save(w, s => s with { ShortcutOverrides = new Dictionary<string, string> { ["dice.1d6"] = "F7", ["view.diagnostics"] = "F8" } });
        Assert.Equal("F8", w.FindMenuItem("&Diagnostics")!.InputGesture?.ToString());
        Assert.Equal("1d6 (F7)", ToolTip.GetTip(w.Toolbar.Find("dice.1d6")!));
        w.KeyPress(Key.F9, RawInputModifiers.None, PhysicalKey.F9, null);
        Pump();
        Assert.Equal(log + 1, w.Session.Game!.State.Log.Count);
        w.KeyPress(Key.F7, RawInputModifiers.None, PhysicalKey.F7, null);
        Pump();
        Assert.Equal(log + 2, w.Session.Game!.State.Log.Count);

        // Unbound: no gesture and the plain caption.
        Save(w, s => s with { ShortcutOverrides = new Dictionary<string, string> { ["dice.1d6"] = "" } });
        Assert.Equal("1d6", ToolTip.GetTip(w.Toolbar.Find("dice.1d6")!));
        Assert.Equal("Ctrl+D", w.FindMenuItem("&Diagnostics")!.InputGesture?.ToString());
    }

    [AvaloniaFact]
    public void Override_shows_cmd_on_mac()
    {
        ShortcutGestures.IsMac = true;
        WriteSettings("""{ "ShortcutOverrides": { "dice.1d6": "Ctrl+F9" } }""");
        var w = Game();
        Assert.Equal("1d6 (⌘F9)", ToolTip.GetTip(w.Toolbar.Find("dice.1d6")!));
    }

    // ---------- maps ----------

    [AvaloniaFact]
    public void Apply_reaches_every_open_map_view()
    {
        var w = Game();
        Assert.Equal(2, w.MapTabs.Views.Count);
        Save(w, s => s with
        {
            WheelZoomStep = 1.5, HoverDelayMs = 900, DragThresholdPx = 11, SnapOnDrop = false, LabelFontSize = 12, HighlightColor = "#112233",
            TabMinWidth = 90, TabMaxWidth = 140,
        });
        void Check(MapView v)
        {
            Assert.Equal(1.5, v.ZoomStep);
            Assert.Equal(900, v.HoverDelayMs);
            Assert.Equal(11, v.DragThreshold);
            Assert.False(v.Snap);
            Assert.Equal(12 / 8f, v.LabelScale);
            Assert.Equal(new SKColor(0x11, 0x22, 0x33), v.Highlight);
        }
        Assert.All(w.MapTabs.Views, Check);
        Assert.All(w.MapTabs.Items.OfType<TabItem>(), t => Assert.Equal((90d, 140d), (t.MinWidth, t.MaxWidth)));

        // Views opened later (another module) take the settings too.
        Assert.True(w.OpenModule(TwoMaps()));
        w.StartSetup(null);
        Pump();
        Assert.Equal(2, w.MapTabs.Views.Count);
        Assert.All(w.MapTabs.Views, Check);
        Assert.All(w.MapTabs.Items.OfType<TabItem>(), t => Assert.Equal((90d, 140d), (t.MinWidth, t.MaxWidth)));
    }

    [AvaloniaFact]
    public void Grid_default_sets_show_grid_once()
    {
        var w = Game();
        var grid = w.FindMenuItem("Show &Grid")!;
        Assert.False(grid.IsChecked);
        Save(w, s => s with { GridDefault = GridMode.Always });
        Assert.True(grid.IsChecked);
        Assert.All(w.MapTabs.Views, v => Assert.True(v.ShowGrid));

        // The user turns it off; another setting change leaves it off (only a GridDefault change sets it).
        w.ClickMenu("Show &Grid");
        Assert.False(grid.IsChecked);
        Save(w, s => s with { WheelZoomStep = 1.3 });
        Assert.False(grid.IsChecked);
        Assert.All(w.MapTabs.Views, v => Assert.True(v.ModuleGrids));

        Save(w, s => s with { GridDefault = GridMode.Never });
        Assert.False(grid.IsChecked);
        Assert.All(w.MapTabs.Views, v => Assert.False(v.ModuleGrids));
    }

    [AvaloniaFact]
    public void Out_of_range_values_are_clamped_on_apply()
    {
        // A hand-edited file: the store clamps on load...
        WriteSettings("""{ "WheelZoomStep": 9, "DragThresholdPx": 500, "LabelFontSize": 2, "HoverDelayMs": -5, "ShortcutOverrides": { "no.such.command": "F3" } }""");
        var w = Game();
        Assert.All(w.MapTabs.Views, v =>
        {
            Assert.Equal(2, v.ZoomStep);
            Assert.Equal(50, v.DragThreshold);
            Assert.Equal(6 / 8f, v.LabelScale);
            Assert.Equal(1, v.HoverDelayMs);
        });

        // ...and values that never went through the file are clamped by the apply itself.
        Save(w, s => s with { WheelZoomStep = 0.5, DragThresholdPx = -4, LabelFontSize = 99, HoverDelayMs = 0, TabMinWidth = 5, TabMaxWidth = 1 });
        Assert.All(w.MapTabs.Views, v =>
        {
            Assert.Equal(1.05, v.ZoomStep);
            Assert.Equal(0, v.DragThreshold);
            Assert.Equal(20 / 8f, v.LabelScale);
            Assert.Equal(1, v.HoverDelayMs);
        });
        Assert.All(w.MapTabs.Items.OfType<TabItem>(), t => Assert.Equal((20d, 20d), (t.MinWidth, t.MaxWidth)));
        Assert.Equal("F3", w.SettingsStore.Current.ShortcutOverrides["no.such.command"]);   // unknown ids are kept, and harmless
    }

    [AvaloniaFact]
    public void Map_menu_follows_game_master_and_confirm_delete()
    {
        var w = Game();
        var services = w.MapTabs.MenuServices!;
        Assert.False(services.GameMaster!());
        Assert.True(services.ConfirmDelete!());
        Save(w, s => s with { GameMasterMode = true, ConfirmDelete = false });
        Assert.True(w.GameMaster);
        Assert.True(services.GameMaster!());
        Assert.False(services.ConfirmDelete!());
    }

    // ---------- main window parts ----------

    [AvaloniaFact]
    public void Toolbar_visibility_hides_buttons()
    {
        var w = Game();
        Assert.True(w.Toolbar.Find("tool.notes")!.IsVisible);
        Save(w, s => s with { ToolbarVisible = new Dictionary<string, bool> { ["tool.notes"] = false, ["dice.2d6"] = false } });
        Assert.False(w.Toolbar.Find("tool.notes")!.IsVisible);
        Assert.False(w.Toolbar.Find("dice.2d6")!.IsVisible);
        Assert.True(w.Toolbar.Find("dice.1d6")!.IsVisible);
        Assert.All(w.Toolbar.Separators, s => Assert.True(s.IsVisible));

        // The whole first group hidden: the separator after it goes too.
        var first = new[] { "tool.openModule", "tool.newGame", "tool.loadSetup", "tool.openGame", "tool.saveGame" };
        Save(w, s => s with { ToolbarVisible = first.ToDictionary(id => id, _ => false) });
        Assert.All(first, id => Assert.False(w.Toolbar.Find(id)!.IsVisible));
        Assert.True(w.Toolbar.Find("tool.notes")!.IsVisible);
        Assert.False(w.Toolbar.Separators[0].IsVisible);
        Assert.True(w.Toolbar.Separators[1].IsVisible);

        Save(w, s => s with { ToolbarVisible = new Dictionary<string, bool>() });
        Assert.All(w.Toolbar.Buttons, b => Assert.True(b.Button.IsVisible));
        Assert.All(w.Toolbar.Separators, s => Assert.True(s.IsVisible));
    }

    [AvaloniaFact]
    public void Palette_only_in_game_master_mode()
    {
        var w = Game();
        Assert.False(w.GameMaster);
        Assert.False(w.Palette.IsVisible);
        Save(w, s => s with { GameMasterMode = true });
        Assert.True(w.Palette.IsVisible);
        Save(w, s => s with { GameMasterMode = false });
        Assert.False(w.Palette.IsVisible);
    }

    [AvaloniaFact]
    public void Alert_bar_setting_hides_bar()
    {
        var w = Game();
        Assert.True(WaitFor(() => w.Alerts.IsVisible));
        Save(w, s => s with { AlertBarVisible = false });
        Assert.False(w.Alerts.IsVisible);
        Save(w, s => s with { AlertBarVisible = true });
        Assert.True(w.Alerts.IsVisible);
    }

    [AvaloniaFact]
    public void Alert_bar_off_still_shows_a_notice()
    {
        WriteSettings("""{ "AlertBarVisible": false }""");
        var w = Game();
        var runs = w.AlertRecomputeCount;
        WaitFor(() => w.AlertRecomputeCount > runs, 1000);   // the bar is re-evaluated after the game starts
        Assert.False(w.Alerts.IsVisible);
        w.Alerts.SetNotice("Your turn");
        Save(w, s => s with { WheelZoomStep = 1.3 });   // any apply re-reads the bar
        Assert.True(w.Alerts.IsVisible);
    }

    [AvaloniaFact]
    public void Log_panel_gets_settings()
    {
        var w = Game();
        var g = w.Session!.Game!;
        var p = g.State.Pieces.Values.First();
        w.Actions!.Move(new[] { new MovePieces(new[] { p.Id }, p.MapId, new PointD(p.Pos.X + 40, p.Pos.Y + 40), null) });
        Pump();
        Assert.True(WaitFor(() => w.Log!.Rows.Any(r => r.Kind == LogKind.Move)));
        Save(w, s => s with { LogShowMoves = false });
        Assert.DoesNotContain(w.Log!.Rows, r => r.Kind == LogKind.Move);

        // A log panel made later (another module) starts with the settings.
        Assert.True(w.OpenModule(TwoMaps()));
        w.StartSetup(0);
        Pump();
        var q = w.Session!.Game!.State.Pieces.Values.First();
        w.Actions!.Move(new[] { new MovePieces(new[] { q.Id }, q.MapId, new PointD(q.Pos.X + 40, q.Pos.Y + 40), null) });
        Assert.True(WaitFor(() => w.Session.Game!.State.Log.Any(l => l.Kind == LogKind.Move)));
        WaitFor(() => false, 400);   // the panel's debounce
        Assert.DoesNotContain(w.Log!.Rows, r => r.Kind == LogKind.Move);
    }

    [AvaloniaFact]
    public void Dice_last_roll_setting()
    {
        var w = Game();
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.NotEqual("", w.StatusText);
        w.SetStatus("");
        Save(w, s => s with { ShowLastRollInStatus = false });
        var log = w.Session!.Game!.State.Log.Count;
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.Equal(log + 1, w.Session.Game!.State.Log.Count);
        Assert.Equal("", w.StatusText);
    }

    [AvaloniaFact]
    public void Autosave_uses_setting()
    {
        WriteSettings("""{ "AutosaveMinutes": 0 }""");
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        w.StartSetup(0, Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Pump();
        var dir = Path.Combine(Autosaver.AutosaveRoot(LayoutRoot), s.Archive.Sha256);
        for (var i = 0; i < 2; i++)
        {
            w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
            Pump();
        }
        Assert.False(w.PhaseControl!.View().BeforeStart);   // the phases did advance
        Assert.False(Directory.Exists(dir) && Directory.GetFiles(dir, "autosave-*.qsave").Length > 0);

        // Switched on while running: the next boundary autosaves.
        Save(w, x => x with { AutosaveMinutes = 5 });
        w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
        Pump();
        Assert.True(Directory.Exists(dir) && Directory.GetFiles(dir, "autosave-*.qsave").Length > 0);
    }

    [AvaloniaFact]
    public void Failing_step_does_not_block_others()
    {
        var w = Game();
        var later = 0;
        w.SettingsApplied += _ => throw new InvalidOperationException("boom");
        w.SettingsApplied += _ => later++;   // never reached: the event is one step, as WinForms
        using var trace = new TraceLines();
        Save(w, s => s with { WheelZoomStep = 1.6, AlertBarVisible = false });
        Assert.Equal(new[] { "apply settings: boom" }, trace.Lines);
        Assert.Equal(0, later);
        Assert.All(w.MapTabs.Views, v => Assert.Equal(1.6, v.ZoomStep));   // the map step comes after the event
        Assert.False(w.Alerts.IsVisible);
        Assert.Null(w.SettingsStore.LastSaveError);
    }

    [AvaloniaFact]
    public void Change_from_another_thread_is_applied_on_the_ui_thread()
    {
        var w = Game();
        Task.Run(() => w.SettingsStore.Save(w.SettingsStore.Current with { WheelZoomStep = 1.9 })).Wait();
        Assert.True(WaitFor(() => w.MapTabs.Views.All(v => v.ZoomStep == 1.9)));
    }

    // ---------- Tools > Settings ----------

    /// <summary>The text the open Tools menu really draws next to Settings... (the template's gesture TextBlock).</summary>
    string SettingsGestureText(MainWindow w)
    {
        var tools = w.FindMenuItem("&Tools")!;
        tools.IsSubMenuOpen = true;
        Pump();
        var item = w.FindMenuItem("&Settings...")!;
        item.ApplyTemplate();
        var texts = item.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        tools.IsSubMenuOpen = false;
        return string.Join("|", texts);
    }

    [AvaloniaFact]
    public void Settings_menu_draws_comma_not_OemComma()
    {
        var w = Game();
        var drawn = SettingsGestureText(w);
        Assert.DoesNotContain("OemComma", drawn);
        Assert.Contains("Ctrl+,", drawn);
    }

    [AvaloniaFact]
    public void Settings_menu_gesture_is_meta_comma_on_mac()
    {
        ShortcutGestures.IsMac = true;
        var w = Game();
        var drawn = SettingsGestureText(w);
        Assert.DoesNotContain("OemComma", drawn);
        Assert.Contains(",", drawn.Split('|')[^1]);   // the glyph (Cmd/⌘) is chosen by Avalonia from the real OS; on macOS it draws ⌘,
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), w.FindMenuItem("&Settings...")!.InputGesture);
    }

    [AvaloniaFact]
    public void Tools_settings_menu_and_ctrl_comma()
    {
        var w = Game();
        Assert.Equal("&Settings...", w.MenuLabels()["&Tools"][^1]);
        Assert.Equal("Ctrl+OemComma", w.FindMenuItem("&Settings...")!.InputGesture?.ToString());
        var shown = new List<SettingsWindow>();
        w.ShowSettingsHook = win => { shown.Add(win); return Task.CompletedTask; };
        w.ClickMenu("&Settings...");
        Pump();
        var first = Assert.Single(shown);
        Assert.Equal("Settings", first.Title);
        w.KeyPress(Key.OemComma, RawInputModifiers.Control, PhysicalKey.Comma, ",");
        Pump();
        Assert.Equal(2, shown.Count);

        // OK in the window saves to the store and applies at once.
        var tabs = (TabControl)((DockPanel)first.Content!).Children.OfType<TabControl>().Single();
        var map = Assert.IsType<MapPage>(((TabItem)tabs.Items[3]!).Content);
        map.Zoom.Value = 1.4m;
        Assert.True(first.CommitAsync().GetAwaiter().GetResult());
        Pump();
        Assert.Equal(1.4, w.SettingsStore.Current.WheelZoomStep);
        Assert.All(w.MapTabs.Views, v => Assert.Equal(1.4, v.ZoomStep));
        Assert.True(File.ReadAllText(Path.Combine(LayoutRoot, "settings.json")).Contains("\"WheelZoomStep\": 1.4", StringComparison.Ordinal));
        Assert.Empty(_dialogs.Warnings);
    }

    [AvaloniaFact]
    public void Save_error_is_shown_after_settings()
    {
        WriteSettings("{ not json");
        var w = Game();
        w.ShowSettingsHook = _ => Task.CompletedTask;
        w.ClickMenu("&Settings...");
        Pump();
        var (message, _) = Assert.Single(_dialogs.Warnings);
        Assert.StartsWith("The settings could not be saved: settings.json is not valid JSON", message);
    }
}
