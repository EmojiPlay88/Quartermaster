using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Ledgers;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>B6 task 5: the Ledger's Places tab (place rows, labels, edits, Add place, filter, CSV), the keyed values panel under the grid and
/// the viewer's view of both. Headless on the invented Riverland places module.</summary>
[Collection("MacFlag")]
public sealed class LedgerPlacesTests : IDisposable
{
    const string Global = "h:global";
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ledgerplaces-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public LedgerPlacesTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        try { File.Delete(Path.Combine(AppDataRoot.Path, "ledger-tabs.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    /// <summary>A started game on the places module with the Riverland places pack (or <paramref name="edit"/> of it).</summary>
    MainWindow Game(Func<RulesPack, RulesPack>? edit = null)
    {
        var fx = PlacesFixture.Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache" + _windows.Count), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        w.StartSetup(null);
        w.Session!.NewRules("t");
        w.Session.UpdateRules(_ => edit == null ? PlacesFixture.Pack() : edit(PlacesFixture.Pack()));
        Pump();
        return w;
    }

    static void Put(MainWindow w, string key, string prop, string valueKey, double v)
    {
        w.Session!.Game!.Execute(new LedgerDelta(key, prop, 0, v, "manual", null, null, "test", null) { ValueKey = valueKey });
        Pump();
    }

    static void PutPlaces(MainWindow w)
    {
        Put(w, Global, "p-ford", "0412", 3);
        Put(w, Global, "p-mines", "0412", 2);
        Put(w, Global, "p-ford", "0505", 1);
    }

    static LedgerWindow Open(MainWindow w, string? tab = null)
    {
        var lw = w.OpenLedger() ?? throw new InvalidOperationException("no ledger");
        if (tab != null) lw.SelectTab(tab);
        lw.Flush();
        lw.UpdateLayout();
        Pump();
        return lw;
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void PressEnter(TextBox box) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });

    static RulesPack WithoutPlaces(RulesPack p) => p with { Properties = p.Properties.Where(x => !KeyedFacts.IsPlace(x)).ToList() };

    static RulesPack WithoutKeyed(RulesPack p) => p with { Properties = p.Properties.Where(x => !KeyedFacts.IsKeyed(x)).ToList() };

    // ---------- the tab ----------

    [AvaloniaFact]
    public void Places_tab_shown_only_with_place_property()
    {
        var w = Game();
        var lw = Open(w);
        Assert.Equal(new[] { "units", "holders", "all", "places" }, lw.TabKeys);
        Assert.Equal("Places", lw.TabTitles[3]);

        // The rules change: the tab goes with the last place property and comes back with one.
        w.Session!.UpdateRules(WithoutPlaces);
        lw.Flush();
        Assert.Equal(new[] { "units", "holders", "all" }, lw.TabKeys);
        w.Session.UpdateRules(_ => PlacesFixture.Pack());
        lw.Flush();
        Assert.Equal(new[] { "units", "holders", "all", "places" }, lw.TabKeys);
    }

    [AvaloniaFact]
    public void Places_grid_rows_and_labels()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        Assert.Equal("places", lw.CurrentTabKey);
        Assert.Equal(new[] { "Hex", "FordDepth", "Mines" }, lw.Headers);
        Assert.Equal(new[] { "0412", "0505" }, lw.Rows.Select(r => r.Row.Name));
        Assert.Equal("3 (Flooded)", lw.CellText(0, 1));   // labelled like any ledger cell
        Assert.Equal("2", lw.CellText(0, 2));
        Assert.Equal("1 (Shallow)", lw.CellText(1, 1));
        Assert.Equal("", lw.CellText(1, 2));              // no value stored
        Assert.False(lw.SideAndGroupShown);
        Assert.False(lw.SideBox.IsVisible || lw.GroupBox.IsVisible);
        Assert.False(lw.UnitTogglesShown);
        Assert.True(lw.PlacesToolsShown);
        Assert.Equal("Add a row for a hex that has no place values yet.", ToolTip.GetTip(lw.AddPlaceButton));
        Assert.Equal("Hex name", lw.HexBox.PlaceholderText);
        Assert.False(lw.PlacesEmptyShown);

        lw.SelectTab("units");
        Assert.False(lw.PlacesToolsShown);
        Assert.True(lw.UnitTogglesShown);
    }

    [AvaloniaFact]
    public void Places_empty_text()
    {
        var w = Game();
        var lw = Open(w, "places");
        Assert.Empty(lw.Rows);
        Assert.True(lw.PlacesEmptyShown);
        Assert.Equal("No place values yet. A formula output keyed by hex() writes one, or add a hex here.", LedgerWindow.PlacesEmptyText);
        Put(w, Global, "p-ford", "0412", 2);
        lw.Flush();
        Assert.False(lw.PlacesEmptyShown);
    }

    [AvaloniaFact]
    public void Places_edit_cell_executes_keyed_delta()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        var s = w.Session!;
        var logged = s.Game!.Log.Count;
        _dialogs.Texts.Enqueue("the river rose");
        lw.ScrollToRow(0);
        Pump();
        Assert.True(lw.BeginEdit(0, 1));
        lw.Editor!.Text = "2";
        PressEnter(lw.Editor);
        PumpUntil(() => KeyedValues.Get(s.Game.State, Global, "p-ford", "0412") == 2);
        Assert.Contains("FordDepth[0412]", _dialogs.TextPrompts.Last());
        var delta = s.Game.Log.Skip(logged).OfType<LedgerDelta>().Single();
        Assert.Equal(("0412", 3d, 2d, "manual", "the river rose"), (delta.ValueKey, delta.Before, delta.After, delta.Source, delta.Note));
        Assert.Equal("FordDepth[0412] 3 → 2", delta.Working);

        // An unchanged value and a cancelled note write nothing; a bad number warns.
        var prompts = _dialogs.TextPrompts.Count;
        logged = s.Game.Log.Count;
        lw.ScrollToRow(0);
        Assert.True(lw.BeginEdit(0, 1));
        lw.Editor!.Text = "2";
        PressEnter(lw.Editor);
        Pump();
        Assert.Equal(prompts, _dialogs.TextPrompts.Count);
        Assert.True(lw.BeginEdit(0, 1));
        lw.Editor!.Text = "x";
        PressEnter(lw.Editor);
        PumpUntil(() => _dialogs.Warnings.Count > 0);
        Assert.Equal("'x' is not a number.", _dialogs.Warnings.Last().Message);
        Assert.Equal(logged, s.Game.Log.Count);
        Assert.False(lw.BeginEdit(0, 0));   // the Hex cell is read-only
    }

    [AvaloniaFact]
    public void Add_place_unknown_hex_status()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        lw.HexBox.Text = "9999";
        Click(lw.AddPlaceButton);
        Pump();
        Assert.Equal("No hex named \"9999\" on any board.", lw.PlaceStatusText);
        Assert.Equal(2, lw.Rows.Count);
        lw.HexBox.Text = "0101";   // a real hex clears the status
        Click(lw.AddPlaceButton);
        Assert.Equal("", lw.PlaceStatusText);
    }

    [AvaloniaFact]
    public void Add_place_adds_empty_row()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        lw.HexBox.Text = " 0101 ";
        Click(lw.AddPlaceButton);
        Pump();
        Assert.Equal(new[] { "0101", "0412", "0505" }, lw.Rows.Select(r => r.Row.Name));
        Assert.Equal("", lw.CellText(0, 1));
        Assert.Equal("", lw.CellText(0, 2));
        Assert.Equal("", lw.HexBox.Text);
        Assert.Null(KeyedValues.Get(w.Session!.Game!.State, Global, "p-ford", "0101"));   // nothing stored yet

        // The new row takes a value like any other.
        _dialogs.Texts.Enqueue("first reading");
        lw.ScrollToRow(0);
        Pump();
        Assert.True(lw.BeginEdit(0, 1));
        lw.Editor!.Text = "2";
        PressEnter(lw.Editor);
        PumpUntil(() => KeyedValues.Get(w.Session.Game.State, Global, "p-ford", "0101") == 2);
        lw.Flush();
        Assert.Equal("2 (Deep)", lw.CellText(0, 1));
    }

    [AvaloniaFact]
    public void Places_filter_by_hex()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        lw.FilterBox.Text = "05";
        lw.Flush();
        Assert.Equal(new[] { "0505" }, lw.Rows.Select(r => r.Row.Name));
        lw.FilterBox.Text = "";
        lw.Flush();
        Assert.Equal(2, lw.Rows.Count);
    }

    [AvaloniaFact]
    public void Places_csv()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        var path = Path.Combine(_tmp, "places.csv");
        _dialogs.SaveFiles.Enqueue(path);
        Click(lw.ExportButton);
        PumpUntil(() => File.Exists(path));
        Assert.Equal("ledger-places.csv", _dialogs.SuggestedNames.Last());
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal("Hex,FordDepth,Mines\r\n0412,3,2\r\n0505,1,\r\n", new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [AvaloniaFact]
    public void Guest_places_tab_hides_private_place()
    {
        var w = Game();
        PutPlaces(w);
        var lw = Open(w, "places");
        Assert.Contains("Mines", lw.Headers);
        w.Session!.SetViewSides(new HashSet<string> { "blue" });
        Pump();
        lw.Flush();
        Assert.Equal(new[] { "Hex", "FordDepth" }, lw.Headers);
        Assert.Equal(new[] { "0412", "0505" }, lw.Rows.Select(r => r.Row.Name));
        Assert.Equal("3 (Flooded)", lw.CellText(0, 1));
    }

    // ---------- keyed values panel ----------

    static void SelectHolder(LedgerWindow lw, string id)
    {
        lw.SelectTab("holders");
        lw.SelectHolder(id);
        lw.Flush();
    }

    [AvaloniaFact]
    public void Keyed_panel_lists_table_rows_for_holder()
    {
        var w = Game();
        Put(w, "h:yard", "p-hulls", "Barge", 5);
        var lw = Open(w);
        Assert.False(lw.KeyedPanelShown);
        SelectHolder(lw, "yard");
        Assert.True(lw.KeyedPanelShown);
        Assert.Equal("Keyed values of Boatyard", lw.KeyedHeaderText);
        var hulls = lw.KeyedRowTexts.Where(r => r[0] == "HullCount").ToList();
        Assert.Equal(new[] { "Barge", "Raft", "Keelboat" }, hulls.Select(r => r[1]));   // every row of the table, in table order
        Assert.Equal(new[] { "5", "1", "1" }, hulls.Select(r => r[2]));                  // the stored value, else the Default

        // Another holder, another table row set; the keyed panel follows the selection.
        lw.SelectHolder("blueyard");
        lw.Flush();
        Assert.Equal("Keyed values of Blueyard", lw.KeyedHeaderText);
    }

    [AvaloniaFact]
    public void Keyed_panel_turn_keys_and_current()
    {
        var w = Game();
        Put(w, "h:yard", "p-due", "7", 4);
        Put(w, "h:yard", "p-due", "2", 5);
        var lw = Open(w);
        SelectHolder(lw, "yard");
        var current = (w.Session!.View!.Position?.Turn ?? w.Session.View.Turn.Turn).ToString();
        var due = lw.KeyedRowTexts.Where(r => r[0] == "BargesDue").ToList();
        var keys = due.Select(r => r[1]).ToList();
        Assert.Equal(keys.OrderBy(int.Parse).ToList(), keys);   // ascending
        Assert.Contains(current, keys);                         // the current turn is always listed
        Assert.Equal("5", due.Single(r => r[1] == "2")[2]);
        Assert.Equal("4", due.Single(r => r[1] == "7")[2]);
    }

    [AvaloniaFact]
    public void Keyed_panel_hidden_without_keyed()
    {
        var w = Game(WithoutKeyed);
        var lw = Open(w);
        SelectHolder(lw, "yard");
        Assert.False(lw.KeyedPanelShown);
        Assert.Empty(lw.KeyedRowTexts);

        // On the Places tab the panel never shows either.
        w.Session!.UpdateRules(_ => PlacesFixture.Pack());
        lw.Flush();
        SelectHolder(lw, "yard");
        Assert.True(lw.KeyedPanelShown);
        lw.SelectTab("places");
        lw.Flush();
        Assert.False(lw.KeyedPanelShown);
    }

    [AvaloniaFact]
    public void Keyed_panel_edit_writes()
    {
        var w = Game();
        var lw = Open(w);
        var s = w.Session!;
        SelectHolder(lw, "yard");
        var at = lw.KeyedRowTexts.ToList().FindIndex(r => r[0] == "HullCount" && r[1] == "Raft");
        Assert.True(at >= 0);
        var logged = s.Game!.Log.Count;
        _dialogs.Texts.Enqueue("new raft built");
        var box = lw.BeginKeyedEdit(at) ?? throw new InvalidOperationException("no editor");
        box.Text = "5";
        PressEnter(box);
        PumpUntil(() => KeyedValues.Get(s.Game.State, "h:yard", "p-hulls", "Raft") == 5);
        Assert.Contains("HullCount[Raft]", _dialogs.TextPrompts.Last());
        var delta = s.Game.Log.Skip(logged).OfType<LedgerDelta>().Single();
        Assert.Equal(("Raft", 1d, 5d, "new raft built"), (delta.ValueKey, delta.Before, delta.After, delta.Note));   // Before: the Default
        lw.Flush();
        Assert.Equal("5", lw.KeyedRowTexts.Single(r => r[0] == "HullCount" && r[1] == "Raft")[2]);

        // The same value again writes nothing.
        logged = s.Game.Log.Count;
        box = lw.BeginKeyedEdit(at)!;
        box.Text = "5";
        PressEnter(box);
        Pump();
        Assert.Equal(logged, s.Game.Log.Count);
    }

    [AvaloniaFact]
    public void Keyed_panel_hidden_value_is_not_the_default()
    {
        var w = Game();
        Put(w, "h:blueyard", "p-hulls", "Raft", 3);
        var s = w.Session!;
        var cells = LedgerViewRules.KeyedRows(PlayerView.For(s.Game!.State, s.Rules!, s.Model, new HashSet<string> { "red" }), s.Rules!, "h:blueyard");
        Assert.NotEmpty(cells);
        Assert.All(cells, c => { Assert.True(c.Hidden); Assert.Null(c.Value); });
        Assert.All(cells, c => Assert.Equal(ModuleSession.HiddenText, LedgerViewRules.KeyedValueText(c)));
    }

    // ---------- old packs ----------

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    [AvaloniaFact]
    public void Old_pack_ledger_tabs_and_columns_unchanged()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache-old"), null, Path.Combine(_tmp, "layout-old")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(Demo("Riverland-Demo.vmod")));
        w.StartSetup(null);
        w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        var lw = Open(w);
        Assert.Equal(new[] { "units", "holders", "all" }, lw.TabKeys);
        Assert.Equal(new[] { "Name", "Kind" }, lw.Headers.Take(2));
        Assert.True(lw.SideAndGroupShown);
        Assert.True(lw.UnitTogglesShown);
        Assert.False(lw.PlacesToolsShown);
        Assert.False(lw.KeyedPanelShown);
        var expected = LedgerTabs.Rows(LedgerTabs.Tabs(Array.Empty<UserLedgerTab>())[0], w.Session.View!, w.Session.Rules!, w.Session.Model, "", null, LedgerGrouping.Map, false);
        Assert.Equal(expected.Columns.Select(c => c.Name), lw.Headers.Skip(2));
    }
}
