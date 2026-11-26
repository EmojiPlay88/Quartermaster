using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Core.Tests;

/// <summary>Formula outputs that write keyed values (B6), and the Remove output's To map resolved through MapRefs. Invented Riverland data.</summary>
public class PlacesRunnerTests
{
    private static FormulaDef Flood(params FormulaOutput[] outputs) =>
        new() { Id = "f-flood", Name = "Flood the ford", Text = "3", Outputs = outputs.ToList() };

    private static FormulaOutput Write(OutputTarget target, string prop, string? key, OutputOp op = OutputOp.Set, string? holder = null) =>
        new() { Target = target, Property = prop, Op = op, Key = key, HolderId = holder };

    private static (FormulaTrace Trace, IReadOnlyList<GameCommand> Commands, GameState State) RunAndApply(FormulaDef f, string? piece = "r1",
        GameState? s = null, RulesPack? pack = null)
    {
        s ??= PlacesKit.State();
        pack = (pack ?? PlacesFixture.Pack()) with { Formulas = new() { f } };
        var runner = new FormulaRunner(pack, PlacesKit.Model, (n, _) => Enumerable.Repeat(1, n).ToList()) { NewPieceId = () => "p:new" };
        var t = runner.Run(s, new RunRequest(f.Id, piece, new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        var cmds = runner.Commands(t, null);
        foreach (var c in cmds) s.Apply(c);
        return (t, cmds, s);
    }

    [Fact]
    public void Output_key_hex_writes_place()
    {
        var s = PlacesKit.State();
        s.Keyed![PlacesKit.Global]["p-ford"].Remove("0412");
        // The working names the property as written ("Ford depth" has a space, so no expression could name it; an output can).
        var pack = PlacesFixture.Pack();
        pack = pack with { Properties = pack.Properties.Select(p => p.Id == "p-ford" ? p with { Name = "Ford depth" } : p).ToList() };
        var (t, cmds, after) = RunAndApply(Flood(Write(OutputTarget.Global, "p-ford", "hex()")), s: s, pack: pack);
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(PlacesKit.Global, o.Key);
        Assert.Equal("0412", o.ValueKey);
        Assert.Equal(0, o.Before);
        Assert.Equal(3, o.After);
        Assert.Equal("Ford depth[0412] = 3 (Flooded)", o.Part);
        Assert.EndsWith("→ Ford depth[0412] = 3 (Flooded)", t.Working);
        Assert.Equal("0412", Assert.Single(cmds.OfType<LedgerDelta>()).ValueKey);
        Assert.Equal(3, KeyedValues.Get(after, PlacesKit.Global, "p-ford", "0412"));
        Assert.False(after.Ledgers.TryGetValue(PlacesKit.Global, out var g) && g.ContainsKey("p-ford"));
        Assert.Contains(after.Log, l => l.Text.Contains("Ford depth[0412] = 3 (Flooded)"));
    }

    [Fact]
    public void Output_key_turn_add()
    {
        var f = new FormulaDef
        {
            Id = "f-barges", Name = "Order barges", Text = "2",
            Outputs = new() { Write(OutputTarget.Global, "p-due", "turn() + 2", OutputOp.Add) },
        };
        var (t, _, after) = RunAndApply(f, null);
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal("3", o.ValueKey);
        Assert.Equal("BargesDue[3] 1 + 2 = 3", o.Part);
        Assert.Equal(3, KeyedValues.Get(after, PlacesKit.Global, "p-due", "3"));
        Assert.Equal(5, KeyedValues.Get(after, PlacesKit.Global, "p-due", "2"));
    }

    [Fact]
    public void Output_key_table_on_holder()
    {
        var f = new FormulaDef
        {
            Id = "f-yard", Name = "Build rafts", Text = "1",
            Outputs = new() { Write(OutputTarget.Holder, "p-hulls", "\" raft \"", OutputOp.Add, "yard") },
        };
        var (t, _, after) = RunAndApply(f, null);
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(PlacesKit.Yard, o.Key);
        Assert.Equal("Raft", o.ValueKey);
        Assert.Equal(1, o.Before);   // the property's Default
        Assert.Equal("HullCount[Raft] 1 + 1 = 2", o.Part);
        Assert.Equal(2, KeyedValues.Get(after, PlacesKit.Yard, "p-hulls", "Raft"));
        // The Global holder does not keep it.
        var g = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(f with { Outputs = new() { Write(OutputTarget.Global, "p-hulls", "\"Raft\"") } }), null, "f-yard");
        Assert.Equal("Global does not keep HullCount", g.Error);
    }

    [Fact]
    public void Output_keyed_on_placed_piece()
    {
        var f = new FormulaDef
        {
            Id = "f-load", Name = "Launch barge", Text = "9",
            Outputs = new()
            {
                new FormulaOutput { Action = OutputAction.Place, Target = OutputTarget.Unit, Slot = "1" },
                Write(OutputTarget.Placed, "p-cargo", "\"Barge\""),
            },
        };
        var (t, _, after) = RunAndApply(f);
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(LedgerKey.Piece("p:new"), o.Key);
        Assert.Equal("Barge", o.ValueKey);
        Assert.Equal(0, o.Before);
        Assert.Equal(9, KeyedValues.Get(after, LedgerKey.Piece("p:new"), "p-cargo", "Barge"));
        // The target's own line names the key; the run's working names the key and the piece
        Assert.Equal("Cargo[Barge] = 9", o.Part);
        Assert.Contains("Cargo[Barge] of Test Unit = 9", t.Working);
    }

    [Fact]
    public void Output_key_error_fails_run_writes_nothing()
    {
        var f = Flood(Write(OutputTarget.Unit, "p-str", null), Write(OutputTarget.Global, "p-ford", "\"9999\""));
        var s = PlacesKit.State();
        var before = System.Text.Json.JsonSerializer.Serialize(s, QuartermasterJson.Options);
        var (t, cmds, after) = RunAndApply(f, s: s);
        Assert.Equal("The key for FordDepth: no hex named \"9999\" on any board", t.Error);
        Assert.Empty(cmds);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(after, QuartermasterJson.Options));
        var turn = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(Flood(Write(OutputTarget.Global, "p-due", "0"))), null, "f-flood");
        Assert.Equal("The key for BargesDue: the turn must be a whole number of at least 1", turn.Error);
    }

    [Fact]
    public void Output_keyed_without_key_fails()
    {
        var none = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(Flood(Write(OutputTarget.Global, "p-ford", "  "))), "r1", "f-flood");
        Assert.Equal("Formula \"Flood the ford\": output 1 writes keyed property \"FordDepth\" but has no key", none.Error);
        var unkeyed = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(Flood(Write(OutputTarget.Unit, "p-str", null), Write(OutputTarget.Unit, "p-str", "hex()"))), "r1", "f-flood");
        Assert.Equal("Formula \"Flood the ford\": output 2 has a key but writes property \"Strength\", which is not keyed", unkeyed.Error);
        var holder = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(Flood(Write(OutputTarget.Holder, "p-ford", "hex()", holder: "yard"))), "r1", "f-flood");
        Assert.Equal("Formula \"Flood the ford\": output 1 writes place property \"FordDepth\" to a holder other than Global", holder.Error);
    }

    [Fact]
    public void Remove_ToMapId_by_name_and_spaces()
    {
        var f = new FormulaDef
        {
            Id = "f-send", Name = "Send south", Text = "1",
            Outputs = new() { new FormulaOutput { Action = OutputAction.Remove, Target = OutputTarget.Unit, ToMapId = "  South " } },
        };
        var (t, cmds, after) = RunAndApply(f);
        Assert.Null(t.Error);
        var move = Assert.Single(cmds.OfType<MovePieces>());
        Assert.Equal("South", move.MapId);
        Assert.Equal("South", after.Pieces["r1"].MapId);
        var bad = PlacesKit.Run(PlacesKit.State(), PlacesKit.Pack(f with { Outputs = new() { f.Outputs[0] with { ToMapId = " Nowhere " } } }), "r1", "f-send");
        Assert.Equal("Map \"Nowhere\" was not found in the module", bad.Error);
    }

    [Fact]
    public void Columns_omit_keyed()
    {
        Assert.Equal(new[] { "Strength" }, LedgerTable.Columns(PlacesFixture.Pack()).Select(c => c.Name));
        var rows = LedgerTable.Rows(PlacesKit.State(), PlacesFixture.Pack(), PlacesKit.Model, "");
        Assert.All(rows, r => Assert.Single(r.Values));
    }
}
