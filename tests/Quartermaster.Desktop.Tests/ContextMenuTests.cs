using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>The map's right-click menu (counter menu from the shared PieceMenuModel, empty-map menu as WinForms MapWindow), the macOS
/// Ctrl+click convention, the property sheet and the input prompt. Headless, on the Riverland demo. Reads ShortcutGestures.IsMac: MacFlag.</summary>
[Collection("MacFlag")]
public sealed class ContextMenuTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 800, H = 600;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-menu-" + Guid.NewGuid().ToString("N"));
    readonly FakeDialogs _dialogs = new();
    ModuleSession? _s;

    // PROP;key;constraints(numeric,min,max,wrap);commands: "Ask" prompts for a number, "Bad" is disabled (refers to a property).
    const string PromptTrait = "PROP;Steps;true,0,5,false;Ask:K4:R\\,Value?,Bad:K5:P\\,$x$";
    const string SheetTrait = "propertysheet;0Fuel;Sheet;";
    const string ObsTrait = "obs;M;m.png;Mask it;I;?";

    public void Dispose()
    {
        ShortcutGestures.IsMac = _wasMac;
        _s?.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    ModuleSession S => _s!;
    GameState St => _s!.Game!.State;

    (MapView View, Window Window) Open(bool mac = false, bool rules = false)
    {
        ShortcutGestures.IsMac = mac;
        _s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        if (rules) _s.OpenRules(Demo("Riverland-Demo.qrules"));
        var view = new MapView(_s, _s.Model.Maps[0]) { MenuServices = new MapMenuServices { Dialogs = _dialogs } };
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        return (view, window);
    }

    List<(string Id, Point At)> LonePieces(MapView view, Func<string, bool>? where = null)
    {
        var st = S.View!;
        var result = new List<(string, Point)>();
        foreach (var stack in st.StacksOn(view.Map.Id))
        {
            if (stack.PieceIds.Count != 1) continue;
            var id = stack.PieceIds[0];
            if (where != null && !where(id)) continue;
            var sp = view.View.ToScreen(stack.Pos);
            if (sp.X < 20 || sp.Y < 20 || sp.X > W - 20 || sp.Y > H - 20) continue;
            if (HitTester.PieceAt(st, view.Map, S.Model, S.Images, stack.Pos) != id) continue;
            result.Add((id, new Point(sp.X, sp.Y)));
        }
        return result;
    }

    Point EmptySpot(MapView view)
    {
        for (var y = 30; y < H - 30; y += 20)
            for (var x = 30; x < W - 30; x += 20)
                if (HitTester.PieceAt(S.View!, view.Map, S.Model, S.Images, view.ScreenToMap(new Point(x, y))) == null) return new Point(x, y);
        throw new InvalidOperationException("no empty spot");
    }

    /// <summary>Puts a trait in front of a piece's type (with its state), as a module would define it.</summary>
    void Wrap(string id, string trait, string state)
    {
        var p = St.Pieces[id];
        p.TypeString = PieceTypeParser.EncodeState(new[] { trait, p.TypeString });
        p.TraitStates.Insert(0, state);
    }

    static void RightClick(Window w, Point p)
    {
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
    }

    /// <summary>The menu as WinForms labels: "-" for a separator, children in brackets.</summary>
    static List<string> Shape(ItemCollection items) => items.Select(i => i switch
    {
        Separator => "-",
        MenuItem m when m.Items.Count > 0 => MenuText.ToWinForms((string)m.Header!) + " [" + string.Join(" | ", Shape(m.Items)) + "]",
        MenuItem m => MenuText.ToWinForms((string)m.Header!),
        _ => "?",
    }).ToList();

    static List<string> Shape(IReadOnlyList<MenuEntry> entries) => entries.Select(e =>
        e.IsSeparator ? "-" : e.Children.Count > 0 ? e.Label + " [" + string.Join(" | ", Shape(e.Children)) + "]" : e.Label).ToList();

    static MenuItem Item(ContextMenu menu, string winFormsLabel) =>
        menu.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == winFormsLabel);

    /// <summary>Clicks a menu item and closes its menu, as a real click does.</summary>
    static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Right_click_opens_piece_menu_with_model_entries()
    {
        var (view, window) = Open();
        var (id, at) = LonePieces(view)[0];
        Wrap(id, PromptTrait, "2");

        window.MouseDown(at, MouseButton.Right);
        Assert.Equal(new[] { id }, view.SelectedPieceIds);   // as WinForms: the right press selects the counter
        Assert.Null(view.OpenMenu);                           // ...and the menu opens on release
        window.MouseUp(at, MouseButton.Right);

        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        Assert.True(menu.IsOpen);
        var model = PieceMenuModel.Build(S, view.Map.Id, new[] { id, id }, null);
        Assert.Equal(Shape(model), Shape(menu.Items));
        Assert.Contains("Ask", Shape(menu.Items));

        // Disabled entries keep their reason as a tooltip that shows on the disabled item.
        var bad = Item(menu, "Bad");
        Assert.False(bad.IsEnabled);
        Assert.Equal(model.Single(e => e.Label == "Bad").DisabledReason, ToolTip.GetTip(bad));
        Assert.True(ToolTip.GetShowOnDisabled(bad));
        Assert.True(Item(menu, "Ask").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Mac_ctrl_click_opens_menu()
    {
        var (view, window) = Open(mac: true);
        var pieces = LonePieces(view);
        Assert.True(pieces.Count >= 2);
        window.MouseDown(pieces[0].At, MouseButton.Left);
        window.MouseUp(pieces[0].At, MouseButton.Left);

        // Ctrl+click on another counter: its menu, and it is selected alone (Ctrl does not add on macOS).
        window.MouseDown(pieces[1].At, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(pieces[1].At, MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(new[] { pieces[1].Id }, view.SelectedPieceIds);
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        Assert.Equal(Shape(PieceMenuModel.Build(S, view.Map.Id, new[] { pieces[1].Id, pieces[1].Id }, null)), Shape(menu.Items));
        menu.Close();
        Assert.Null(view.OpenMenu);

        // Ctrl+click on the empty map: the map menu, the selection stays.
        var empty = EmptySpot(view);
        window.MouseDown(empty, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(empty, MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(new[] { pieces[1].Id }, view.SelectedPieceIds);
        Assert.Equal("Mark All Unmoved", Shape(Assert.IsType<ContextMenu>(view.OpenMenu).Items)[0]);
        view.OpenMenu!.Close();
        window.Close();

        // Windows / Linux: Ctrl+click adds to the selection and opens no menu.
        var (pc, w2) = Open(mac: false);
        var ps = LonePieces(pc);
        w2.MouseDown(ps[0].At, MouseButton.Left); w2.MouseUp(ps[0].At, MouseButton.Left);
        w2.MouseDown(ps[1].At, MouseButton.Left, RawInputModifiers.Control); w2.MouseUp(ps[1].At, MouseButton.Left, RawInputModifiers.Control);
        Assert.Equal(2, pc.SelectedPieceIds.Count);
        Assert.Null(pc.OpenMenu);
        w2.Close();
    }

    [AvaloniaFact]
    public void Masked_counter_menu_has_no_commands()
    {
        var (view, window) = Open(rules: true);
        S.SetViewSides(new HashSet<string> { "north" });
        Dispatcher.UIThread.RunJobs();
        var theirs = LonePieces(view, id => S.CanSee(id) && !S.CanAct(id));
        Assert.NotEmpty(theirs);
        var (id, at) = theirs[0];
        Wrap(id, ObsTrait, "x");                               // masked by its owner
        S.SetViewSides(new HashSet<string> { "north" });       // the viewer now sees the mask
        Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(St.Pieces[id].TypeString, S.View!.Pieces[id].TypeString);

        // As in WinForms (same HitTester on the same view), the masked placeholder has no picture of its own and so no area to hit: a
        // right-click there is a right-click on the empty map, whose menu shows nothing about the counter.
        RightClick(window, at);
        Assert.Equal("Mark All Unmoved", Shape(Assert.IsType<ContextMenu>(view.OpenMenu).Items)[0]);
        view.OpenMenu!.Close();

        // The counter menu itself, opened on the masked counter (e.g. one made hittable by a later hit-test change): only "(no commands)".
        view.OpenContextMenu(id);
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        var only = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal("(no commands)", MenuText.ToWinForms((string)only.Header!));
        Assert.False(only.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Property_sheet_edits_write_back()
    {
        var (view, window) = Open();
        var (id, at) = LonePieces(view)[0];
        Wrap(id, SheetTrait, "40");

        RightClick(window, at);
        _dialogs.SheetValues = null;   // Cancel: nothing changes
        Click(Item(view.OpenMenu!, "Sheet"));
        var shown = Assert.Single(_dialogs.Sheets);
        Assert.Equal("Sheet", shown.Title);
        Assert.Equal(new[] { ("Fuel", "40") }, shown.Rows);
        Assert.Equal(new string?[] { null }, shown.Managed);
        Assert.Equal("40", St.Pieces[id].TraitStates[0]);

        RightClick(window, at);
        _dialogs.SheetValues = new[] { "55" };
        Click(Item(view.OpenMenu!, "Sheet"));
        Assert.Equal(new[] { ("Fuel", "55") }, PieceActions.ReadSheet(St.Pieces[id], 0));
        window.Close();
    }

    [AvaloniaFact]
    public void Action_with_prompt_asks_input()
    {
        var (view, window) = Open();
        var (id, at) = LonePieces(view)[0];
        Wrap(id, PromptTrait, "2");

        RightClick(window, at);
        _dialogs.Texts.Enqueue(null);   // Cancel
        Click(Item(view.OpenMenu!, "Ask"));
        Assert.Equal("Ask|Value?|", Assert.Single(_dialogs.TextPrompts));
        Assert.Equal("2", St.Pieces[id].TraitStates[0]);

        RightClick(window, at);
        _dialogs.Texts.Enqueue("4");
        Click(Item(view.OpenMenu!, "Ask"));
        Assert.Equal(2, _dialogs.TextPrompts.Count);
        Assert.Equal("4", St.Pieces[id].TraitStates[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void Delete_action_asks_before_deleting()
    {
        var (view, window) = Open();
        var (id, at) = LonePieces(view)[0];
        Wrap(id, "delete;Delete;D", "");

        RightClick(window, at);
        _dialogs.Answer = false;
        Click(Item(view.OpenMenu!, "Delete"));
        var q = Assert.Single(_dialogs.Questions);
        Assert.Equal(("Delete " + PieceInfo.Name(St.Pieces[id]) + "?", "Quartermaster"), q);
        Assert.Equal(new[] { true }, _dialogs.YesNoDefaultNo);   // delete confirmations default to No
        Assert.True(St.Pieces.ContainsKey(id));

        RightClick(window, at);
        _dialogs.Answer = true;
        Click(Item(view.OpenMenu!, "Delete"));
        Assert.False(St.Pieces.ContainsKey(id));
        window.Close();
    }

    [AvaloniaFact]
    public void Empty_map_menu_items()
    {
        var (view, window) = Open();
        var empty = EmptySpot(view);
        RightClick(window, empty);
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        // WinForms MapWindow.BuildMapMenu.
        Assert.Equal(new[] { "Mark All Unmoved", "-", "Show Grid", "Zoom to Fit", "Zoom 100%", "-", "Hide counters", "Centre on selected", "Save map as picture…" }, Shape(menu.Items));
        Assert.False(Item(menu, "Hide counters").IsChecked);

        var grid = Item(menu, "Show Grid");
        Assert.Equal(MenuItemToggleType.CheckBox, grid.ToggleType);
        Assert.False(grid.IsChecked);
        Click(grid);
        Assert.True(view.ShowGrid);

        RightClick(window, empty);
        Assert.True(Item(view.OpenMenu!, "Show Grid").IsChecked);
        Click(Item(view.OpenMenu!, "Zoom 100%"));
        Assert.Equal(1f, view.View.Zoom);

        RightClick(window, empty);
        Click(Item(view.OpenMenu!, "Zoom to Fit"));
        Assert.NotEqual(1f, view.View.Zoom);

        string? message = null;
        view.StatusMessage += m => message = m;
        RightClick(window, empty);
        Click(Item(view.OpenMenu!, "Centre on selected"));
        Assert.Equal("Select a counter first", message);

        RightClick(window, empty);
        Click(Item(view.OpenMenu!, "Mark All Unmoved"));
        Assert.DoesNotContain(S.Log.Items, e => e.Severity == DiagnosticSeverity.Error);
        window.Close();
    }

    /// <summary>Runs the dispatcher loop (timers included) for a while.</summary>
    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Hidden_counters_are_not_interactive()
    {
        // As WinForms MapCanvas (PieceAt and the band check _hideCounters): with counters hidden via the map menu, the pointer finds no
        // counter: no hover or popup, no click selection, no drag, the empty-map menu on a counter, and no rubber-band selection.
        var (view, window) = Open();
        view.HoverDelayMs = 30;
        var (id, at) = LonePieces(view)[0];
        var pos = St.Pieces[id].Pos;
        var mapMenu = Shape(MapContextMenu.Build(view, null)!.Items);

        RightClick(window, EmptySpot(view));
        Click(Item(view.OpenMenu!, "Hide counters"));
        Assert.True(view.HideCounters);

        window.MouseMove(at);
        Pump(200);
        Assert.Null(view.HoverStackId);
        Assert.Null(view.Popup);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Empty(view.SelectedPieceIds);

        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(new Point(at.X + 40, at.Y + 40), RawInputModifiers.LeftMouseButton);
        Assert.False(view.IsDragging);
        window.MouseUp(new Point(at.X + 40, at.Y + 40), MouseButton.Left);
        Assert.Equal(pos, St.Pieces[id].Pos);
        Assert.Empty(view.SelectedPieceIds);   // nor did the band started there pick the counter up

        window.MouseDown(new Point(at.X - 30, at.Y - 30), MouseButton.Left);
        window.MouseMove(new Point(at.X + 30, at.Y + 30), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(at.X + 30, at.Y + 30), MouseButton.Left);
        Assert.Empty(view.SelectedPieceIds);

        RightClick(window, at);
        Assert.Empty(view.SelectedPieceIds);
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        Assert.Equal(mapMenu, Shape(menu.Items));
        Assert.True(Item(menu, "Hide counters").IsChecked);
        Click(Item(menu, "Hide counters"));
        Assert.False(view.HideCounters);

        // Shown again: the same counter is interactive.
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal(new[] { id }, view.SelectedPieceIds);
        window.Close();
    }

    [AvaloniaFact]
    public void Main_window_maps_use_its_dialogs_and_status_bar()
    {
        ShortcutGestures.IsMac = false;
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        try
        {
            w.Show();
            Assert.True(w.OpenModule(Demo("Riverland-Demo.vmod")));
            w.StartSetup(null, Difficulty.FullyAuto);
            var view = Assert.IsType<MapView>(w.MapTabs.ActiveView);
            Assert.Same(_dialogs, view.MenuServices?.Dialogs);
            view.CentreOnSelection();
            Assert.Equal("Select a counter first", w.StatusText);
        }
        finally { w.ForceClose(); }
    }

    [AvaloniaFact]
    public void Property_sheet_dialog_lists_rows_and_returns_values()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dlg = new PropertySheetDialog("Sheet", new[] { ("Fuel", "40"), ("Notes", "a\nb") }, n => n == "Fuel" ? "40" : null);
        var result = dlg.ShowDialog<IReadOnlyList<string>?>(owner);
        Dispatcher.UIThread.RunJobs();
        var boxes = dlg.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal(2, boxes.Count);
        Assert.True(boxes[0].IsReadOnly);
        Assert.False(boxes[1].IsReadOnly);
        Assert.True(boxes[1].AcceptsReturn);
        Assert.Contains(dlg.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "(managed by ledger: 40)");
        boxes[1].Text = "c";
        Button(dlg, "OK").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(result.IsCompleted);
        Assert.Equal(new[] { "40", "c" }, result.Result);

        var cancelled = new PropertySheetDialog("Sheet", new[] { ("Fuel", "40") });
        var r2 = cancelled.ShowDialog<IReadOnlyList<string>?>(owner);
        Dispatcher.UIThread.RunJobs();
        Button(cancelled, "Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(r2.Result);
        owner.Close();
    }

    [AvaloniaFact]
    public void Input_dialog_returns_text_or_null()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dlg = new InputDialog("Ask", "Value?", "");
        var result = dlg.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ask", dlg.Title);
        Assert.Contains(dlg.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Value?");
        dlg.GetVisualDescendants().OfType<TextBox>().Single().Text = "4";
        Button(dlg, "OK").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("4", result.Result);

        var cancelled = new InputDialog("Ask", "Value?", "x");
        var r2 = cancelled.ShowDialog<string?>(owner);
        Dispatcher.UIThread.RunJobs();
        Button(cancelled, "Cancel").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(r2.Result);
        owner.Close();
    }

    static Button Button(Window w, string text) => w.GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == text);
}
