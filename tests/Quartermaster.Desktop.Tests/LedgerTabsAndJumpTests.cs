using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Ledgers;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 5: Edit Ledger tabs (Edit tabs… and "+"), Show in Ledger (Ctrl/Cmd+L with counters selected, the counter menu) and
/// holder alert matches opening the Ledger. Headless on the Riverland demo (prefs in the per-process temp root).</summary>
[Collection("MacFlag")]
public sealed class LedgerTabsAndJumpTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ledgertabs-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<Window> _others = new();
    readonly FakeDialogs _dialogs = new();

    public LedgerTabsAndJumpTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        try { File.Delete(Path.Combine(AppDataRoot.Path, "ledger-tabs.json")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        foreach (var o in _others) o.Close();
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
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

    MainWindow Game(bool rules = true)
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
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

    static RulesPack Pack() => RulesPackIo.Load(Demo("Riverland-Demo.qrules"));

    EditTabsDialog Dialog(IReadOnlyList<UserLedgerTab> tabs, int select = 0, bool addNew = false, RulesPack? pack = null)
    {
        var d = new EditTabsDialog(tabs, pack ?? Pack(), select, addNew);
        _others.Add(d);
        d.Show();
        Pump();
        return d;
    }

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    static void ClickMenu(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        item.FindLogicalAncestorOfType<ContextMenu>()?.Close();
        Pump();
    }

    static void Same(UserLedgerTab expected, UserLedgerTab actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Filter, actual.Filter);
        Assert.Equal(expected.ColumnIds, actual.ColumnIds);
        Assert.Equal(expected.IncludeHolders, actual.IncludeHolders);
        Assert.Equal(expected.Notes, actual.Notes);
    }

    /// <summary>A unit with a row on the Units tab (it has Ledger values) that is on the active map.</summary>
    static string UnitWithRow(MainWindow w)
    {
        var s = w.Session!;
        var map = w.Maps.ActiveView!.Map.Id;
        var rows = LedgerTabs.Rows(LedgerTabs.Tabs(Array.Empty<UserLedgerTab>())[0], s.View!, s.Rules!, s.Model, "", null, LedgerGrouping.None, false);
        return rows.Rows.Select(r => LedgerKey.TryParse(r.Row.Key, out var isPiece, out var id) && isPiece ? id : null)
            .First(id => id != null && s.View!.Pieces.TryGetValue(id, out var p) && p.MapId == map)!;
    }

    // ---------- the dialog ----------

    [AvaloniaFact]
    public void Edit_tabs_add_rename_filter_columns_roundtrip()
    {
        var pack = Pack();
        var start = new[] { new UserLedgerTab("Supply", "Fuel < 1000", new[] { "fuel" }, false, null) };
        var d = Dialog(start, pack: pack);
        Assert.Equal("Edit Ledger tabs", d.Title);
        Assert.Equal((720d, 460d), (d.Width, d.Height));
        Assert.Equal(new[] { "Add", "Remove", "Move up", "Move down" }, new[] { d.AddButton, d.RemoveButton, d.UpButton, d.DownButton }.Select(b => b.Content as string));
        Assert.Equal(new[] { "Name:", "Unit filter:", "Columns:", "Notes:" }, d.FieldLabels);
        Assert.Contains("Empty means all units. Example: Fuel < 5", d.Hints);
        Assert.Contains("None ticked means the same columns as the Units tab.", d.Hints);
        Assert.Equal("Include depots & Global", d.HoldersBox.Content);
        Assert.Equal(new[] { "Up", "Down" }, new[] { d.ColUpButton, d.ColDownButton }.Select(b => b.Content as string));
        Assert.Equal(new[] { "OK", "Cancel" }, new[] { d.OkButton, d.CancelButton }.Select(b => b.Content as string));
        Assert.Equal(new[] { "★ Supply" }, d.ListItems);
        Assert.Equal(0, d.SelectedIndex);
        Assert.Equal("Supply", d.NameBox.Text);
        Assert.Equal("Fuel < 1000", d.FilterBox.Text);
        // The tab's ticked columns first (in its order), then the rest of the pack's columns; holder columns are marked.
        var all = LedgerTable.Columns(pack);
        var fuel = all.First(p => p.Id == "fuel");
        Assert.Equal(fuel.Name, d.ColumnItems[0].Text);
        Assert.True(d.ColumnItems[0].Checked);
        Assert.Equal(all.Count, d.ColumnItems.Count);
        Assert.All(d.ColumnItems.Skip(1), c => Assert.False(c.Checked));
        var stores = all.First(p => p.Id == "stores");
        Assert.Contains(d.ColumnItems, c => c.Text == stores.Name + LedgerViewRules.HolderColumnSuffix);
        Assert.False(d.UpButton.IsEnabled);
        Assert.False(d.DownButton.IsEnabled);

        // Add: a fresh "New tab" after the selected one, selected, its name ready to type over.
        Click(d.AddButton);
        Assert.Equal(new[] { "★ Supply", "★ " + LedgerViewRules.NewTabName(start) }, d.ListItems);
        Assert.Equal(1, d.SelectedIndex);
        Assert.Equal("New tab", d.NameBox.Text);
        Assert.True(d.UpButton.IsEnabled);
        Assert.False(d.DownButton.IsEnabled);

        d.NameBox.Text = "Low fuel";
        Assert.Equal("★ Low fuel", d.ListItems[1]);
        d.FilterBox.Text = "Fuel <";
        Pump();
        Assert.True(d.FilterErrorShown);
        Assert.StartsWith("Does not parse: ", d.FilterErrorText);
        Assert.False(d.OkButton.IsEnabled);
        d.FilterBox.Text = "Fuel < 5";
        Pump();
        Assert.False(d.FilterErrorShown);
        Assert.True(d.OkButton.IsEnabled);
        var ammo = d.ColumnItems.ToList().FindIndex(c => c.Text == all.First(p => p.Id == "ammo").Name);
        var fuelAt = d.ColumnItems.ToList().FindIndex(c => c.Text == fuel.Name);
        d.SetColumnChecked(fuelAt, true);
        d.SetColumnChecked(ammo, true);
        d.ColumnList.SelectedIndex = d.ColumnItems.ToList().FindIndex(c => c.Text == all.First(p => p.Id == "ammo").Name);
        Click(d.ColUpButton);   // ammo before fuel when it sits below it
        d.HoldersBox.IsChecked = true;
        d.NotesBox.Text = "Running dry";
        Pump();
        var cols = d.ColumnItems.Where(c => c.Checked).Select(c => all.First(p => p.Name == c.Text).Id).ToList();
        Assert.Equal(2, cols.Count);

        // Move up, then OK: the edited list in the new order.
        Click(d.UpButton);
        Assert.Equal(new[] { "★ Low fuel", "★ Supply" }, d.ListItems);
        Click(d.OkButton);
        var result = d.Answer!;
        Assert.Equal(2, result.Count);
        Same(new UserLedgerTab("Low fuel", "Fuel < 5", cols, true, "Running dry"), result[0]);
        Same(start[0], result[1]);

        // Remove empties the editor once nothing is left.
        var d2 = Dialog(start, pack: pack);
        Click(d2.RemoveButton);
        Assert.Empty(d2.ListItems);
        Assert.False(d2.RemoveButton.IsEnabled);
        Assert.False(d2.NameBox.IsEffectivelyEnabled);
        Click(d2.OkButton);
        Assert.Empty(d2.Answer!);

        // Through the Ledger: Edit tabs… opens the editor at the current tab and the answer becomes the strip and the stored prefs.
        var w = Game();
        var lw = Open(w);
        Assert.True(lw.EditTabsButton.IsEnabled);
        Assert.True(lw.PlusTabItem.IsEnabled);
        _dialogs.TabEditAnswer = (_, _) => result;
        Click(lw.EditTabsButton);
        Pump();
        var asked = Assert.Single(_dialogs.TabEdits);
        Assert.Empty(asked.Tabs);
        Assert.False(asked.AddNew);
        Assert.Equal(new[] { "★ Low fuel", "★ Supply", "+" }, lw.TabTitles.Skip(3));
        Assert.Equal("units", lw.CurrentTabKey);   // Edit tabs… keeps the tab shown
        var stored = LedgerTabStore.Load(AppDataRoot.Path, w.Session!.Archive.Sha256);
        Assert.Equal(new[] { "Low fuel", "Supply" }, stored.Tabs.Select(t => t.Name));
        Same(result[0], stored.Tabs[0]);

        // Opened on a user tab: the editor selects that tab. Cancel changes nothing.
        lw.SelectTab("user:Supply");
        _dialogs.TabEditAnswer = (_, _) => null;
        Click(lw.EditTabsButton);
        Pump();
        Assert.Equal(1, _dialogs.TabEdits[1].Select);
        Assert.Equal(new[] { "★ Low fuel", "★ Supply", "+" }, lw.TabTitles.Skip(3));
        Assert.Equal("user:Supply", lw.CurrentTabKey);
    }

    [AvaloniaFact]
    public void Edit_tabs_ok_refused_with_problem()
    {
        var pack = Pack();
        var d = Dialog(new[] { new UserLedgerTab("Supply", null, Array.Empty<string>(), false, null) }, pack: pack);
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("", d.ProblemsText);

        Click(d.AddButton);
        d.NameBox.Text = " supply ";
        Pump();
        var expected = LedgerTabsEdit.Problems(d.Tabs, pack);
        Assert.NotEmpty(expected);
        Assert.Equal(string.Join(Environment.NewLine, expected), d.ProblemsText);
        Assert.Contains("More than one tab is named", d.ProblemsText);
        Assert.False(d.OkButton.IsEnabled);
        Click(d.OkButton);   // refused (also when clicked anyway)
        Assert.Null(d.Answer);
        Assert.True(d.IsVisible);

        d.NameBox.Text = "";
        Pump();
        Assert.Equal(LedgerViewRules.NoNameText, d.ListItems[1]);
        Assert.Equal(string.Join(Environment.NewLine, LedgerTabsEdit.Problems(d.Tabs, pack)), d.ProblemsText);
        Assert.False(d.OkButton.IsEnabled);

        // A filter that names nothing in this rules pack is a problem too (shown under the filter box).
        d.NameBox.Text = "Other";
        d.FilterBox.Text = "Nonsense > 1";
        Pump();
        var problems = LedgerTabsEdit.Problems(d.Tabs, pack);
        Assert.NotEmpty(problems);
        Assert.True(d.FilterErrorShown);
        Assert.Equal(LedgerTabs.FilterError("Nonsense > 1", pack), d.FilterErrorText);
        Assert.False(d.OkButton.IsEnabled);

        d.FilterBox.Text = "";
        Pump();
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("", d.ProblemsText);
        Click(d.OkButton);
        Assert.Equal(new[] { "Supply", "Other" }, d.Answer!.Select(t => t.Name));
        Assert.Null(d.Answer![1].Filter);

        // Cancel answers nothing.
        var d2 = Dialog(Array.Empty<UserLedgerTab>(), pack: pack);
        Click(d2.CancelButton);
        Assert.Null(d2.Answer);
    }

    [AvaloniaFact]
    public void Plus_adds_new_tab()
    {
        // The dialog opened by "+": a fresh tab already added and selected.
        var d = Dialog(new[] { new UserLedgerTab("Supply", null, Array.Empty<string>(), false, null) }, addNew: true);
        Assert.Equal(new[] { "★ Supply", "★ New tab" }, d.ListItems);
        Assert.Equal(1, d.SelectedIndex);
        Assert.Equal("New tab", d.NameBox.Text);

        var w = Game();
        var lw = Open(w);
        _dialogs.TabEditAnswer = (tabs, addNew) =>
        {
            Assert.True(addNew);
            return tabs.Append(new UserLedgerTab("Mine", "Fuel < 5", Array.Empty<string>(), false, "my notes")).ToList();
        };
        lw.PlusTabItem.IsSelected = true;   // a click on "+"
        PumpUntil(() => _dialogs.TabEdits.Count == 1 && lw.CurrentTabKey == "user:Mine");
        Assert.True(_dialogs.TabEdits[0].AddNew);
        Assert.Equal(new[] { "★ Mine", "+" }, lw.TabTitles.Skip(3));
        Assert.Equal("my notes", lw.TabTip(3));
        Assert.False(lw.PlusTabItem.IsSelected);   // "+" is a button, never the shown tab

        // Cancel from "+": the shown tab stays.
        _dialogs.TabEditAnswer = (_, _) => null;
        lw.PlusTabItem.IsSelected = true;
        PumpUntil(() => _dialogs.TabEdits.Count == 2);
        Pump();
        Assert.Equal("user:Mine", lw.CurrentTabKey);
        Assert.False(lw.PlusTabItem.IsSelected);
    }

    // ---------- Show in Ledger ----------

    [AvaloniaFact]
    public void Ctrl_L_with_selection_shows_rows()
    {
        var w = Game();
        var lw = Open(w);
        lw.SelectTab("holders");   // the jump moves to the tab that shows the counter
        var id = UnitWithRow(w);
        w.Maps.ActiveView!.SelectOnly(id);
        w.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
        Pump();
        Assert.Same(lw, w.Ledger);
        Assert.Equal("units", lw.CurrentTabKey);
        Assert.Equal(new[] { LedgerKey.Piece(id) }, lw.SelectedRowKeys);
        Assert.Equal("Shown in the Ledger.", w.StatusText);

        // View > Ledger does the same; with nothing selected it only opens (rows untouched).
        lw.SelectTab("holders");
        w.ClickMenu("&Ledger");
        Pump();
        Assert.Equal("units", lw.CurrentTabKey);
        lw.SelectTab("holders");
        w.Maps.ActiveView.SelectOnly(null);
        w.SetStatus("");
        w.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
        Pump();
        Assert.Equal("holders", lw.CurrentTabKey);
        Assert.Equal("", w.StatusText);

        // A closed Ledger opens for the jump.
        lw.Close();
        Pump();
        Assert.Null(w.Ledger);
        w.Maps.ActiveView.SelectOnly(id);
        w.KeyPress(Key.L, RawInputModifiers.Control, PhysicalKey.L, null);
        Pump();
        var again = Assert.IsType<LedgerWindow>(w.Ledger);
        Assert.Equal(new[] { LedgerKey.Piece(id) }, again.SelectedRowKeys);
    }

    [AvaloniaFact]
    public void Show_in_ledger_menu_entry()
    {
        var w = Game(rules: false);
        var view = w.Maps.ActiveView!;
        var any = w.Session!.View!.Pieces.Values.First(p => p.MapId == view.Map.Id).Id;
        view.OpenContextMenu(any);
        var item = view.OpenMenu!.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == PieceMenuModel.ShowInLedgerLabel);
        Assert.False(item.IsEnabled);
        Assert.Equal(PieceMenuModel.LedgerNeedsRulesText, ToolTip.GetTip(item));
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Control), item.InputGesture);
        view.OpenMenu.Close();

        w.Session.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        var id = UnitWithRow(w);
        view.OpenContextMenu(id);
        item = view.OpenMenu!.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == PieceMenuModel.ShowInLedgerLabel);
        Assert.True(item.IsEnabled);
        Assert.IsType<Separator>(view.OpenMenu.Items[view.OpenMenu.Items.IndexOf(item) - 1]);
        ClickMenu(item);
        var lw = Assert.IsType<LedgerWindow>(w.Ledger);
        Assert.Contains(LedgerKey.Piece(id), lw.SelectedRowKeys);
        Assert.Equal(lw.LastJump!.RowKeys.Count == 1 ? "Shown in the Ledger." : $"{lw.LastJump.RowKeys.Count} counters shown in the Ledger.", w.StatusText);
    }

    [AvaloniaFact]
    public void Show_in_ledger_for_hidden_counter_says_no_row()
    {
        var w = Game();
        var s = w.Session!;
        // The first map turns the other side's: its units are hidden from this northern viewer.
        s.SetViewSides(new HashSet<string> { "north" });
        s.UpdateRules(r => r with { MapOwners = new List<MapOwner> { new() { MapId = s.Model.Maps[0].Id, SideId = "south" } } });
        Pump();
        var hidden = s.Game!.State.Pieces.Keys.FirstOrDefault(id => !s.CanSee(id) && !s.View!.Pieces.ContainsKey(id));
        Assert.NotNull(hidden);
        w.ShowInLedger(new[] { hidden! });
        Pump();
        var lw = Assert.IsType<LedgerWindow>(w.Ledger);
        Assert.Empty(lw.SelectedRowKeys);
        Assert.Equal("That counter has no row in the Ledger.", w.StatusText);
        Assert.Equal(1, lw.LastJump!.Missing);
    }

    [AvaloniaFact]
    public void Show_in_ledger_status_texts()
    {
        var w = Game();
        var s = w.Session!;
        var units = LedgerTabs.Rows(LedgerTabs.Tabs(Array.Empty<UserLedgerTab>())[0], s.View!, s.Rules!, s.Model, "", null, LedgerGrouping.None, false)
            .Rows.Select(r => LedgerKey.TryParse(r.Row.Key, out var isPiece, out var id) && isPiece ? id : null).OfType<string>().Take(2).ToList();
        Assert.Equal(2, units.Count);
        w.ShowInLedger(new[] { units[0] });
        Pump();
        Assert.Equal("Shown in the Ledger.", w.StatusText);
        w.ShowInLedger(units);
        Pump();
        Assert.Equal("2 counters shown in the Ledger.", w.StatusText);
        Assert.Equal(units.Select(LedgerKey.Piece).ToHashSet(), w.Ledger!.SelectedRowKeys.ToHashSet());
    }

    // ---------- alerts ----------

    [AvaloniaFact]
    public void Holder_alert_match_opens_ledger_and_selects_row()
    {
        var w = Game();
        var st = w.Session!.Game!.State;
        var key = LedgerKey.Holder("h-north-depot");
        w.Session.Game.Execute(new LedgerDelta(key, "stores", Ledger.Get(st, key, "stores") ?? 0, -5, "manual", null, null, "test", null));
        Pump();
        w.FlushAlerts();
        Pump();
        var alertId = AlertEngine.NegativeId("stores");
        Assert.Contains(w.AlertHits, h => h.Id == alertId);
        // A counter selected on the map must not take over: the depot's row is what the alert shows.
        w.Maps.ActiveView!.SelectOnly(UnitWithRow(w));
        Assert.Null(w.Ledger);
        w.Alerts.ClickChip(alertId);
        Pump();
        var lw = Assert.IsType<LedgerWindow>(w.Ledger);
        lw.Flush();
        Pump();
        Assert.Equal("holders", lw.CurrentTabKey);
        Assert.Equal(new[] { key }, lw.SelectedRowKeys);
    }

    // ---------- persistence ----------

    [AvaloniaFact]
    public void Tabs_persist_and_other_windows_follow_changed()
    {
        var w1 = Game();
        var w2 = Game();
        var a = Open(w1);
        var b = Open(w2);
        var changes = new List<string>();
        void Seen(string sha) => changes.Add(sha);
        LedgerTabStore.Changed += Seen;
        try
        {
            _dialogs.TabEditAnswer = (_, _) => new[] { new UserLedgerTab("Shared", null, Array.Empty<string>(), false, null) };
            Click(a.EditTabsButton);
            Assert.Contains(w1.Session!.Archive.Sha256, changes);
        }
        finally { LedgerTabStore.Changed -= Seen; }
        PumpUntil(() => b.TabTitles.Contains("★ Shared"));
        Assert.Equal("units", b.CurrentTabKey);   // the other window keeps its own tab

        // Stored on this computer: a Ledger opened afresh has the tab.
        a.Close();
        Pump();
        var c = Open(w1);
        Assert.Contains("★ Shared", c.TabTitles);
    }
}
