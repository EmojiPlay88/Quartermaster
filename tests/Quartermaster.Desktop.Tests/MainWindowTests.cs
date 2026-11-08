using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Records every dialog the window asks for and answers from queues; no real window, picker or message box is ever shown.</summary>
sealed class FakeDialogs : IDialogs
{
    public readonly Queue<string?> OpenFiles = new();
    public readonly Queue<string?> SaveFiles = new();
    public readonly List<(string Message, string Caption)> Questions = new();
    public readonly List<(string Message, string Caption)> Warnings = new();
    public readonly List<string> PickerTitles = new();
    public bool Answer;
    public SetupChoice? Setup;
    public int SetupChooserShown;
    public bool LastPreselectNew;

    public Task<string?> OpenFileAsync(Window owner, string title, IReadOnlyList<FileFilter> filters)
    {
        PickerTitles.Add(title);
        return Task.FromResult(OpenFiles.Count > 0 ? OpenFiles.Dequeue() : null);
    }

    public readonly List<string?> SuggestedNames = new();
    public TaskCompletionSource<string?>? SaveGate;   // when set, the next save picker waits for it (a slow dialog)

    public Task<string?> SaveFileAsync(Window owner, string title, FileFilter filter, string defaultExtension, string? suggestedFileName = null)
    {
        SuggestedNames.Add(suggestedFileName);
        if (SaveGate is { } g) { SaveGate = null; PickerTitles.Add(title); return g.Task; }
        PickerTitles.Add(title);
        return Task.FromResult(SaveFiles.Count > 0 ? SaveFiles.Dequeue() : null);
    }

    public Task<bool> AskYesNoAsync(Window owner, string message, string caption)
    {
        Questions.Add((message, caption));
        return Task.FromResult(Answer);
    }

    public readonly Queue<bool?> YesNoCancel = new();
    public readonly Queue<string?> Texts = new();
    public readonly List<string> TextPrompts = new();

    public Task<bool?> AskYesNoCancelAsync(Window owner, string message, string caption)
    {
        Questions.Add((message, caption));
        return Task.FromResult(YesNoCancel.Count > 0 ? YesNoCancel.Dequeue() : null);
    }

    public Task<string?> AskTextAsync(Window owner, string title, string prompt, string initial)
    {
        TextPrompts.Add($"{title}|{prompt}|{initial}");
        return Task.FromResult(Texts.Count > 0 ? Texts.Dequeue() : null);
    }

    /// <summary>Property sheets shown: title, rows, and each row's managed-by-ledger value.</summary>
    public readonly List<(string Title, IReadOnlyList<(string Name, string Value)> Rows, IReadOnlyList<string?> Managed)> Sheets = new();
    /// <summary>What the next property sheet answers (null = Cancel).</summary>
    public IReadOnlyList<string>? SheetValues;

    public Task<IReadOnlyList<string>?> EditPropertySheetAsync(Window owner, string title, IReadOnlyList<(string Name, string Value)> rows,
        Func<string, string?>? managedValue)
    {
        Sheets.Add((title, rows, rows.Select(r => managedValue?.Invoke(r.Name)).ToList()));
        return Task.FromResult(SheetValues);
    }

    public Task ShowWarningAsync(Window owner, string message, string caption)
    {
        Warnings.Add((message, caption));
        return Task.CompletedTask;
    }

    public Task<SetupChoice?> ChooseSetupAsync(Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty)
    {
        SetupChooserShown++;
        LastPreselectNew = preselectNew;
        return Task.FromResult(Setup);
    }
}

/// <summary>Task 7: the main window shell, headless, with fake dialogs (menus, map tabs, palette, open/save, unsaved prompt, command line).</summary>
[Collection("MacFlag")]
public sealed class MainWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-main-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public MainWindowTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    MainWindow NewWindow(FakeDialogs? dialogs = null)
    {
        var w = new MainWindow(dialogs ?? _dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>The minimal fixture module plus one predefined setup "Start" (piece p1 on Main Map), as the Render.Tests smoke fixture.</summary>
    static FixtureModule SetupFixture()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        var add = "+/" + SequenceEncoder.Encode(new[] { "p1", "piece;;;unit.svg;A", "Main Map;100;50;1;0" }, '/');
        return new FixtureModule().WithBuildFile(xml).AddSave("s.vsav", new[] { "begin_save", "", add, "end_save" });
    }

    static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    /// <summary>Moves the first piece of the first map by the action port (one undoable move gesture).</summary>
    static void MoveAPiece(MainWindow w)
    {
        var g = w.Session!.Game!;
        var p = g.State.Pieces.Values.First(x => x.MapId == w.Session.Model.Maps[0].Id);
        w.Actions!.Move(new[] { new MovePieces(new[] { p.Id }, p.MapId, new PointD(p.Pos.X + 40, p.Pos.Y + 40), null) });
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Pump();
            Thread.Sleep(10);
        }
        Pump();
    }

    [AvaloniaFact]
    public void Menu_labels_match_winforms()
    {
        var w = NewWindow();
        var menus = w.MenuLabels();
        Assert.Equal(new[] { "&File", "&Edit", "&View" }, menus.Keys);
        Assert.Equal(new[] { "&Open Module...", "&New Game", "Load &Setup...", "Open &Game...", "&Save Game", "Save Game &As...", "New &Rules Pack...", "Open Rules &Pack...", "Save Rules Pac&k", "Save Rules Pack A&s...", "E&xit" }, menus["&File"]);
        Assert.Equal(new[] { "&Undo", "&Redo" }, menus["&Edit"]);
        Assert.Equal(new[] { "Show &Grid", "Zoom to Fit", "Zoom 100%", "Zoom" }, menus["&View"]);
        Assert.Equal(new[] { "25%", "50%", "100%", "200%", "Fit" },
            w.FindMenuItem("Zoom")!.Items.OfType<MenuItem>().Select(i => MenuText.ToWinForms((string)i.Header!)));

        // Shortcuts are shown (display only) and dispatched by the CommandRouter, never bound twice as a HotKey.
        var save = w.FindMenuItem("&Save Game")!;
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Control), save.InputGesture);
        Assert.Null(save.HotKey);
        Assert.Equal(new KeyGesture(Key.Z, KeyModifiers.Control), w.FindMenuItem("&Undo")!.InputGesture);
        // Nothing to save or undo before a module is open.
        Assert.False(save.IsEnabled);
        Assert.False(w.FindMenuItem("&New Game")!.IsEnabled);
        Assert.True(w.FindMenuItem("&Open Module...")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Mac_menu_shows_cmd_shortcuts()
    {
        ShortcutGestures.IsMac = true;
        var w = NewWindow();
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Meta), w.FindMenuItem("&Save Game")!.InputGesture);
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Meta | KeyModifiers.Shift), w.FindMenuItem("Save Game &As...")!.InputGesture);
    }

    [AvaloniaFact]
    public void Open_module_and_start_setup()
    {
        using var fx = SetupFixture();
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        Assert.Null(s.Game);
        Assert.Equal("Start", Assert.Single(s.Model.Setups).Name);
        w.StartSetup(0);

        Assert.Equal(new[] { "p1" }, s.Game!.State.Pieces.Keys);
        Assert.Equal(new[] { "Main Map" }, w.MapTabs.Views.Select(v => v.Map.Name));
        Assert.Equal(s.Game.State.Pieces["p1"].MapId, w.MapTabs.Views[0].Map.Id);
        Assert.False(w.HasUnsavedChanges);
        Assert.Equal("Quartermaster: Fixture 1.0", w.Title);
        Assert.Equal("Fixture 1.0", w.ModuleText);
    }

    [AvaloniaFact]
    public void Open_demo_module_and_start_game()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo("Riverland-Demo.vmod")));
        var s = w.Session!;
        Assert.Empty(s.Model.Setups);   // the demo starts from its setup stacks ("New game from module")
        Assert.False(w.FindMenuItem("&Save Game")!.IsEnabled);
        w.StartSetup(null);

        Assert.NotEmpty(s.Game!.State.Pieces);
        var expected = s.Model.Maps.Where(m => s.CanSeeMap(m.Id)).Select(m => m.Name).ToList();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, w.MapTabs.Views.Select(v => v.Map.Name));
        Assert.All(w.MapTabs.Views, v => Assert.Same(s, v.Session));
        // The views act through the window's action port and error guard (viewer copies and replays are blocked there).
        Assert.All(w.MapTabs.Views, v => Assert.Same(w.Actions, v.Actions!()));
        Assert.All(w.MapTabs.Views, v => Assert.NotNull(v.Guarded));
        Assert.False(w.HasUnsavedChanges);
        Assert.StartsWith("Quartermaster: " + s.Model.Name, w.Title);
        Assert.True(w.FindMenuItem("&Save Game")!.IsEnabled);
        Assert.True(w.Palette.IsVisible);
        Assert.Contains(s.Game.State.Pieces.Values, p => p.MapId == w.MapTabs.Views[0].Map.Id);

        // Tiles get prepared for each tab; then the map is drawn (not the plain workspace grey).
        PumpUntil(() => w.MapTabs.WhenTiled().IsCompleted);
        Assert.All(w.MapTabs.Views, v => Assert.Null(v.Preparing));
        Assert.False(w.StatusTilingVisible);
    }

    [AvaloniaFact]
    public void Setup_index_out_of_range_starts_a_new_game_and_warns()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(3);
        Assert.NotEmpty(w.Session!.Game!.State.Pieces);
        Assert.Contains(w.Session.Log.Items, d => d.Message == "--setup 3 is out of range (module has 0); starting a new game.");
    }

    [AvaloniaFact]
    public void Save_and_reopen_roundtrip()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        Assert.True(w.HasUnsavedChanges);
        Assert.EndsWith("*", w.Title);

        var path = Path.Combine(_tmp, "round.qsave");
        Directory.CreateDirectory(_tmp);
        w.SaveGame(path);
        Assert.False(w.HasUnsavedChanges);
        Assert.Contains("— round", w.Title);

        var w2 = NewWindow(new FakeDialogs());
        w2.OpenModule(Demo("Riverland-Demo.vmod"));
        w2.OpenGame(path);
        Assert.Equal(Json(w.Session!.Game!.State), Json(w2.Session!.Game!.State));
        Assert.False(w2.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void Save_menu_asks_for_a_path_once_then_saves_in_place()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, "menu.qsave");
        _dialogs.SaveFiles.Enqueue(path);

        w.Commands.Execute("file.saveGame");
        Pump();
        Assert.True(File.Exists(path));
        Assert.Equal(new[] { "Save Game" }, _dialogs.PickerTitles);

        MoveAPiece(w);
        w.Commands.Execute("file.saveGame");   // known path: no picker
        Pump();
        Assert.Single(_dialogs.PickerTitles);
        Assert.False(w.HasUnsavedChanges);

        var reread = NativeSave.Read(path);
        Assert.Equal(Json(w.Session!.Game!.State), Json(reread.State));
    }

    [AvaloniaFact]
    public void Save_and_open_path_with_spaces_and_unicode()
    {
        var folder = Path.Combine(_tmp, "my games ä ñ", "sub folder");
        Directory.CreateDirectory(folder);
        var vmod = Path.Combine(folder, "Riverland demo ä ñ.vmod");
        File.Copy(Demo("Riverland-Demo.vmod"), vmod);

        var w = NewWindow();
        Assert.True(w.OpenModule(vmod));
        w.StartSetup(null);
        MoveAPiece(w);
        var path = Path.Combine(folder, "Riverland game ä ñ 1.qsave");
        w.SaveGame(path);
        Assert.True(File.Exists(path));
        Assert.Contains("Riverland game ä ñ 1", w.Title);

        // Through the menu path as well: the picker hands back the same unicode path.
        var w2 = NewWindow();
        _dialogs.OpenFiles.Enqueue(vmod);
        w2.Commands.Execute("file.openModule");
        Pump();
        Assert.NotNull(w2.Session);   // the setup chooser was cancelled (fake answers null): no game yet
        _dialogs.OpenFiles.Enqueue(path);
        w2.Commands.Execute("file.openGame");
        Pump();
        Assert.Equal(Json(w.Session!.Game!.State), Json(w2.Session!.Game!.State));
        Assert.Contains("Riverland game ä ñ 1", w2.Title);
        Assert.Empty(_dialogs.Warnings);
    }

    [AvaloniaFact]
    public void Winforms_save_opens()
    {
        using var fx = SetupFixture();
        var vmod = fx.Build();
        var save = Path.Combine(_tmp, "winforms.qsave");
        Directory.CreateDirectory(_tmp);
        string expected;
        using (var s = ModuleSession.Open(vmod, Path.Combine(_tmp, "cache-wf"), null))
        {
            s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
            s.Game!.Execute(new MovePieces(new[] { "p1" }, s.Game.State.Pieces["p1"].MapId, new PointD(150, 75), null));
            Assert.NotEmpty(s.Game!.State.Pieces);
            s.SaveNative(save);   // exactly what WinForms MainForm.SaveGame writes
            expected = Json(s.Game.State);
        }

        var w = NewWindow();
        Assert.True(w.OpenModule(vmod));
        w.OpenGame(save);
        Assert.Equal(expected, Json(w.Session!.Game!.State));
        Assert.Contains("— winforms", w.Title);
    }

    [AvaloniaFact]
    public void Desktop_save_opens_in_a_winforms_session()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, "desktop.qsave");
        w.SaveGame(path);
        using var s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache-wf"), null);
        s.LoadNative(path);   // exactly what WinForms MainForm.LoadSavedGame does
        Assert.Equal(Json(w.Session!.Game!.State), Json(s.Game!.State));
    }

    [AvaloniaFact]
    public void Close_with_unsaved_changes_asks()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        Assert.True(w.HasUnsavedChanges);

        _dialogs.Answer = false;
        w.Close();
        Pump();
        Assert.Equal(new[] { ("The current game has unsaved changes. Discard them?", "Quartermaster") }, _dialogs.Questions);
        Assert.True(w.IsVisible);
        Assert.NotNull(w.Session);

        _dialogs.Answer = true;
        w.Close();
        Pump();
        Assert.Equal(2, _dialogs.Questions.Count);
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void Close_without_changes_does_not_ask()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        w.Close();
        Pump();
        Assert.Empty(_dialogs.Questions);
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void New_game_with_unsaved_changes_asks_and_no_keeps_the_game()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        var before = Json(w.Session!.Game!.State);
        _dialogs.Answer = false;
        _dialogs.Setup = new SetupChoice(null, Difficulty.FullyAuto);

        w.Commands.Execute("file.newGame");
        Pump();
        Assert.Single(_dialogs.Questions);
        Assert.Equal(0, _dialogs.SetupChooserShown);
        Assert.Equal(before, Json(w.Session.Game!.State));

        _dialogs.Answer = true;
        w.Commands.Execute("file.newGame");
        Pump();
        Assert.Equal(1, _dialogs.SetupChooserShown);
        Assert.True(_dialogs.LastPreselectNew);
        Assert.False(w.HasUnsavedChanges);
        Assert.Equal(Difficulty.FullyAuto, w.Session.Game!.State.Difficulty);
    }

    [AvaloniaFact]
    public void Undo_menu_label_and_shortcut_follow_history()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        var undo = w.FindMenuItem("&Undo")!;
        Assert.False(undo.IsEnabled);
        MoveAPiece(w);
        Pump();
        Assert.True(undo.IsEnabled);
        Assert.StartsWith("&Undo ", MenuText.ToWinForms((string)undo.Header!));
        var moved = Json(w.Session!.Game!.State);

        w.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Pump();
        Assert.NotEqual(moved, Json(w.Session.Game!.State));
        Assert.True(w.FindMenuItem("&Redo", prefix: true)!.IsEnabled);
    }

    [AvaloniaFact]
    public void Palette_drop_adds_piece()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        Pump();
        var s = w.Session!;
        Assert.NotEmpty(w.Palette.ListedSlots);
        var slot = w.Palette.ListedSlots[0];
        var view = w.MapTabs.Views[0];
        var board = view.Map.Boards[0];
        var centre = new PointD(board.Offset.X + board.Width / 2.0, board.Offset.Y + board.Height / 2.0);
        view.CentreOn(centre);
        Pump();
        var sp = view.View.ToScreen(centre);
        var at = view.TranslatePoint(new Point(sp.X, sp.Y), w)!.Value;
        var count = s.Game!.State.Pieces.Count;
        var log = s.Game.Log.Count;

        var data = PaletteDrop.DataFor(slot);
        w.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        w.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        w.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy);
        Pump();

        Assert.Equal(count + 1, s.Game.State.Pieces.Count);
        var add = Assert.IsType<AddPiece>(s.Game.Log[log]);
        Assert.Equal(view.Map.Id, add.MapId);
        Assert.Equal($"Placed {PaletteModel.PieceName(slot)}", w.StatusText);
        // One undoable action through the action port.
        Assert.True(w.FindMenuItem("&Undo", prefix: true)!.IsEnabled);
        Assert.True(w.HasUnsavedChanges);
    }

    static bool Alive(Avalonia.Media.Imaging.Bitmap b)
    {
        try { _ = b.PixelSize; return true; }
        catch (ObjectDisposedException) { return false; }
    }

    [AvaloniaFact]
    public void Palette_cache_eviction_never_disposes_a_shown_thumbnail()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        var palette = w.Palette;
        PumpUntil(() => palette.ThumbImages.Count > 0 && palette.ThumbImages.All(i => i.Source != null));
        var shown = palette.ThumbImages.Select(i => i.Source).OfType<Avalonia.Media.Imaging.Bitmap>().ToList();
        Assert.NotEmpty(shown);

        var slot = palette.ListedSlots[0];
        var unshown = PaletteDock.Render(w.Session!, slot)!;
        var newest = PaletteDock.Render(w.Session!, slot)!;
        palette.CacheCap = 1;
        palette.Remember("test:unshown", unshown);
        palette.Remember("test:newest", newest);   // evicts every shown thumbnail and the unshown one

        Assert.All(shown, b => Assert.True(Alive(b), "a thumbnail still on screen was disposed"));
        Assert.False(Alive(unshown), "an evicted thumbnail nobody shows should be disposed");
        Assert.True(Alive(newest));
    }

    [AvaloniaFact]
    public void Palette_drop_off_the_boards_says_so()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        Pump();
        var view = w.MapTabs.Views[0];
        var count = w.Session!.Game!.State.Pieces.Count;
        // Far outside every board.
        view.CentreOn(new PointD(-100000, -100000));
        Pump();
        var at = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), w)!.Value;
        var data = PaletteDrop.DataFor(w.Palette.ListedSlots[0]);
        w.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        w.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy);
        Pump();
        Assert.Equal(count, w.Session.Game.State.Pieces.Count);
        Assert.Equal("Drop onto a board", w.StatusText);
    }

    [AvaloniaFact]
    public void Status_bar_shows_map_location_and_zoom()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        Pump();
        var view = w.MapTabs.Views[0];
        var board = view.Map.Boards[0];
        var centre = new PointD(board.Offset.X + board.Width / 2.0, board.Offset.Y + board.Height / 2.0);
        PumpUntil(() => w.MapTabs.WhenTiled().IsCompleted);
        view.CentreOn(centre);
        Pump();
        var sp = view.View.ToScreen(centre);
        w.MouseMove(view.TranslatePoint(new Point(sp.X, sp.Y), w)!.Value);
        Pump();
        var m = view.ScreenToMap(new Point(sp.X, sp.Y));
        var expected = MapStatus.Location(view.Map, m);
        Assert.StartsWith($"{(int)Math.Round(m.X)}, {(int)Math.Round(m.Y)}", expected);
        Assert.Equal(expected, w.LocationText);
        Assert.Equal($"{Math.Round(view.View.Zoom * 100)}%", w.ZoomText);

        w.ClickMenu("Zoom 100%");
        Pump();
        Assert.Equal("100%", w.ZoomText);
        Assert.Equal("-", w.LocationText);   // a menu zoom resets the location, as WinForms
    }

    /// <summary>Opens the demo, waits for its tiles, and moves the mouse to the middle of the first board; returns the view and that screen point.</summary>
    (MainWindow W, MapView View, Point At) DemoWithMouseOverBoard()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        PumpUntil(() => w.MapTabs.WhenTiled().IsCompleted);
        var view = w.MapTabs.Views[0];
        var board = view.Map.Boards[0];
        var centre = new PointD(board.Offset.X + board.Width / 2.0, board.Offset.Y + board.Height / 2.0);
        view.CentreOn(centre);
        Pump();
        var sp = view.View.ToScreen(centre);
        var at = view.TranslatePoint(new Point(sp.X, sp.Y), w)!.Value;
        w.MouseMove(at);
        Pump();
        return (w, view, new Point(sp.X, sp.Y));
    }

    [AvaloniaFact]
    public void Tiles_arriving_after_a_mouse_move_keep_the_location()
    {
        var (w, view, at) = DemoWithMouseOverBoard();
        var expected = MapStatus.Location(view.Map, view.ScreenToMap(at));
        Assert.Equal(expected, w.LocationText);
        // Tile preparation finishing (MapTabs.PrepareTilesAsync → SetTiles) must not wipe the location (WinForms never does).
        var board = view.Map.Boards[0];
        view.SetTiles(BoardTiler.TileKey(board), new TileSet("", Math.Max(board.Width, 1), Math.Max(board.Height, 1), 0, isPlaceholder: true));
        Pump();
        Assert.Equal(expected, w.LocationText);
    }

    [AvaloniaFact]
    public void Wheel_zoom_reports_the_cursor_location()
    {
        var (w, view, at) = DemoWithMouseOverBoard();
        var zoom = view.View.Zoom;
        w.MouseWheel(view.TranslatePoint(at, w)!.Value, new Vector(0, 1));
        Pump();
        Assert.NotEqual(zoom, view.View.Zoom);
        Assert.Equal(MapStatus.Location(view.Map, view.ScreenToMap(at)), w.LocationText);
        Assert.NotEqual("-", w.LocationText);
        Assert.Equal($"{Math.Round(view.View.Zoom * 100)}%", w.ZoomText);
    }

    [AvaloniaFact]
    public void Show_grid_menu_toggles_every_map()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        w.ClickMenu("Show &Grid");
        Assert.All(w.MapTabs.Views, v => Assert.True(v.ShowGrid));
        Assert.True(w.FindMenuItem("Show &Grid")!.IsChecked);
        w.ClickMenu("Show &Grid");
        Assert.All(w.MapTabs.Views, v => Assert.False(v.ShowGrid));
    }

    [AvaloniaFact]
    public void Command_line_opens_module_and_setup()
    {
        using var fx = SetupFixture();
        var vmod = fx.Build();
        var warnings = new List<string>();
        var a = StartupArgs.Parse(new[] { vmod, "--setup", "0", "--bogus" }, warnings.Add);
        Assert.Equal(vmod, a.ModulePath);
        Assert.Equal(0, a.SetupIndex);
        Assert.False(a.NewGame);
        Assert.Equal(new[] { "Unknown option '--bogus'; ignored." }, warnings);

        var w = NewWindow();
        w.ApplyStartup(a);
        Pump();
        Assert.Equal(new[] { "p1" }, w.Session!.Game!.State.Pieces.Keys);
        Assert.Equal(0, _dialogs.SetupChooserShown);

        var n = StartupArgs.Parse(new[] { "--new", Demo("Riverland-Demo.vmod") }, warnings.Add);
        Assert.True(n.NewGame);
        var w2 = NewWindow(new FakeDialogs());
        w2.ApplyStartup(n);
        Assert.NotNull(w2.Session?.Game);

        // No flag: the setup chooser is shown, as WinForms.
        var plain = StartupArgs.Parse(new[] { Demo("Riverland-Demo.vmod") }, warnings.Add);
        var d3 = new FakeDialogs { Setup = new SetupChoice(null, Difficulty.Hardcore) };
        var w3 = NewWindow(d3);
        w3.ApplyStartup(plain);
        Pump();
        Assert.Equal(1, d3.SetupChooserShown);
        Assert.False(d3.LastPreselectNew);
        Assert.Equal(Difficulty.Hardcore, w3.Session!.Game!.State.Difficulty);

        Assert.Equal(new[] { "--setup needs a non-negative integer; ignored." },
            Collect(w => StartupArgs.Parse(new[] { "--setup", "x" }, w)));
    }

    static List<string> Collect(Action<Action<string>> run)
    {
        var l = new List<string>();
        run(l.Add);
        return l;
    }

    [AvaloniaFact]
    public void Open_module_failure_shows_the_winforms_message()
    {
        var w = NewWindow();
        var missing = Path.Combine(_tmp, "nope.vmod");
        Assert.False(w.OpenModule(missing));
        Pump();
        Assert.Null(w.Session);
        var (msg, caption) = Assert.Single(_dialogs.Warnings);
        Assert.StartsWith($"Could not open '{missing}':\n", msg);
        Assert.Equal("Quartermaster", caption);
    }

    [AvaloniaFact]
    public void Setup_chooser_lists_setups_and_difficulty()
    {
        using var fx = SetupFixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        var c = new SetupChooser(s.Model, Difficulty.FullyAuto, preselectNew: false);
        Assert.Equal("New game", c.Title);
        Assert.Equal(new[] { "New game from module", "Start" }, c.Lines);
        Assert.Equal(1, c.SelectedIndex);
        Assert.Equal(new SetupChoice(s.Model.Setups[0], Difficulty.FullyAuto), c.Current);
        Assert.Equal(new[] { "Hardcore", "Semi-automatic", "Fully automatic" }, SetupChooser.DifficultyModes.Select(m => m.Name));

        var n = new SetupChooser(s.Model, Difficulty.SemiAuto, preselectNew: true);
        Assert.Equal(0, n.SelectedIndex);
        Assert.Equal(new SetupChoice(null, Difficulty.SemiAuto), n.Current);
    }

    [AvaloniaFact]
    public void Corrupt_or_missing_save_via_menu_warns_and_keeps_the_game()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        Directory.CreateDirectory(_tmp);
        var bad = Path.Combine(_tmp, "bad.qsave");
        File.WriteAllText(bad, "{ not json");
        var before = Json(w.Session!.Game!.State);
        var game = w.Session.Game;

        _dialogs.OpenFiles.Enqueue(bad);
        w.Commands.Execute("file.openGame");
        Pump();
        var (msg, caption) = Assert.Single(_dialogs.Warnings);
        Assert.StartsWith("Malformed save file: ", msg);
        Assert.Equal("Quartermaster", caption);

        _dialogs.OpenFiles.Enqueue(Path.Combine(_tmp, "missing.qsave"));
        w.Commands.Execute("file.openGame");
        Pump();
        Assert.Equal(2, _dialogs.Warnings.Count);
        Assert.Contains("missing.qsave", _dialogs.Warnings[1].Message);

        Assert.Same(game, w.Session.Game);
        Assert.Equal(before, Json(w.Session.Game!.State));
        Assert.Null(w.SavePath);
        Assert.Empty(_dialogs.Questions);   // nothing changed, so nothing was asked
    }

    [AvaloniaTheory]
    [InlineData("file.openGame")]
    [InlineData("file.openModule")]
    [InlineData("Load &Setup...")]
    public void Discard_question_guards_every_game_replacing_command(string command)
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        var game = w.Session!.Game;
        var before = Json(game!.State);
        Directory.CreateDirectory(_tmp);
        var other = Path.Combine(_tmp, "other.qsave");
        w.SaveGame(other);
        MoveAPiece(w);   // dirty again after the save
        Assert.True(w.HasUnsavedChanges);
        _dialogs.OpenFiles.Enqueue(command == "file.openModule" ? Demo("Riverland-Demo.vmod") : other);
        _dialogs.Setup = new SetupChoice(null, Difficulty.FullyAuto);
        _dialogs.Answer = false;

        if (command.StartsWith("file.", StringComparison.Ordinal)) w.Commands.Execute(command); else w.ClickMenu(command);
        Pump();

        Assert.Equal(new[] { ("The current game has unsaved changes. Discard them?", "Quartermaster") }, _dialogs.Questions);
        Assert.Same(game, w.Session.Game);   // No: the same game stays, still dirty
        Assert.NotEqual(before, Json(w.Session.Game!.State));
        Assert.True(w.HasUnsavedChanges);
        Assert.Equal(0, _dialogs.SetupChooserShown);
    }

    [AvaloniaFact]
    public void Undo_after_save_makes_the_game_dirty_again()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        Directory.CreateDirectory(_tmp);
        w.SaveGame(Path.Combine(_tmp, "u.qsave"));
        Assert.False(w.HasUnsavedChanges);
        Assert.True(w.Commands.Execute("edit.undo"));
        Pump();
        Assert.True(w.HasUnsavedChanges);
        Assert.EndsWith("*", w.Title);
    }

    [AvaloniaFact]
    public void Undo_tooltip_says_why_it_is_off()
    {
        var w = NewWindow();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        MoveAPiece(w);
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, "t.qsave");
        w.SaveGame(path);
        var undo = w.FindMenuItem("&Undo", prefix: true)!;
        Assert.Null(ToolTip.GetTip(undo));   // enabled: no reason shown

        // A save rewritten without its undo history: undo stops at the loaded state and says so (WinForms FloorTip).
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("genesis"); json.Remove("actions"); json.Remove("checkpoints");
        json["log"] = new System.Text.Json.Nodes.JsonArray();
        File.WriteAllText(path, json.ToJsonString());
        w.OpenGame(path);
        Pump();
        undo = w.FindMenuItem("&Undo", prefix: true)!;
        Assert.False(undo.IsEnabled);
        Assert.Equal(MainWindow.FloorTip, ToolTip.GetTip(undo));
        Assert.True(ToolTip.GetShowOnDisabled(undo));
    }
}
