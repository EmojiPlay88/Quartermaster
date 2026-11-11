using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 6: the workbench Tables tab. Headless, fake dialogs, invented tables.</summary>
[Collection("MacFlag")]
public sealed class TablesTabTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tablestab-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    static TableDef Cost() => new()
    {
        Id = "t-cost", Name = "Cost", Kind = TableKind.OneD, RowKeys = new() { "Clear", "Sand" }, ColumnKeys = new() { "Value" },
        Cells = new() { new() { "2" }, new() { "3" } },
    };

    static TableDef Terrain() => new()
    {
        Id = "t-terrain", Name = "Terrain", Kind = TableKind.TwoD, RowKeys = new() { "1-3", "4" }, ColumnKeys = new() { "Wet", "Dry", "" },
        Cells = new() { new() { "a", "b", "c" }, new() { "d", "e", "f" } },
    };

    ModuleSession Session(bool withPack = true, params TableDef[] tables)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        if (withPack)
        {
            s.NewRules("Invented");
            s.UpdateRules(p => p with { Tables = tables.ToList() });
        }
        return s;
    }

    TablesTab Tab(ModuleSession s, Func<string, RuleRef?>? link = null, Action<RuleRef>? show = null)
    {
        var tab = new TablesTab(s, _dialogs, link ?? (_ => null), show);
        var w = new Window { Width = 900, Height = 500, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static TableDef T(ModuleSession s, string id) => s.Rules!.Tables.Single(t => t.Id == id);

    static RuleRef Ref(ModuleSession s, string id = "r1", string entry = "rules.pdf", int page = 4)
    {
        var r = new RuleRef { Id = id, Label = "Combat", DocumentEntry = entry, Page = page };
        s.UpdateRules(p => p with { RuleRefs = p.RuleRefs.Append(r).ToList() });
        return r;
    }

    [AvaloniaFact]
    public void Lists_tables_by_name_and_selects()
    {
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        Assert.Equal(["Cost", "Terrain"], ((IEnumerable<string>)tab.List.ItemsSource!).ToList());
        Assert.Equal("t-cost", tab.SelectedTableId);
        Assert.Equal(2, tab.Grid.RowCount);
        tab.SelectTable("t-terrain");
        Pump();
        Assert.Equal("t-terrain", tab.SelectedTableId);
        Assert.Equal("a", tab.Grid.Cell(0, 1));
        Assert.Equal(1, tab.KindBox.SelectedIndex);   // TwoD
        tab.SelectTable("nope");
        Assert.Equal("t-terrain", tab.SelectedTableId);
    }

    [AvaloniaFact]
    public void New_rename_delete_with_name_rules()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        _dialogs.Texts.Enqueue("Weather");
        Click(tab.NewButton);
        Assert.Equal("Table 2", _dialogs.TextPrompts[0].Split('|')[2]);
        Assert.Equal(2, s.Rules!.Tables.Count);
        Assert.Equal("Weather", s.Rules.Tables[1].Name);
        Assert.Equal(s.Rules.Tables[1].Id, tab.SelectedTableId);

        _dialogs.Texts.Enqueue("cost");   // duplicate, case-insensitive
        Click(tab.NewButton);
        Assert.Equal(2, s.Rules.Tables.Count);
        Assert.Single(_dialogs.Warnings);
        Assert.Equal("New table", _dialogs.Warnings[0].Caption);

        _dialogs.Texts.Enqueue("Storm");
        Click(tab.RenameButton);
        Assert.Equal("Storm", s.Rules.Tables[1].Name);
        Assert.Equal("Weather", _dialogs.TextPrompts[^1].Split('|')[2]);
        _dialogs.Texts.Enqueue("COST");
        Click(tab.RenameButton);
        Assert.Equal("Storm", s.Rules.Tables[1].Name);
        Assert.Equal("Rename table", _dialogs.Warnings[^1].Caption);

        _dialogs.Answer = false;
        Click(tab.DeleteButton);
        Assert.Equal(2, s.Rules.Tables.Count);
        _dialogs.Answer = true;
        Click(tab.DeleteButton);
        Assert.Single(s.Rules.Tables);
        Assert.Equal("t-cost", tab.SelectedTableId);
        Assert.All(_dialogs.YesNoDefaultNo, d => Assert.True(d));
        Assert.Equal("Delete the table \"Storm\"? Formulas that use it will report an error.", _dialogs.Questions[0].Message);
    }

    [AvaloniaFact]
    public void Kind_change_commits()
    {
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        tab.KindBox.SelectedIndex = 2;
        Pump();
        Assert.Equal(TableKind.Dice, T(s, "t-cost").Kind);

        tab.SelectTable("t-terrain");
        Pump();
        _dialogs.Answer = false;   // data loss refused
        tab.KindBox.SelectedIndex = 0;
        Pump();
        Assert.Equal(TableKind.TwoD, T(s, "t-terrain").Kind);
        Assert.Equal(1, tab.KindBox.SelectedIndex);
        Assert.True(_dialogs.YesNoDefaultNo[^1]);
        Assert.Equal("Change kind", _dialogs.Questions[^1].Caption);

        _dialogs.Answer = true;
        tab.KindBox.SelectedIndex = 0;
        Pump();
        Assert.Equal(TableKind.OneD, T(s, "t-terrain").Kind);
        Assert.Single(T(s, "t-terrain").ColumnKeys);
    }

    [AvaloniaFact]
    public void Add_remove_rows_and_columns()
    {
        var s = Session(true, Terrain());
        var tab = Tab(s);
        Click(tab.AddRowButton);
        Assert.Equal(3, T(s, "t-terrain").RowKeys.Count);
        Assert.Equal(3, tab.Grid.RowCount);
        tab.Grid.SelectCell(0, 1);
        Click(tab.RemoveRowButton);
        Assert.Equal(["4", ""], T(s, "t-terrain").RowKeys);
        Click(tab.AddColumnButton);
        Assert.Equal(4, T(s, "t-terrain").ColumnKeys.Count);
        tab.Grid.SelectCell(0, 1);   // column "Wet"
        Click(tab.RemoveColumnButton);
        Assert.Equal(["Dry", "", ""], T(s, "t-terrain").ColumnKeys);
        tab.Grid.SelectCell(0, 0);   // the row key column has no column to remove
        Click(tab.RemoveColumnButton);
        Assert.Equal(3, T(s, "t-terrain").ColumnKeys.Count);
    }

    [AvaloniaFact]
    public void Add_column_buttons_are_disabled_for_one_dimensional_tables()
    {
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        Assert.False(tab.AddColumnButton.IsEnabled);
        Assert.False(tab.ColumnsButton.IsEnabled);
        Assert.True(tab.AddRowButton.IsEnabled);
        tab.SelectTable("t-terrain");
        Pump();
        Assert.True(tab.AddColumnButton.IsEnabled);
        Assert.True(tab.ColumnsButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Cell_edit_commits_to_pack()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        tab.Grid.BeginEdit(0, 1, "9");
        Assert.True(tab.Grid.CommitEdit());
        Assert.Equal("9", T(s, "t-cost").Cells[0][0]);
        tab.Grid.BeginEdit(1, 0, "Rock");
        tab.Grid.CommitEdit();
        Assert.Equal(["Clear", "Rock"], T(s, "t-cost").RowKeys);
        Assert.Equal("text \"Rock\"", tab.Grid.CellToolTip(1, 0));
        // an empty row key is pink
        tab.Grid.BeginEdit(1, 0, "");
        tab.Grid.CommitEdit();
        Assert.Same(EditGrid.ErrorBrush, tab.Grid.CellBackground(1, 0));
        Assert.Equal("(empty key)", tab.Grid.CellToolTip(1, 0));
    }

    [AvaloniaFact]
    public void Two_dimensional_headers_show_key_hints_and_pink_empty_key()
    {
        var s = Session(true, Terrain());
        var tab = Tab(s);
        Assert.Equal("text \"Wet\"", tab.Grid.HeaderToolTip(1));
        Assert.Equal("(empty key)", tab.Grid.HeaderToolTip(3));
        Assert.Same(EditGrid.ErrorBrush, tab.Grid.HeaderBackground(3));
        Assert.NotSame(EditGrid.ErrorBrush, tab.Grid.HeaderBackground(1));
        Assert.Equal("1 to 3", tab.Grid.CellToolTip(0, 0));
    }

    [AvaloniaFact]
    public void Columns_dialog_sets_keys()
    {
        var s = Session(true, Terrain());
        var tab = Tab(s);
        _dialogs.ColumnsAnswer = ["X", "Y"];
        Click(tab.ColumnsButton);
        Assert.Equal([["Wet", "Dry", ""]], _dialogs.ColumnPrompts.Select(k => k.ToList()).ToList());
        Assert.Equal(["X", "Y"], T(s, "t-terrain").ColumnKeys);
        Assert.Equal(2, tab.Grid.RowCount);
        _dialogs.ColumnsAnswer = null;   // cancel
        Click(tab.ColumnsButton);
        Assert.Equal(["X", "Y"], T(s, "t-terrain").ColumnKeys);
    }

    [AvaloniaFact]
    public void Paste_from_clipboard_applies_grid()
    {
        var s = Session(true, Terrain());
        var tab = Tab(s);
        _dialogs.Clipboard = "k\tA\tB\nr1\t1\t2\nr2\t3\t4";
        _dialogs.PasteAnswer = TablePaste.Parse(_dialogs.Clipboard, true, true);
        Click(tab.PasteButton);
        Assert.Equal(_dialogs.Clipboard, _dialogs.PastePrompts[0].Text);
        Assert.False(_dialogs.PastePrompts[0].TsvOnly);
        var t = T(s, "t-terrain");
        Assert.Equal(["r1", "r2"], t.RowKeys);
        Assert.Equal(["A", "B"], t.ColumnKeys);
        Assert.Equal("4", t.Cells[1][1]);
        _dialogs.Clipboard = null;
        _dialogs.PasteAnswer = null;
        Click(tab.PasteButton);
        Assert.Equal("", _dialogs.PastePrompts[1].Text);
        Assert.Equal(["r1", "r2"], T(s, "t-terrain").RowKeys);
    }

    [AvaloniaFact]
    public void Notes_commit_on_leave()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        tab.Notes.Focus();
        tab.Notes.Text = "Movement costs";
        Assert.Null(T(s, "t-cost").Notes);
        tab.Grid.Focus();   // leaving the box
        Pump();
        tab.Notes.Focus();
        Assert.Equal("Movement costs", T(s, "t-cost").Notes);
        tab.Notes.Text = "";
        tab.Grid.Focus();   // leaving the box
        Pump();
        tab.Notes.Focus();
        Assert.Null(T(s, "t-cost").Notes);
    }

    [AvaloniaFact]
    public void Link_to_current_page_sets_rule_ref()
    {
        var s = Session(true, Cost());
        var r = Ref(s);
        string? asked = null;
        var tab = Tab(s, name => { asked = name; return r; });
        Assert.Equal("(none)", tab.RefRow.Text);
        Click(tab.RefRow.LinkButton);
        Assert.Equal("Cost", asked);
        Assert.Equal("r1", T(s, "t-cost").RuleRefId);
        Assert.Equal("Combat, page 4", tab.RefRow.Text);
        // UseRuleRef (the workbench's "Use as rule ref")
        var r2 = Ref(s, "r2", "other.pdf", 9);
        Assert.True(tab.CanUseRuleRef);
        Assert.True(tab.UseRuleRef(_ => r2));
        Assert.Equal("r2", T(s, "t-cost").RuleRefId);
        Assert.False(tab.UseRuleRef(_ => null));
    }

    [AvaloniaFact]
    public void Rule_ref_click_calls_show_ref()
    {
        var s = Session(true, Cost() with { RuleRefId = "r1" });
        var r = Ref(s);
        RuleRef? shown = null;
        var tab = Tab(s, show: x => shown = x);
        tab.RefreshRuleRef();
        tab.RefRow.ClickLink();
        Assert.Equal("r1", shown?.Id);
    }

    static PastedGrid Pasted(string tsv) => TablePaste.Parse(tsv, true, true, tsvOnly: true);

    [AvaloniaFact]
    public async Task Import_into_new_table_rechecks_the_name_after_the_paste_dialog()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        var tsv = "k\tA\tB\nr1\t1\t2";
        _dialogs.ImportTargetAnswer = false;
        _dialogs.Texts.Enqueue("Imported");
        _dialogs.PasteAnswer = Pasted(tsv);
        _dialogs.OnPaste = () => s.UpdateRules(p => p with { Tables = p.Tables.Append(TableEdit.New("imported", TableKind.OneD)).ToList() });   // appears while the dialog is open
        var count = s.Rules!.Tables.Count;
        var id = await tab.ImportGridAsync(tsv, "scan.png", null);
        Pump();
        Assert.Null(id);
        Assert.Equal(count + 1, s.Rules.Tables.Count);
        Assert.Equal(1, s.Rules.Tables.Count(t => t.Name.Equals("imported", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("There is already an item named \"Imported\".", _dialogs.Infos[^1].Message);
    }

    [AvaloniaFact]
    public async Task Import_into_new_table_with_scan_rule_ref()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        tab.MakeRuleRef = (label, entry, page) =>
        {
            var (pack, r) = WorkbenchRules.MakeOrReuseRef(s.Rules!, label, entry, page);
            s.UpdateRules(_ => pack);
            return r;
        };
        var tsv = "k\tA\tB\nr1\t1\t2\nr2\t3\t4";
        _dialogs.ImportTargetAnswer = false;
        _dialogs.Texts.Enqueue("Imported");
        _dialogs.PasteAnswer = Pasted(tsv);
        var id = await tab.ImportGridAsync(tsv, "scan.png", null);
        Pump();
        Assert.NotNull(id);
        Assert.Equal(id, tab.SelectedTableId);
        var t = T(s, id!);
        Assert.Equal("Imported", t.Name);
        Assert.Equal(TableKind.TwoD, t.Kind);
        Assert.Equal(["r1", "r2"], t.RowKeys);
        var r = s.Rules!.RuleRefs.Single(x => x.Id == t.RuleRefId);
        Assert.Equal(("scan.png", 0), (r.DocumentEntry, r.Page));
        Assert.Equal(["Cost"], _dialogs.ImportTargetPrompts);
        Assert.Equal("Scanned table", _dialogs.TextPrompts[^1].Split('|')[2]);
        var (_, title, target, tsvOnly) = _dialogs.PastePrompts[0];
        Assert.Equal("Import into table", title);
        Assert.Equal("OK creates the new table \"Imported\".", target);
        Assert.True(tsvOnly);
        Assert.Single(s.Rules.Tables, x => x.Id == "t-cost");
    }

    [AvaloniaFact]
    public async Task Import_into_existing_keeps_its_rule_ref()
    {
        var s = Session(true, Cost() with { RuleRefId = "r1" }, Terrain());
        Ref(s);
        var tab = Tab(s);
        var made = 0;
        tab.MakeRuleRef = (_, _, _) => { made++; return null; };
        var tsv = "k\tV\nn1\t7\nn2\t8";
        _dialogs.ImportTargetAnswer = true;
        _dialogs.PasteAnswer = Pasted(tsv);
        var id = await tab.ImportGridAsync(tsv, "scan.png", null);
        Assert.Equal("t-cost", id);
        var t = T(s, "t-cost");
        Assert.Equal(["n1", "n2"], t.RowKeys);
        Assert.Equal("8", t.Cells[1][0]);
        Assert.Equal("r1", t.RuleRefId);
        Assert.Equal(0, made);
        Assert.Equal("OK replaces the rows, columns and cells of the table \"Cost\".", _dialogs.PastePrompts[0].Target);

        // an existing table without a rule ref gets one for the scan
        tab.SelectTable("t-terrain");
        Pump();
        RuleRef? given = null;
        tab.MakeRuleRef = (label, entry, page) => { given = new RuleRef { Id = "r9", Label = label, DocumentEntry = entry, Page = page }; s.UpdateRules(p => p with { RuleRefs = p.RuleRefs.Append(given).ToList() }); return given; };
        await tab.ImportGridAsync(tsv, "scan.pdf", 3);
        Assert.Equal("r9", T(s, "t-terrain").RuleRefId);
        Assert.Equal(("Terrain", "scan.pdf", 3), (given!.Label, given.DocumentEntry, given.Page));
        // cancelling the target prompt imports nothing
        _dialogs.ImportTargetAnswer = null;
        Assert.Null(await tab.ImportGridAsync(tsv, "scan.pdf", 3));
    }

    [AvaloniaFact]
    public async Task Import_refused_without_pack_or_as_guest_or_replaying()
    {
        var none = Session(false);
        var tab = Tab(none);
        Assert.Null(await tab.ImportGridAsync("a\tb", null, null));
        Assert.Equal(("Create or open a rules pack first; the table is stored in it.", "Import into table"), _dialogs.Infos[^1]);

        var replaying = Session(true, Cost());
        var tab2 = Tab(replaying);
        typeof(ModuleSession).GetProperty("IsReplaying")!.SetValue(replaying, true);
        Assert.Null(await tab2.ImportGridAsync("a\tb", null, null));
        Assert.Equal(ModuleSession.ReplayingText, _dialogs.Infos[^1].Message);

        var guest = Session(true, Cost());
        var tab3 = Tab(guest);
        guest.EnterGuestMode();
        Assert.Null(await tab3.ImportGridAsync("a\tb", null, null));
        Assert.Equal("The host's rules pack is read-only here.", _dialogs.Infos[^1].Message);

        Assert.Equal(3, _dialogs.Infos.Count);
        Assert.Empty(_dialogs.PastePrompts);
        Assert.Empty(_dialogs.ImportTargetPrompts);
    }

    [AvaloniaFact]
    public void External_change_while_editing_drops_stale_edit()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        tab.Grid.BeginEdit(0, 1, "zzz");
        Assert.True(tab.Grid.IsEditing);
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t with { Cells = new() { new() { "7" }, new() { "3" } } }).ToList() });
        Pump();
        Assert.False(tab.Grid.IsEditing);
        Assert.Equal("7", tab.Grid.Cell(0, 1));
        Assert.Equal("7", T(s, "t-cost").Cells[0][0]);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        tab.Grid.BeginEdit(0, 1, "11");
        tab.Grid.CommitEdit();
        Click(tab.AddRowButton);
        tab.Notes.Text = "Notes here";
        tab.CommitPending();
        tab.SelectTable("t-terrain");
        Pump();
        _dialogs.ColumnsAnswer = ["Wet", "Dry"];
        Click(tab.ColumnsButton);
        var path = Path.Combine(_tmp, "t.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        Assert.Equal(s.Rules!.Tables.Count, back.Tables.Count);
        for (var i = 0; i < back.Tables.Count; i++)
        {
            var a = s.Rules.Tables[i];
            var b = back.Tables[i];
            Assert.Equal((a.Id, a.Name, a.Kind, a.Notes, a.RuleRefId), (b.Id, b.Name, b.Kind, b.Notes, b.RuleRefId));
            Assert.Equal(a.RowKeys, b.RowKeys);
            Assert.Equal(a.ColumnKeys, b.ColumnKeys);
            Assert.Equal(a.Cells.Select(r => string.Join("|", r)), b.Cells.Select(r => string.Join("|", r)));
        }
        Assert.Equal("11", back.Tables[0].Cells[0][0]);
        Assert.Equal("Notes here", back.Tables[0].Notes);
        Assert.Equal(["Wet", "Dry"], back.Tables[1].ColumnKeys);
    }

    [AvaloniaFact]
    public void Detaching_the_tab_flushes_typed_notes_and_open_edit()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        var w = (Window)tab.Parent!;
        tab.Notes.Text = "typed quickly";
        tab.Grid.BeginEdit(0, 1, "42");
        w.Content = null;   // a hidden tab is detached
        Pump();
        Assert.Equal("typed quickly", T(s, "t-cost").Notes);
        Assert.Equal("42", T(s, "t-cost").Cells[0][0]);
        w.Content = tab;
        Pump();
        Assert.Equal("typed quickly", tab.Notes.Text);
    }

    [AvaloniaFact]
    public void Edited_table_deleted_externally_drops_edit_and_touches_nothing_else()
    {
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        tab.Grid.BeginEdit(0, 1, "zzz");
        s.UpdateRules(p => p with { Tables = p.Tables.Where(t => t.Id != "t-cost").ToList() });
        Pump();
        Assert.False(tab.Grid.IsEditing);
        Assert.Equal("t-terrain", tab.SelectedTableId);
        Assert.Equal("a", T(s, "t-terrain").Cells[0][0]);
        Assert.Equal(Terrain().Cells[0], T(s, "t-terrain").Cells[0]);
    }

    [AvaloniaFact]
    public void Tab_without_a_pack_is_empty_and_disabled()
    {
        var tab = Tab(Session(false));
        Assert.Null(tab.SelectedTableId);
        Assert.Equal(0, tab.Grid.RowCount);
        Assert.False(tab.AddRowButton.IsEnabled);
        Assert.False(tab.KindBox.IsEnabled);
    }

    [AvaloniaFact]
    public void Rename_dialog_answer_is_dropped_when_the_table_changed_meanwhile()
    {
        var s = Session(true, Cost(), Terrain());
        var tab = Tab(s);
        var gate = new TaskCompletionSource<string?>();
        _dialogs.TextGate = gate;
        Click(tab.RenameButton);   // waiting on "Cost"
        s.UpdateRules(p => p with { Tables = p.Tables.Where(t => t.Id != "t-cost").ToList() });
        Pump();
        gate.SetResult("Zed");
        Pump();
        Assert.Equal(["Terrain"], s.Rules!.Tables.Select(t => t.Name).ToList());
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var s = Session(true, Cost() with { Notes = "a\r\nb" }, Terrain());
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.SelectTable("t-terrain");
        Pump();
        tab.SelectTable("t-cost");
        Pump();
        tab.Notes.Focus();
        tab.Grid.Focus();   // leaving the notes box with CRLF notes and no edit
        Pump();
        tab.CommitPending();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session(true, Cost());
        var tab = Tab(s);
        s.EnterGuestMode();
        tab.Grid.BeginEdit(0, 1, "99");
        tab.Grid.CommitEdit();
        Pump();
        Assert.Equal("2", tab.Grid.Cell(0, 1));
        Assert.Equal("2", T(s, "t-cost").Cells[0][0]);
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Table edit failed"));
    }
}
