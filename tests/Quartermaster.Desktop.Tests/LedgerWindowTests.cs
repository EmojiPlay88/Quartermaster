using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Ledgers;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 4: the Ledger window (tabs, filter, side, grouping, counter pictures and preview, breakdown tips, manual edits, CSV
/// export, preferences), opened by View > Ledger / Ctrl+L. Headless on the Riverland demo (prefs in the per-process temp root).</summary>
[Collection("MacFlag")]
public sealed class LedgerWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ledger-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public LedgerWindowTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        // Every test starts from the default Ledger preferences (the store is one file under the per-process test root).
        try { File.Delete(Path.Combine(AppDataRoot.Path, "ledger-tabs.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        CultureInfo.CurrentCulture = _culture;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

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

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    MainWindow Game(bool rules = true)
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        w.StartSetup(null);
        if (rules) w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        return w;
    }

    static LedgerWindow Open(MainWindow w)
    {
        var lw = w.OpenLedger() ?? throw new InvalidOperationException("no ledger");
        lw.Flush();
        lw.UpdateLayout();
        Pump();
        return lw;
    }

    static void SetLedger(MainWindow w, string id, string prop, double value, string note = "test")
    {
        var st = w.Session!.Game!.State;
        var key = LedgerKey.Piece(id);
        w.Session.Game.Execute(new LedgerDelta(key, prop, Ledger.Get(st, key, prop) ?? 0, value, "manual", null, null, note, null));
        Pump();
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void DoubleClick(Window win, Point at)
    {
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);
        Pump();
    }

    static void PressEnter(TextBox box) =>
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });

    static int Col(LedgerWindow lw, string header) => lw.Headers.ToList().IndexOf(header);

    static LedgerTabResult Expected(MainWindow w, string tabKey, string filter = "", string? side = null, LedgerGrouping group = LedgerGrouping.Map, bool empty = false)
    {
        var s = w.Session!;
        var tab = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>()).First(t => t.Key == tabKey);
        return LedgerTabs.Rows(tab, s.View!, s.Rules!, s.Model, filter, side, group, empty);
    }

    /// <summary>The first unit row (on screen after scrolling to it) whose piece matches.</summary>
    static int UnitRow(LedgerWindow lw, Func<LedgerRowEx, bool>? where = null)
    {
        var i = lw.Rows.ToList().FindIndex(r => r.PieceId != null && (where == null || where(r)));
        Assert.True(i >= 0, "no unit row");
        lw.ScrollToRow(i);
        Pump();
        return i;
    }

    // ---------- opening ----------

    [AvaloniaFact]
    public void Ctrl_L_opens_ledger_with_units_tab()
    {
        var w = Game(rules: false);
        var item = w.FindMenuItem("&Ledger")!;
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Control), item.InputGesture);
        Assert.False(item.IsEnabled);   // needs a rules pack (WinForms gate)
        w.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
        Pump();
        Assert.Null(w.Ledger);

        w.Session!.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        Assert.True(w.FindMenuItem("&Ledger")!.IsEnabled);
        w.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
        Pump();
        var lw = Assert.IsType<LedgerWindow>(w.Ledger);
        lw.Flush();
        Assert.Equal("Ledger", lw.Title);
        Assert.Equal((820d, 560d), (lw.Width, lw.Height));
        Assert.Same(w, lw.Owner);
        Assert.Equal("units", lw.CurrentTabKey);
        Assert.Equal(new[] { "Filter:", "[filter]", "|", "Side:", "[combo]", "Group by:", "[combo]", "Show counters", "Edit tabs…", "|", "Export CSV…", "Show units without values", "Refresh" },
            lw.ToolbarTexts);
        Assert.Equal(new[] { "All sides", "Northern League", "Southern Compact" }, lw.SideBox.Items.Cast<string>());
        Assert.Equal(new[] { "Map", "Side", "None" }, lw.GroupBox.Items.Cast<string>());
        Assert.Equal("All sides", lw.SideBox.SelectedItem);
        Assert.Equal("Map", lw.GroupBox.SelectedItem);
        Assert.True(lw.CountersButton.IsChecked);
        Assert.Equal("Filter by name or kind", ToolTip.GetTip(lw.FilterBox));
        Assert.Equal("Save the current tab's rows and columns as a CSV file", ToolTip.GetTip(lw.ExportButton));
        // Edit tabs… and "+" are there but open nothing yet (the tab editor is the next task): disabled, WinForms tooltips.
        Assert.False(lw.EditTabsButton.IsEnabled);
        Assert.Equal("Add, remove or change your own Ledger tabs (kept on this PC)", ToolTip.GetTip(lw.EditTabsButton));
        Assert.False(lw.PlusTabItem.IsEnabled);
        Assert.Equal("Add a tab of your own (kept on this PC)", ToolTip.GetTip(lw.PlusTabItem));
        // Units tab: Name, Kind, then the pieces' properties; header tooltips are the notes.
        Assert.Equal(new[] { "Name", "Kind", "Ammo", "Fuel", "Readiness" }, lw.Headers);
        Assert.Equal(w.Session.Rules!.ById<Quartermaster.Rules.PropertyDef>("fuel")!.Notes, lw.HeaderTip(Col(lw, "Fuel")));
        Assert.Null(lw.HeaderTip(0));

        // Single instance: Ctrl+L or the menu again brings the same window back.
        w.ClickMenu("&Ledger");
        Pump();
        Assert.Same(lw, w.Ledger);
        lw.Close();
        Pump();
        Assert.Null(w.Ledger);
    }

    [AvaloniaFact]
    public void Tabs_show_counts()
    {
        var w = Game();
        var sha = w.Session!.Archive.Sha256;
        LedgerTabStore.Save(AppDataRoot.Path, sha, LedgerPrefs.Default with
        {
            Tabs = new[]
            {
                new UserLedgerTab("Supply", "Fuel < 1000", new[] { "fuel" }, true, "Units that still have fuel"),
                new UserLedgerTab("Fuel check", "Fuel <", Array.Empty<string>(), false, null),
            },
        });
        var lw = Open(w);
        var units = Expected(w, "units");
        var holders = Expected(w, "holders", group: LedgerGrouping.None);
        Assert.True(holders.Count > 0);
        Assert.Equal(new[] { $"Units ({units.Count:N0})", $"Depots & Global ({holders.Count:N0})", "Everything", "★ Supply", "★ Fuel check", "+" }, lw.TabTitles);
        Assert.Equal("Units that still have fuel", lw.TabTip(3));
        Assert.True(lw.SideAndGroupShown);

        lw.SelectTab("holders");
        Assert.Equal("holders", lw.CurrentTabKey);
        Assert.False(lw.SideAndGroupShown);   // Side and Group by apply to unit tabs only
        Assert.Equal(new[] { "Name", "Kind", "Stores", "Weather" }, lw.Headers);
        Assert.Equal(holders.Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));

        lw.SelectTab("user:Supply");
        Assert.True(lw.SideAndGroupShown);
        Assert.Equal(new[] { "Name", "Kind", "Fuel" }, lw.Headers);
        Assert.False(lw.BannerShown);
        lw.SelectTab("user:Fuel check");
        Assert.True(lw.BannerShown);
        Assert.StartsWith("The unit filter of \"Fuel check\" does not parse, so this tab shows no rows: ", lw.BannerTextShown);
        Assert.Empty(lw.Rows);

        // The counts follow the filter.
        lw.SelectTab("units");
        Assert.False(lw.BannerShown);
        lw.FilterBox.Text = "zzz-no-such-unit";
        lw.Flush();
        Assert.Equal("Units (0)", lw.TabTitles[0]);
        Assert.Equal("Depots & Global (0)", lw.TabTitles[1]);
    }

    [AvaloniaFact]
    public void Filter_and_side_and_group_by()
    {
        var w = Game();
        var lw = Open(w);
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        var all = Expected(w, "units", empty: true);
        Assert.Equal(all.Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));
        // Grouped by map: bold header rows.
        var header = lw.Rows.ToList().FindIndex(r => r.IsGroupHeader);
        Assert.Equal(0, header);
        lw.ScrollToRow(0);
        Assert.True(lw.IsBoldRow(0));

        // The text filter (name or kind), after the pause.
        var name = lw.Rows.First(r => r.PieceId != null).Row.Name;
        lw.FilterBox.Text = name;
        lw.Flush();
        var filtered = Expected(w, "units", name, empty: true);
        Assert.Equal(filtered.Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));
        Assert.Equal($"Units ({filtered.Count:N0})", lw.TabTitles[0]);
        lw.FilterBox.Text = "";
        lw.Flush();

        // One side only.
        lw.SideBox.SelectedIndex = 1;
        Pump();
        var north = Expected(w, "units", side: "north", empty: true);
        Assert.Equal(north.Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));
        Assert.All(lw.Rows.Where(r => !r.IsGroupHeader), r => Assert.Equal("north", r.SideId));
        Assert.True(north.Count < all.Count);

        // Group by side, then none.
        lw.SideBox.SelectedIndex = 0;
        lw.GroupBox.SelectedItem = "Side";
        Pump();
        Assert.Equal(new[] { "Northern League", "Southern Compact" }, lw.Rows.Where(r => r.IsGroupHeader).Select(r => r.Row.Name).Take(2));
        lw.GroupBox.SelectedItem = "None";
        Pump();
        Assert.DoesNotContain(lw.Rows, r => r.IsGroupHeader);
        Assert.Equal(Expected(w, "units", group: LedgerGrouping.None, empty: true).Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));

        // A click on a header sorts within the groups (blanks last).
        lw.ClickHeader(0);
        Assert.Equal("▲", lw.SortGlyph(0));
        Assert.Equal(LedgerViewRules.Sort(Expected(w, "units", group: LedgerGrouping.None, empty: true).Rows, 0, true).Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));
        lw.ClickHeader(0);
        Assert.Equal("▼", lw.SortGlyph(0));
    }

    // ---------- hidden information ----------

    [AvaloniaFact]
    public void Ledger_hides_other_side_private_values()
    {
        var w = Game();
        var s = w.Session!;
        foreach (var id in s.Game!.State.Pieces.Keys.ToList()) SetLedger(w, id, "readiness", 1);   // Readiness is private to the owning side
        var lw = Open(w);
        var col = Col(lw, "Readiness");
        Assert.All(lw.Rows.Where(r => r.PieceId != null).Select(r => lw.CellText(lw.Rows.ToList().IndexOf(r), col)), t => Assert.Equal("1", t));

        s.SetViewSides(new HashSet<string> { "north" });
        Pump();
        lw.Flush();
        Assert.True(s.ViewFiltered);
        var seen = Expected(w, "units");
        Assert.Equal(seen.Rows.Select(r => r.Row.Key), lw.Rows.Select(r => r.Row.Key));
        var rows = lw.Rows.ToList();
        var others = rows.Select((r, i) => (r, i)).Where(x => x.r.PieceId != null && x.r.SideId == "south").ToList();
        Assert.NotEmpty(others);
        Assert.All(others, x => Assert.Equal("", lw.CellText(x.i, col)));
        Assert.All(rows.Select((r, i) => (r, i)).Where(x => x.r.SideId == "north"), x => Assert.Equal("1", lw.CellText(x.i, col)));
        Assert.All(rows.Where(r => r.PieceId != null), r => Assert.True(s.View!.Pieces.ContainsKey(r.PieceId!)));

        // Typing into the other side's value is refused with the WinForms warning; nothing is written.
        var (other, at) = others[0];
        lw.ScrollToRow(at);
        Assert.True(lw.BeginEdit(at, col));
        lw.Editor!.Text = "5";
        PressEnter(lw.Editor);
        Pump();
        Assert.Contains(("That value " + ModuleSession.OtherSideText + ".", "Ledger"), _dialogs.Warnings);
        Assert.Equal(1, Ledger.Get(s.Game.State, other.Row.Key, "readiness"));
        Assert.Empty(_dialogs.TextPrompts);
    }

    [AvaloniaFact]
    public void Ledger_pictures_only_for_visible_pieces()
    {
        var w = Game();
        var s = w.Session!;
        s.SetViewSides(new HashSet<string> { "north" });
        Pump();
        var lw = Open(w);
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        lw.UpdateLayout();
        Assert.True(lw.CountersButton.IsChecked);
        var shown = Enumerable.Range(0, lw.Rows.Count).Where(i => lw.RowControl(i) != null).ToList();
        Assert.NotEmpty(shown);
        PumpUntil(() => shown.Where(i => lw.Rows[i].PieceId != null).All(i => lw.PictureOf(i) != null));
        foreach (var i in shown)
        {
            var r = lw.Rows[i];
            if (r.PieceId != null)
            {
                Assert.True(s.View!.Pieces.ContainsKey(r.PieceId));
                Assert.Equal((64, 48), (lw.PictureOf(i)!.PixelSize.Width, lw.PictureOf(i)!.PixelSize.Height));
                Assert.Equal(52, lw.RowHeightOf(i));
            }
            else
            {
                Assert.Null(lw.PictureOf(i));   // holder and group rows have none
                Assert.False(lw.PlaceholderShown(i));
                Assert.Equal(r.IsGroupHeader ? 26 : 22, lw.RowHeightOf(i));
            }
        }
        // The map turns the other side's: its units are hidden from this viewer, so they have no rows and no picture is drawn for them.
        s.UpdateRules(r => r with { MapOwners = new List<Quartermaster.Rules.MapOwner> { new() { MapId = s.Model.Maps[0].Id, SideId = "south" } } });
        Pump();
        lw.Flush();
        lw.UpdateLayout();
        var hidden = s.Game!.State.Pieces.Keys.Where(id => !s.View!.Pieces.ContainsKey(id)).ToList();
        Assert.NotEmpty(hidden);
        Assert.DoesNotContain(lw.Rows, r => r.PieceId != null && hidden.Contains(r.PieceId));
        var pending = lw.Thumbnails.PendingCount;
        Assert.All(hidden, id => Assert.Null(lw.Thumbnails.Get(id, new PixelSize(64, 48), null)));
        Assert.Equal(pending, lw.Thumbnails.PendingCount);   // nothing queued for them
        Assert.All(Enumerable.Range(0, lw.Rows.Count).Where(i => lw.RowControl(i) != null), i => Assert.Null(lw.PictureOf(i)));
        s.UpdateRules(r => r with { MapOwners = new List<Quartermaster.Rules.MapOwner>() });
        Pump();
        lw.Flush();
        lw.UpdateLayout();

        // Show counters off: compact rows without pictures.
        lw.CountersButton.IsChecked = false;
        Pump();
        lw.UpdateLayout();
        var first = Enumerable.Range(0, lw.Rows.Count).First(i => lw.Rows[i].PieceId != null && lw.RowControl(i) != null);
        Assert.Null(lw.PictureOf(first));
        Assert.Equal(22, lw.RowHeightOf(first));
    }

    // ---------- hover ----------

    [AvaloniaFact]
    public void Hover_cell_shows_breakdown()
    {
        var w = Game();
        var s = w.Session!;
        var id = s.Game!.State.Pieces.Values.First(p => s.View!.Pieces.ContainsKey(p.Id) && Ledger.Get(s.Game.State, LedgerKey.Piece(p.Id), "fuel") != null).Id;
        SetLedger(w, id, "fuel", 3, "forced march");
        var lw = Open(w);
        var row = UnitRow(lw, r => r.PieceId == id);
        var col = Col(lw, "Fuel");
        Assert.Equal("3", lw.CellText(row, col));
        lw.MouseMove(lw.CellBounds(row, col).Center);
        Pump();
        var expected = LedgerBreakdown.Format(LedgerBreakdown.For(s.View!, s.Rules, s.Model, LedgerKey.Piece(id), "fuel"));
        Assert.Equal(expected, lw.TipText);
        Assert.Contains("forced march", lw.TipText);
        // Leaving for the Kind cell hides it.
        lw.MouseMove(lw.CellBounds(row, 1).Center);
        Pump();
        Assert.Null(lw.TipText);

        // A negative value is red.
        SetLedger(w, id, "fuel", -2);
        lw.Flush();
        row = UnitRow(lw, r => r.PieceId == id);
        Assert.Equal("-2", lw.CellText(row, col));
        Assert.True(lw.CellIsRed(row, col));
    }

    [AvaloniaFact]
    public void Hover_picture_shows_preview()
    {
        var w = Game();
        var lw = Open(w);
        var row = UnitRow(lw);
        var r = lw.Rows[row];
        Assert.False(lw.Preview.IsOpen);
        var pic = lw.PictureBounds(row);
        Assert.Equal((64d, 48d), (pic.Width, pic.Height));
        lw.MouseMove(pic.Center);
        PumpUntil(() => lw.Preview.IsOpen);
        Assert.Equal(r.Row.Name, lw.Preview.Label);
        Assert.Equal(r.PieceId, lw.Preview.PieceId);
        // Off the picture (onto the name): hidden.
        lw.MouseMove(new Point(lw.CellBounds(row, 1).Center.X, pic.Center.Y));
        Pump();
        Assert.False(lw.Preview.IsOpen);
        // Back on it, then a click: hidden.
        lw.MouseMove(pic.Center);
        PumpUntil(() => lw.Preview.IsOpen);
        lw.MouseDown(pic.Center, MouseButton.Left);
        lw.MouseUp(pic.Center, MouseButton.Left);
        Pump();
        Assert.False(lw.Preview.IsOpen);
    }

    // ---------- manual edits ----------

    [AvaloniaFact]
    public void Manual_edit_asks_note_and_writes()
    {
        var w = Game();
        var s = w.Session!;
        var lw = Open(w);
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        var row = UnitRow(lw);
        var key = lw.Rows[row].Row.Key;
        var col = Col(lw, "Fuel");

        // Double-click a value cell edits it in place (WinForms EditOnKeystrokeOrF2).
        DoubleClick(lw, lw.CellBounds(row, col).Center);
        var box = Assert.IsType<TextBox>(lw.Editor);
        box.Text = "4";
        _dialogs.Texts.Enqueue("resupply");
        PressEnter(box);
        Pump();
        Assert.Null(lw.Editor);
        Assert.Equal("Manual change|Why is Fuel being set to 4? (required)|", _dialogs.TextPrompts.Last());
        Assert.Equal(4, Ledger.Get(s.Game!.State, key, "fuel"));
        var entry = s.Game.State.LedgerHistory.Last(e => e.Key == key && e.PropertyId == "fuel");
        Assert.Equal("resupply", entry.Note);
        lw.Flush();
        Assert.Equal("4", lw.CellText(lw.Rows.ToList().FindIndex(r => r.Row.Key == key), col));

        // Cancelling the note writes nothing; the same value again asks nothing and writes nothing.
        var prompts = _dialogs.TextPrompts.Count;
        row = UnitRow(lw, r => r.Row.Key == key);
        Assert.True(lw.BeginEdit(row, col));
        lw.Editor!.Text = "7";
        PressEnter(lw.Editor);   // no answer queued: Cancel
        Pump();
        Assert.Equal(prompts + 1, _dialogs.TextPrompts.Count);
        Assert.Equal(4, Ledger.Get(s.Game.State, key, "fuel"));
        row = UnitRow(lw, r => r.Row.Key == key);
        Assert.True(lw.BeginEdit(row, col));
        lw.Editor!.Text = "4";
        PressEnter(lw.Editor);
        Pump();
        Assert.Equal(prompts + 1, _dialogs.TextPrompts.Count);
        // Name and Kind are not editable.
        Assert.False(lw.BeginEdit(row, 0));
        Assert.False(lw.BeginEdit(row, 1));
    }

    [AvaloniaFact]
    public void Manual_edit_not_a_number_warns()
    {
        var w = Game();
        var s = w.Session!;
        var lw = Open(w);
        var row = UnitRow(lw);
        var key = lw.Rows[row].Row.Key;
        var col = Col(lw, "Fuel");
        var old = Ledger.Get(s.Game!.State, key, "fuel");
        Assert.True(lw.BeginEdit(row, col));
        lw.Editor!.Text = "abc";
        PressEnter(lw.Editor);
        Pump();
        Assert.Contains(("'abc' is not a number.", "Ledger"), _dialogs.Warnings);
        Assert.Empty(_dialogs.TextPrompts);
        Assert.Equal(old, Ledger.Get(s.Game.State, key, "fuel"));
    }

    [AvaloniaFact]
    public void Manual_edit_parses_comma_decimal_in_de_DE()
    {
        var w = Game();
        var s = w.Session!;
        var lw = Open(w);
        var row = UnitRow(lw);
        var key = lw.Rows[row].Row.Key;
        var col = Col(lw, "Fuel");
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Assert.True(lw.BeginEdit(row, col));
        lw.Editor!.Text = "2,5";
        _dialogs.Texts.Enqueue("half a load");
        PressEnter(lw.Editor);
        Pump();
        Assert.Empty(_dialogs.Warnings);
        Assert.Equal(2.5, Ledger.Get(s.Game!.State, key, "fuel"));
    }

    // ---------- export ----------

    [AvaloniaFact]
    public void Export_csv_writes_bom_utf8()
    {
        var w = Game();
        var lw = Open(w);
        var path = Path.Combine(_tmp, "out.csv");
        _dialogs.SaveFiles.Enqueue(path);
        Click(lw.ExportButton);
        PumpUntil(() => File.Exists(path));
        Assert.Equal("ledger-units.csv", _dialogs.SuggestedNames.Last());
        Assert.Equal("csv", _dialogs.DefaultExtensions.Last());
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(LedgerTabs.ToCsv(Expected(w, "units")), text);
        Assert.StartsWith("Name,Kind,Ammo,Fuel,Readiness", text);
        Assert.DoesNotContain("group:", text);
    }

    // ---------- preferences ----------

    [AvaloniaFact]
    public void Prefs_persist_per_module()
    {
        var w = Game();
        var sha = w.Session!.Archive.Sha256;
        var lw = Open(w);
        lw.SideBox.SelectedIndex = 1;
        lw.GroupBox.SelectedItem = "Side";
        lw.CountersButton.IsChecked = false;
        lw.SetColumnWidth(1, 200);
        Pump();
        lw.SelectTab("all");
        lw.SetColumnWidth(0, 250);
        lw.Close();   // a pending width change is written on close
        Pump();
        var p = LedgerTabStore.Load(AppDataRoot.Path, sha);
        Assert.Equal(("all", false, "north", LedgerGrouping.Side), (p.LastTab, p.ShowCounters, p.Side, p.Grouping));
        Assert.Equal(200, p.ColumnWidths["units"][1]);
        Assert.Equal(250, p.ColumnWidths["all"][0]);

        lw = Open(w);
        Assert.Equal("all", lw.CurrentTabKey);
        Assert.Equal(250, lw.ColumnWidths[0]);
        Assert.False(lw.CountersButton.IsChecked);
        Assert.Equal("Northern League", lw.SideBox.SelectedItem);
        Assert.Equal("Side", lw.GroupBox.SelectedItem);
        lw.SelectTab("units");
        Assert.Equal(200, lw.ColumnWidths[1]);

        // Another module has its own (default) preferences.
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w2 = NewWindow();
        Assert.True(w2.OpenModule(fx.Build()));
        w2.StartSetup(null);
        var s2 = w2.Session!;
        s2.NewRules("t");
        s2.UpdateRules(_ => PhaseFixture.Pack(s2.Archive.Sha256));
        Pump();
        Assert.NotEqual(sha, s2.Archive.Sha256);
        var other = Open(w2);
        Assert.Equal("units", other.CurrentTabKey);
        Assert.True(other.CountersButton.IsChecked);
        Assert.Equal(LedgerPrefs.Default.Grouping, other.Prefs.Grouping);
        Assert.Null(other.Prefs.Side);
    }

    // ---------- double-click ----------

    [AvaloniaFact]
    public void Double_click_row_centres_piece()
    {
        var w = Game();
        var lw = Open(w);
        var row = UnitRow(lw);
        var id = lw.Rows[row].PieceId!;
        void Clear() { foreach (var v in w.Maps.Views) v.SelectOnly(null); }
        bool NoneSelected() => w.Maps.Views.All(v => v.SelectedPieceIds.Count == 0);

        // As WinForms (DoubleClickCounter / OverImage): only a double-click on the counter picture centres.
        Clear();
        DoubleClick(lw, lw.PictureBounds(row).Center);
        Assert.Contains(id, w.Maps.ActiveView!.SelectedPieceIds);
        Assert.Null(lw.Editor);

        // Name text and Kind are read-only cells: a double-click there does nothing.
        Clear();
        var name = lw.CellBounds(row, 0);
        DoubleClick(lw, new Point(name.Right - 10, name.Center.Y));
        Assert.True(NoneSelected());
        DoubleClick(lw, lw.CellBounds(row, 1).Center);
        Assert.True(NoneSelected());
        Assert.Null(lw.Editor);

        // A group header row centres nothing.
        lw.ScrollToRow(0);
        Assert.True(lw.Rows[0].IsGroupHeader);
        DoubleClick(lw, lw.CellBounds(0, 0).Center);
        Assert.True(NoneSelected());

        // Show counters off: no picture, so nothing centres anywhere in the row.
        lw.CountersButton.IsChecked = false;
        Pump();
        lw.UpdateLayout();
        row = UnitRow(lw, r => r.PieceId == id);
        DoubleClick(lw, new Point(lw.CellBounds(row, 0).X + 34, lw.CellBounds(row, 0).Center.Y));
        DoubleClick(lw, lw.CellBounds(row, 1).Center);
        Assert.True(NoneSelected());
    }

    [AvaloniaFact]
    public void Typing_on_value_cell_starts_edit()
    {
        var w = Game();
        var s = w.Session!;
        var lw = Open(w);
        var row = UnitRow(lw);
        var key = lw.Rows[row].Row.Key;
        var col = Col(lw, "Fuel");
        var at = lw.CellBounds(row, col).Center;
        lw.MouseDown(at, MouseButton.Left);
        lw.MouseUp(at, MouseButton.Left);
        Pump();
        lw.KeyTextInput("7");   // WinForms EditOnKeystrokeOrF2: the keystroke opens the cell with what was typed
        Pump();
        var box = Assert.IsType<TextBox>(lw.Editor);
        Assert.Equal("7", box.Text);
        Assert.Equal(1, box.CaretIndex);
        _dialogs.Texts.Enqueue("typed");
        PressEnter(box);
        Pump();
        Assert.Equal("Manual change|Why is Fuel being set to 7? (required)|", _dialogs.TextPrompts.Last());
        Assert.Equal(7, Ledger.Get(s.Game!.State, key, "fuel"));

        // On the Kind cell the keystroke edits the row's first value column, as F2 does.
        lw.Flush();
        row = UnitRow(lw, r => r.Row.Key == key);
        at = lw.CellBounds(row, 1).Center;
        lw.MouseDown(at, MouseButton.Left);
        lw.MouseUp(at, MouseButton.Left);
        Pump();
        lw.KeyTextInput("3");
        Pump();
        Assert.Equal("3", lw.Editor?.Text);
        lw.EndEdit(false);
        Assert.Null(lw.Editor);
    }

    // ---------- large games ----------

    [AvaloniaFact]
    public void Ledger_1200_units_reload_under_2s()
    {
        var w = Game();
        var s = w.Session!;
        var g = s.Game!;
        var src = g.State.Pieces.Values.First(p => s.View!.Pieces.ContainsKey(p.Id) && Ledger.Get(g.State, LedgerKey.Piece(p.Id), "fuel") != null);
        var state = src.TraitStates.All(x => x.Length == 0) ? "" : PieceTypeParser.EncodeState(src.TraitStates);
        for (var i = 0; i < 1200; i++)
            g.State.Apply(new AddPiece("big" + i, src.TypeString, state, src.MapId, new PointD(src.Pos.X + i % 40, src.Pos.Y + i / 40), null));
        s.SetViewSides(null);   // the view is read again
        Pump();
        Assert.True(s.View!.Pieces.Count >= 1200);

        var lw = Open(w);
        lw.ShowEmptyButton.IsChecked = true;
        lw.Flush();
        Assert.True(lw.CountersButton.IsChecked);
        Assert.True(lw.Rows.Count(r => r.PieceId != null) >= 1200);

        var sw = Stopwatch.StartNew();
        lw.Reload();
        lw.UpdateLayout();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"reload took {sw.ElapsedMilliseconds} ms");
        // Virtualised: only the rows on screen are built, and only their pictures are drawn.
        Assert.InRange(lw.LiveRowCount, 1, 60);
        var live = Enumerable.Range(0, lw.Rows.Count).Where(i => lw.RowControl(i) != null && lw.Rows[i].PieceId != null).ToList();
        PumpUntil(() => live.All(i => lw.PictureOf(i) != null));
        Assert.True(lw.Thumbnails.Count <= 600);
        Assert.True(lw.Thumbnails.Count < 100, $"{lw.Thumbnails.Count} pictures drawn for {lw.LiveRowCount} rows on screen");

        // Scrolling far down builds the rows there.
        lw.ScrollToRow(lw.Rows.Count - 1);
        Assert.NotNull(lw.RowControl(lw.Rows.Count - 1));
        Assert.InRange(lw.LiveRowCount, 1, 60);
    }
}
