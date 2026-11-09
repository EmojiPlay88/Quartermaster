using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2a task 7: Tools → Dice / Turn, F5 / F6, the dice and turn dialogs and the main toolbar. Headless, fake dialogs, a fixed roller.</summary>
[Collection("MacFlag")]
public sealed class DiceTurnTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-dice-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public DiceTurnTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", "Riverland-Demo.vmod"));

    static void Pump() => Dispatcher.UIThread.RunJobs();

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>A window with a new game of <paramref name="module"/> (Riverland by default) and a roller that always shows <paramref name="face"/>.</summary>
    MainWindow Game(string? module = null, int face = 4)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(module ?? Demo()));
        w.Session!.SetRoller((n, _) => Enumerable.Repeat(face, n).ToList());
        w.StartSetup(null);
        Pump();
        return w;
    }

    static IReadOnlyList<string> Labels(MenuItem m) =>
        m.Items.OfType<MenuItem>().Select(i => MenuText.ToWinForms((string)i.Header!)).ToList();

    static LogEntry LastLog(MainWindow w) => w.Session!.Game!.State.Log[^1];

    [AvaloniaFact]
    public void F5_rolls_1d6_and_logs()
    {
        var w = Game(face: 5);
        var before = w.Session!.Game!.State.Log.Count;
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        Assert.Equal(before + 1, w.Session!.Game!.State.Log.Count);
        var log = LastLog(w);
        Assert.Equal(LogKind.Dice, log.Kind);
        var roll = Assert.IsType<RollDice>(w.Session.Game.Log[^1]);
        Assert.Equal(("Roll", "1d6"), (roll.Purpose, roll.Expression));
        Assert.Equal(new[] { 5 }, roll.Results);
        Assert.Equal(log.Text, w.StatusText);   // the roll's log line, as WinForms ShowResult

        w.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
        Pump();
        roll = Assert.IsType<RollDice>(w.Session.Game.Log[^1]);
        Assert.Equal(("Roll", "2d6"), (roll.Purpose, roll.Expression));
        Assert.Equal(new[] { 5, 5 }, roll.Results);

        // The menu shows the shortcuts (display only).
        w.ClickMenu("&Dice");
        Assert.Equal(new KeyGesture(Key.F5), w.FindMenuItem("&1d6")!.InputGesture);
        Assert.Equal(new KeyGesture(Key.F6), w.FindMenuItem("&2d6")!.InputGesture);
        Assert.Null(w.FindMenuItem("&1d6")!.HotKey);
    }

    [AvaloniaFact]
    public void Rolls_use_session_roller()
    {
        var w = Game();
        var calls = new List<(int N, int Sides)>();
        w.Session!.SetRoller((n, sides) => { calls.Add((n, sides)); return Enumerable.Range(1, n).ToList(); });
        w.ClickMenu("&2d6");
        Assert.Equal(new[] { (2, 6) }, calls);
        Assert.Equal(new[] { 1, 2 }, Assert.IsType<RollDice>(w.Session.Game!.Log[^1]).Results);

        // A broken roller records nothing and says so.
        var before = w.Session.Game.Log.Count;
        w.Session.SetRoller((_, _) => new[] { 9 });
        w.ClickMenu("&1d6");
        Assert.Equal(before, w.Session.Game.Log.Count);
        Assert.Equal(DiceTurnActions.InvalidRollText, w.StatusText);
    }

    [AvaloniaFact]
    public void Custom_dice_expression_rolls()
    {
        var w = Game(face: 3);
        _dialogs.Dice = new DiceChoice(new DiceSpec(3, 10, 2), 1);
        w.ClickMenu("&Custom...");
        Pump();
        Assert.Equal(new[] { "Custom Roll|False" }, _dialogs.DicePrompts);
        var roll = Assert.IsType<RollDice>(w.Session!.Game!.Log[^1]);
        Assert.Equal(("Roll", "3d10+2", 2), (roll.Purpose, roll.Expression, roll.Modifier));
        Assert.Equal(new[] { 3, 3, 3 }, roll.Results);

        _dialogs.Dice = new DiceChoice(new DiceSpec(1, 6, 0), 3);
        w.ClickMenu("&Multi-roll...");
        Pump();
        Assert.Equal("Multi-roll|True", _dialogs.DicePrompts[^1]);
        Assert.Equal(new[] { "Multi-roll 1/3", "Multi-roll 2/3", "Multi-roll 3/3" },
            w.Session.Game.Log.OfType<RollDice>().Skip(1).Select(r => r.Purpose));

        var count = w.Session.Game.Log.Count;
        _dialogs.Dice = null;   // Cancel
        w.ClickMenu("&Custom...");
        Pump();
        Assert.Equal(count, w.Session.Game.Log.Count);
    }

    [AvaloniaFact]
    public void Invalid_dice_expression_shows_error()
    {
        var d = new DiceDialog("Custom Roll", multi: false);
        Assert.Equal("Custom Roll", d.Title);
        Assert.Equal("2d6", d.ExpressionBox.Text);
        Assert.Equal("", d.ErrorText);
        Assert.True(d.OkButton.IsEnabled);
        Assert.False(d.TimesBox.IsVisible);

        d.ExpressionBox.Text = "2x6";
        Assert.Equal(DiceExpr.FormatError, d.ErrorText);
        Assert.False(d.OkButton.IsEnabled);

        d.ExpressionBox.Text = "3d10+2";
        Assert.Equal("", d.ErrorText);
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal(new DiceChoice(new DiceSpec(3, 10, 2), 1), d.Result());

        var m = new DiceDialog("Multi-roll", multi: true);
        Assert.True(m.TimesBox.IsVisible);
        Assert.Equal(3, m.TimesBox.Value);
        Assert.Equal((1m, 20m), (m.TimesBox.Minimum, m.TimesBox.Maximum));
        m.TimesBox.Value = 5;
        Assert.Equal(new DiceChoice(new DiceSpec(2, 6, 0), 5), m.Result());
    }

    [AvaloniaFact]
    public void Module_dice_listed()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.SpecialDiceButton name=\"Dice\">" +
            "<VASSAL.build.module.SpecialDie name=\"R&amp;D\"><VASSAL.build.module.SpecialDieFace text=\"Sun\"/><VASSAL.build.module.SpecialDieFace text=\"Rain\"/></VASSAL.build.module.SpecialDie>" +
            "<VASSAL.build.module.SpecialDie name=\"Blank\"><VASSAL.build.module.SpecialDieFace text=\"x\"/></VASSAL.build.module.SpecialDie>" +
            "</VASSAL.build.module.SpecialDiceButton></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml);
        _fixtures.Add(fx);
        var w = Game(fx.Build(), face: 2);

        Assert.Equal(new[] { "&Inventory", "&Notes", "&Dice", "&Turn" }, w.MenuLabels()["&Tools"]);
        var dice = w.FindMenuItem("&Dice")!;
        Assert.Equal(new[] { "&1d6", "&2d6", "R&&D", "Blank", "&Custom...", "&Multi-roll..." }, Labels(dice));
        Assert.Equal(2, dice.Items.OfType<Separator>().Count());   // around the module dice, as WinForms
        var blank = w.FindMenuItem("Blank")!;
        Assert.False(blank.IsEnabled);
        Assert.Equal(DiceTurnActions.NeedsFacesText, ToolTip.GetTip(blank));

        w.ClickMenu("R&&D");
        var roll = Assert.IsType<RollDice>(w.Session!.Game!.Log[^1]);
        Assert.Equal("R&D", roll.Purpose);
        Assert.Equal(new[] { "Rain" }, roll.FaceTexts);
        Assert.Equal(LastLog(w).Text, w.StatusText);

        // The toolbar's Dice drop-down lists the same items.
        Assert.Equal(new[] { "&1d6", "&2d6", "R&&D", "Blank", "&Custom...", "&Multi-roll..." },
            w.Toolbar.DropDownItems("dice.menu").Select(i => MenuText.ToWinForms((string)i.Header!)));

        // Without a game the menu says so.
        var w2 = NewWindow();
        Assert.False(w2.FindMenuItem("&Dice")!.IsEnabled);
        Assert.Equal(new[] { "(no game)" }, Labels(w2.FindMenuItem("&Dice")!));
    }

    /// <summary>Opens Tools, then Tools → Dice, as the user does.</summary>
    static MenuItem OpenDiceMenu(MainWindow w)
    {
        var tools = w.FindMenuItem("&Tools")!;
        tools.IsSubMenuOpen = true;
        Pump();
        var dice = w.FindMenuItem("&Dice")!;
        dice.IsSubMenuOpen = true;
        Pump();
        return dice;
    }

    /// <summary>Clicks a menu item with the pointer in its own popup (headless, never shown).</summary>
    static void PointerClick(MenuItem item)
    {
        var top = TopLevel.GetTopLevel(item) ?? throw new InvalidOperationException("item not shown");
        var at = item.TranslatePoint(new Avalonia.Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top) ?? throw new InvalidOperationException("not in the tree");
        top.MouseMove(at);
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        Pump();
    }

    [AvaloniaFact]
    public void Dice_menu_click_closes_menu_and_keeps_items()
    {
        // WinForms rebuilds the Dice menu on DropDownOpening; rebuilding it on every game change cleared the items inside the clicked item's
        // own Click handler.
        var w = Game(face: 3);
        var tools = w.FindMenuItem("&Tools")!;
        var dice = OpenDiceMenu(w);
        Assert.True(dice.IsSubMenuOpen);
        var items = dice.Items.Cast<object>().ToList();
        var d1 = dice.Items.OfType<MenuItem>().First(i => MenuText.ToWinForms((string)i.Header!) == "&1d6");
        var before = w.Session!.Game!.Log.Count;

        PointerClick(d1);
        Assert.Equal(before + 1, w.Session.Game.Log.Count);
        Assert.Equal("1d6", Assert.IsType<RollDice>(w.Session.Game.Log[^1]).Expression);
        Assert.False(dice.IsSubMenuOpen);    // the menu closed normally
        Assert.False(tools.IsSubMenuOpen);
        Assert.Equal(items, dice.Items.Cast<object>());   // the items were not swapped under the click
        Assert.Equal(new[] { "&1d6", "&2d6", "&Custom...", "&Multi-roll..." }, Labels(dice));

        // Opening it again rolls again.
        dice = OpenDiceMenu(w);
        PointerClick(dice.Items.OfType<MenuItem>().First(i => MenuText.ToWinForms((string)i.Header!) == "&1d6"));
        Assert.Equal(before + 2, w.Session.Game.Log.Count);
        Assert.False(dice.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Dice_menu_follows_module_change()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.SpecialDiceButton name=\"Dice\">" +
            "<VASSAL.build.module.SpecialDie name=\"Weather\"><VASSAL.build.module.SpecialDieFace text=\"Sun\"/><VASSAL.build.module.SpecialDieFace text=\"Rain\"/></VASSAL.build.module.SpecialDie>" +
            "</VASSAL.build.module.SpecialDiceButton></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml);
        _fixtures.Add(fx);
        var w = Game();
        Assert.Equal(new[] { "&1d6", "&2d6", "&Custom...", "&Multi-roll..." }, Labels(OpenDiceMenu(w)));
        w.FindMenuItem("&Tools")!.IsSubMenuOpen = false;
        Pump();

        Assert.True(w.OpenModule(fx.Build()));
        w.StartSetup(null);
        Pump();
        var dice = OpenDiceMenu(w);
        Assert.Equal(new[] { "&1d6", "&2d6", "Weather", "&Custom...", "&Multi-roll..." }, Labels(dice));
        Assert.True(dice.IsSubMenuOpen);
    }

    [AvaloniaFact]
    public void Turn_next_previous_set()
    {
        var w = Game();
        Assert.Equal(new[] { "&Next turn", "&Previous turn", "&Set..." }, Labels(w.FindMenuItem("&Turn")!));
        Assert.Equal(new KeyGesture(Key.Right, KeyModifiers.Control), w.FindMenuItem("&Next turn")!.InputGesture);
        Assert.Equal("Turn 1", w.TurnText);
        Assert.True(w.TurnLabelVisible);
        Assert.False(w.FindMenuItem("&Previous turn")!.IsEnabled);

        w.ClickMenu("&Next turn");
        Pump();
        Assert.Equal(2, w.Session!.Game!.State.Turn.Turn);
        Assert.Equal("Turn 2", w.TurnText);
        Assert.True(w.FindMenuItem("&Previous turn")!.IsEnabled);
        w.ClickMenu("&Previous turn");
        Pump();
        Assert.Equal(1, w.Session.Game.State.Turn.Turn);
        Assert.False(w.FindMenuItem("&Previous turn")!.IsEnabled);

        _dialogs.Turn = new TurnChoice(7, "  Combat ");
        w.ClickMenu("&Set...");
        Pump();
        Assert.Equal(new[] { new TurnState(1, null) }, _dialogs.TurnPrompts);
        Assert.Equal(new TurnState(7, "Combat"), w.Session.Game.State.Turn);
        Assert.Equal("Turn 7 · Combat", w.TurnText);
        Assert.IsType<SetTurn>(w.Session.Game.Log[^1]);

        // Ctrl+Right (turn.next) without a phased rules pack steps the counter, as WinForms; the window's arrow-key navigation must not
        // swallow it, with nothing focused or with the map focused.
        w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
        Pump();
        Assert.Equal(new TurnState(8, null), w.Session.Game.State.Turn);
        w.MapTabs.ActiveView!.Focus();
        w.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
        Pump();
        Assert.Equal(9, w.Session.Game.State.Turn.Turn);

        // Cancel changes nothing.
        _dialogs.Turn = null;
        var count = w.Session.Game.Log.Count;
        w.ClickMenu("&Set...");
        Pump();
        Assert.Equal(count, w.Session.Game.Log.Count);

        // No game: no turn label, the menu is off.
        var w2 = NewWindow();
        Assert.False(w2.TurnLabelVisible);
        Assert.False(w2.FindMenuItem("&Turn")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Toolbar_has_default_buttons_in_order()
    {
        var w = NewWindow();
        var bar = w.Toolbar;
        // WinForms MainForm.BuildToolbar order, without the buttons whose features are not in the Avalonia app yet
        // (Palette).
        var expected = new[]
        {
            ("tool.openModule", "Open Module"), ("tool.newGame", "New Game"), ("tool.loadSetup", "Load Setup"), ("tool.openGame", "Open Game"),
            ("tool.saveGame", "Save Game"), ("tool.diagnostics", "Diagnostics"), ("tool.notes", "Notes"), ("tool.overview", "Overview"), ("tool.inventory", "Inventory"), ("tool.los", "LOS"), ("edit.undo", "Undo"), ("edit.redo", "Redo"), ("phase.stepBack", "◀ Step Back"),
            ("dice.1d6", "1d6"), ("dice.2d6", "2d6"), ("dice.menu", "Dice ▾"), ("turn.next", "Next turn"),
        };
        Assert.Equal(expected, bar.Buttons.Select(b => (b.Id, b.Text)));
        Assert.All(bar.Buttons, b => Assert.Contains(ToolbarIds.All, t => t.Id == b.Id));
        Assert.All(bar.Buttons, b => Assert.False(b.Button.Focusable));

        // Gated by CommandAvailability: without a module only Open Module is on.
        Assert.Equal(new[] { "tool.openModule" }, bar.Buttons.Where(b => b.Button.IsEnabled).Select(b => b.Id));
        Assert.True(w.OpenModule(Demo()));
        Assert.Equal(new[] { "tool.openModule", "tool.newGame", "tool.loadSetup", "tool.openGame", "tool.diagnostics" },
            bar.Buttons.Where(b => b.Button.IsEnabled).Select(b => b.Id));
        w.Session!.SetRoller((n, _) => Enumerable.Repeat(6, n).ToList());
        w.StartSetup(null);
        Pump();
        foreach (var id in new[] { "tool.saveGame", "tool.notes", "tool.inventory", "tool.los", "dice.1d6", "dice.2d6", "dice.menu", "turn.next" })
            Assert.True(bar.Find(id)!.IsEnabled, id);
        Assert.False(bar.Find("edit.undo")!.IsEnabled);

        // The buttons act: 2d6 rolls, Next turn steps, Undo then takes it back.
        bar.Find("dice.2d6")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("2d6", Assert.IsType<RollDice>(w.Session.Game!.Log[^1]).Expression);
        bar.Find("turn.next")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(2, w.Session.Game.State.Turn.Turn);
        Assert.True(bar.Find("edit.undo")!.IsEnabled);
        Assert.Equal("Undo turn change (Ctrl+Z)", ToolTip.GetTip(bar.Find("edit.undo")!));
        bar.Find("edit.undo")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal(1, w.Session.Game.State.Turn.Turn);
        Assert.True(bar.Find("edit.redo")!.IsEnabled);
    }
}
