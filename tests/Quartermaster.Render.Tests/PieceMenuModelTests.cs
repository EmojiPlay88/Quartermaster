using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>The counter right-click menu model, item for item as the WinForms map window built it. Invented PhaseFixture module only.</summary>
[Collection(AppDataRootEnv.Name)]
public class PieceMenuModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-menu-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PieceMenuModelTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Open(bool rules)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto, null);
        if (rules)
        {
            s.NewRules("t");
            s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        }
        return s;
    }

    private static string MapOf(ModuleSession s, string pieceId) => s.Game!.State.Pieces[pieceId].MapId;

    /// <summary>Labels as a WinForms menu shows them; "-" for a separator, children in brackets.</summary>
    private static string Shape(IReadOnlyList<MenuEntry> entries) =>
        string.Join(" | ", entries.Select(e => e.IsSeparator ? "-" : e.Children.Count > 0 ? e.Label + " [" + Shape(e.Children) + "]" : e.Label));

    [Fact]
    public void Menu_for_movable_piece_lists_actions_in_winforms_order()
    {
        var s = Open(rules: true);
        var shown = new List<IReadOnlyList<string>>();
        var hooks = new PieceMenuHooks { GameMaster = true, ShowInLedger = ids => shown.Add(ids) };
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null, hooks);

        var formulas = PieceFilter.FormulasFor(s.Rules!, s.Game!.State.Pieces[PhaseFixture.RedOne], s.Game.State, s.Model).Select(f => f.Name);
        Assert.Equal("Sheet | - | Calculate [" + string.Join(" | ", formulas) + "] | - | Delete counter | - | Show in &Ledger", Shape(menu));
        var ledger = menu[^1];
        Assert.Equal("view.ledger", ledger.CommandId);
        Assert.True(ledger.Enabled);
        Assert.Null(ledger.DisabledReason);
        Assert.All(menu.Where(e => !e.IsSeparator && e.Children.Count == 0), e => Assert.NotNull(e.Run));

        // Show in Ledger acts on the clicked counter, or the selection when the clicked counter is part of it.
        ledger.Run!();
        var selected = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne, PhaseFixture.BlueOne, PhaseFixture.RedOne }, null, hooks);
        selected[^1].Run!();
        var unselected = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne, PhaseFixture.BlueOne }, null, hooks);
        unselected[^1].Run!();
        Assert.Equal(new[] { PhaseFixture.RedOne }, shown[0]);
        Assert.Equal(new[] { PhaseFixture.RedOne, PhaseFixture.BlueOne }.OrderBy(x => x), shown[1].OrderBy(x => x));
        Assert.Equal(new[] { PhaseFixture.RedOne }, shown[2]);
    }

    [Fact]
    public void Menu_without_game_master_or_host_has_no_delete_or_ledger_entry()
    {
        var s = Open(rules: false);
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null);
        Assert.Equal("Sheet", Shape(menu));
        Assert.Empty(PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { "nope" }, null));
        Assert.Empty(PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), Array.Empty<string>(), null));
    }

    [Fact]
    public void Delete_counter_confirms_then_removes_through_the_port()
    {
        var s = Open(rules: false);
        string? asked = null;
        var hooks = new PieceMenuHooks { GameMaster = true, ConfirmDelete = q => { asked = q; return true; } };
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null, hooks);
        var delete = menu.Single(e => e.Label == "Delete counter");
        delete.Run!();
        Assert.Equal("Delete Red 1?", asked);
        Assert.False(s.Game!.State.Pieces.ContainsKey(PhaseFixture.RedOne));

        var refuse = new PieceMenuHooks { GameMaster = true, ConfirmDelete = q => { asked = q; return false; } };
        var both = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedTwo), new[] { PhaseFixture.RedTwo, PhaseFixture.RedTwo, PhaseFixture.BlueOne }, null, refuse);
        both.Single(e => e.Label == "Delete counter").Run!();
        Assert.Equal("Delete 2 pieces?", asked);
        Assert.True(s.Game.State.Pieces.ContainsKey(PhaseFixture.RedTwo));
    }

    [Fact]
    public void Menu_for_masked_piece_is_no_commands()
    {
        var s = Open(rules: true);
        var mapId = MapOf(s, PhaseFixture.RedOne);
        s.UpdateRules(p => p with { MapOwners = new() { new MapOwner { MapId = mapId, SideId = "red" } } });
        s.SetViewSides(new HashSet<string> { "blue" });   // hot-seat: Blue at the screen, Red's map is hidden
        var menu = PieceMenuModel.Build(s, mapId, new[] { PhaseFixture.RedOne }, null, new PieceMenuHooks { GameMaster = true, ShowInLedger = _ => { } });
        var only = Assert.Single(menu);
        Assert.Equal("(no commands)", only.Label);
        Assert.False(only.Enabled);
        Assert.Null(only.Run);
    }

    [Fact]
    public void Calculate_submenu_lists_formulas()
    {
        var s = Open(rules: true);
        var opened = new List<(string Formula, string Piece)>();
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.BlueOne), new[] { PhaseFixture.BlueOne }, null,
            new PieceMenuHooks { ShowFormula = (f, p) => opened.Add((f, p)) });
        var calc = menu.Single(e => e.Label == "Calculate");
        var piece = s.Game!.State.Pieces[PhaseFixture.BlueOne];
        var formulas = PieceFilter.FormulasFor(s.Rules!, piece, s.Game.State, s.Model);
        Assert.NotEmpty(formulas);
        Assert.Equal(formulas.Select(f => f.Name), calc.Children.Select(c => c.Label));
        Assert.True(calc.Enabled);
        Assert.Null(calc.Run);
        calc.Children[0].Run!();
        Assert.Equal((formulas[0].Id, PhaseFixture.BlueOne), opened.Single());

        // No rules pack: no Calculate entry at all.
        var bare = Open(rules: false);
        Assert.DoesNotContain(PieceMenuModel.Build(bare, MapOf(bare, PhaseFixture.BlueOne), new[] { PhaseFixture.BlueOne }, null), e => e.Label == "Calculate");
    }

    [Fact]
    public void Show_in_ledger_disabled_without_rules()
    {
        var s = Open(rules: false);
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null, new PieceMenuHooks { ShowInLedger = _ => { } });
        Assert.Equal("Sheet | - | Show in &Ledger", Shape(menu));
        var ledger = menu[^1];
        Assert.False(ledger.Enabled);
        Assert.Equal("The Ledger needs a rules pack (File > Open Rules Pack… or New Rules Pack…).", ledger.DisabledReason);
        Assert.Equal("view.ledger", ledger.CommandId);
    }

    [Fact]
    public void Sheet_entry_goes_to_the_edit_sheet_hook()
    {
        var s = Open(rules: true);
        (string Label, string Piece)? edited = null;
        var menu = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null,
            new PieceMenuHooks { EditSheet = (a, p) => edited = (a.Label, p) });
        menu[0].Run!();
        Assert.Equal(("Sheet", PhaseFixture.RedOne), edited);
        Assert.True(PieceMenuModel.SheetIndex(s, s.Game!.State.Pieces[PhaseFixture.RedOne], "Sheet") >= 0);
        Assert.Equal(-1, PieceMenuModel.SheetIndex(s, s.Game.State.Pieces[PhaseFixture.RedOne], "Nope"));
        // The Fuel row is managed by the pack's fuel property, seeded from the sheet's 40.
        Assert.Equal("40", PieceMenuModel.ManagedSheetValue(s, s.Game.State.Pieces[PhaseFixture.RedOne], "Fuel"));
        Assert.Null(PieceMenuModel.ManagedSheetValue(s, s.Game.State.Pieces[PhaseFixture.RedOne], "Other"));
    }

    [Fact]
    public async Task Async_hooks_ask_then_run_and_sync_run_refuses_a_pending_dialog()
    {
        var s = Open(rules: false);
        // A gamemaster delete through the async question: nothing happens until the question is answered.
        var answer = new TaskCompletionSource<bool>();
        string? asked = null;
        var hooks = new PieceMenuHooks { GameMaster = true, ConfirmDeleteAsync = q => { asked = q; return answer.Task; } };
        var delete = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedOne), new[] { PhaseFixture.RedOne }, null, hooks)
            .Single(e => e.Label == "Delete counter");
        Assert.NotNull(delete.RunAsync);
        var running = delete.RunAsync!();
        Assert.Equal("Delete Red 1?", asked);
        Assert.False(running.IsCompleted);
        Assert.True(s.Game!.State.Pieces.ContainsKey(PhaseFixture.RedOne));
        answer.SetResult(true);
        await running;
        Assert.False(s.Game.State.Pieces.ContainsKey(PhaseFixture.RedOne));

        // The sync Run cannot wait for an asynchronous dialog: it says so instead of returning early.
        var pending = new PieceMenuHooks { GameMaster = true, ConfirmDeleteAsync = _ => new TaskCompletionSource<bool>().Task };
        var again = PieceMenuModel.Build(s, MapOf(s, PhaseFixture.RedTwo), new[] { PhaseFixture.RedTwo }, null, pending)
            .Single(e => e.Label == "Delete counter");
        Assert.Throws<InvalidOperationException>(() => again.Run!());

        // Sync hooks still run synchronously through Run (the WinForms path).
        var sync = new PieceMenuHooks { GameMaster = true, ConfirmDelete = _ => true };
        PieceMenuModel.Build(s, MapOf(s, PhaseFixture.BlueOne), new[] { PhaseFixture.BlueOne }, null, sync)
            .Single(e => e.Label == "Delete counter").Run!();
        Assert.False(s.Game.State.Pieces.ContainsKey(PhaseFixture.BlueOne));
    }
}
