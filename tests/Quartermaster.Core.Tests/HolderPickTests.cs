using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B9 #13b: a holder chosen when the formula runs (<see cref="HolderChoices"/>, the runner). The Task 2 Riverland fixture
/// (<see cref="HolderOwnersTests.Pack"/>: Ford Dump owned by <c>keyed(DumpOwner, "Ford Dump")</c>, Mill Dump of the south, North Depot of the
/// north, a Private Stores) plus the formula Draw stores: a Holder binding Dump over Ford Dump, Mill Dump and North Depot (property Stores), an
/// Input Need, and an output that subtracts Need from the chosen holder's Stores. Invented data only.
/// </summary>
public class HolderPickTests
{
    private const string Ford = HolderOwnersTests.Ford;
    private const string Mill = HolderOwnersTests.Mill;
    private const string Depot = "h-north";
    private static ModuleModel Model => RunnerFx.Model;
    private static readonly IReadOnlySet<string> NorthOnly = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> SouthOnly = new HashSet<string> { "south" };

    private static VariableBinding Dump => new()
    {
        Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderChoices = new() { Ford, Mill, Depot },
    };

    private static FormulaDef Draw => new()
    {
        Id = "f-draw", Name = "Draw stores", Text = "Dump",
        Bindings = new() { Dump, new VariableBinding { Variable = "Need", Kind = BindingKind.Input } },
        Outputs = new()
        {
            new FormulaOutput { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores", Op = OutputOp.Subtract, Expr = "Need" },
        },
    };

    private static RulesPack Pack(FormulaDef? f = null) => HolderOwnersTests.Pack() with { Formulas = new() { f ?? Draw } };

    private static GameState State()
    {
        var s = HolderOwnersTests.NewState();
        s.Apply(new LedgerDelta(LedgerKey.Holder(Ford), "p-stores", 0, 10, "manual", null, null, null, null));
        s.Apply(new LedgerDelta(LedgerKey.Holder(Mill), "p-stores", 0, 20, "manual", null, null, null, null));
        s.Apply(new LedgerDelta(LedgerKey.Holder(Depot), "p-stores", 0, 30, "manual", null, null, null, null));
        return s;
    }

    private static FormulaRunner Runner(RulesPack pk) => new(pk, Model, (n, _) => Enumerable.Repeat(1, n).ToList());

    private static FormulaTrace Run(GameState s, RulesPack pk, string? dump, string? need = "3", IReadOnlySet<string>? sides = null, bool auto = false,
        string formulaId = "f-draw")
    {
        var typed = new Dictionary<string, string>();
        if (dump != null) typed["Dump"] = dump;
        if (need != null) typed["Need"] = need;
        return Runner(pk).Run(s, new RunRequest(formulaId, null, typed, new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(),
            UseAutoValues: auto, PickSides: sides));
    }

    private static string[] Labels(GameState s, RulesPack pk, IReadOnlySet<string>? held) =>
        HolderChoices.For(s, pk, pk.Formulas[0].Bindings[0], held).Select(c => c.Label).ToArray();

    private static VariableState Var(FormulaTrace t, string name) => t.Variables.Single(v => v.Variable == name);

    [Fact]
    public void Candidates_are_listed_holders_chooser_may_act_on()
    {
        var pk = Pack();
        var s = State();
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, Labels(s, pk, NorthOnly));
        Assert.Equal(new[] { "Mill Dump" }, Labels(s, pk, SouthOnly));
        Assert.Equal(new[] { Ford, Depot }, HolderChoices.For(s, pk, Dump, NorthOnly).Select(c => c.HolderId).ToArray());
        s.Apply(HolderOwnersTests.DumpOwner(pk, 1, 2));   // the Southern Compact takes Ford Dump
        Assert.Equal(new[] { "North Depot" }, Labels(s, pk, NorthOnly));
        Assert.Equal(new[] { "Ford Dump", "Mill Dump" }, Labels(s, pk, SouthOnly));
        // A player view without a held set: the view's own sides choose (an online guest's state is its view).
        Assert.Equal(new[] { "North Depot" }, Labels(PlayerView.For(s, pk, Model, NorthOnly), pk, null));
        // A holder of no side is everyone's to choose.
        s.Apply(HolderOwnersTests.DumpOwner(pk, 2, 0));
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, Labels(s, pk, NorthOnly));
        Assert.Equal(new[] { "Ford Dump", "Mill Dump" }, Labels(s, pk, SouthOnly));
    }

    [Fact]
    public void Solo_lists_every_listed_holder()
    {
        var pk = Pack();
        Assert.Equal(new[] { "Ford Dump", "Mill Dump", "North Depot" }, Labels(State(), pk, null));
        // The list order, not the pack's: the global holder and unknown ids are never candidates.
        var b = Dump with { HolderChoices = new() { Depot, "h-gone", HolderDef.GlobalId, Ford } };
        Assert.Equal(new[] { "North Depot", "Ford Dump" }, HolderChoices.For(State(), pk, b, null).Select(c => c.Label).ToArray());
    }

    [Fact]
    public void Duplicate_labels_numbered_in_list_order()
    {
        var pk = Pack();
        pk = pk with
        {
            Holders = pk.Holders.Select(h => h.Id switch
            {
                Mill => h with { Name = "ford dump" },
                Depot => h with { Name = " " },
                _ => h,
            }).ToList(),
        };
        Assert.Equal(new[] { "Ford Dump", "ford dump #2", Depot }, Labels(State(), pk, null));
        var t = Run(State(), pk, "FORD DUMP #2");
        Assert.Null(t.Error);
        Assert.Equal(20, Var(t, "Dump").Value!.Value.Number);
    }

    [Fact]
    public void Blank_waiting_unknown_and_none_texts()
    {
        Assert.Equal("Choose one of the listed holders", HolderChoices.Unknown);
        Assert.Equal("No holder to choose", HolderChoices.None);
        var pk = Pack();
        var s = State();
        Assert.Equal("Waiting for input", Var(Run(s, pk, null, sides: NorthOnly), "Dump").MissingReason);
        Assert.Equal("Waiting for input", Var(Run(s, pk, "  ", sides: NorthOnly), "Dump").MissingReason);
        var other = Run(s, pk, "Mill Dump", sides: NorthOnly);   // the south's dump
        Assert.Equal(HolderChoices.Unknown, Var(other, "Dump").MissingReason);
        Assert.Null(other.Result);
        Assert.Empty(other.Outputs);
        Assert.Equal(HolderChoices.Unknown, Var(Run(s, pk, "Weir Dump", sides: NorthOnly), "Dump").MissingReason);
        Assert.Equal(HolderChoices.None, Var(Run(s, pk, "Ford Dump", sides: new HashSet<string>()), "Dump").MissingReason);
        Assert.False(Var(Run(s, pk, null, sides: NorthOnly), "Dump").Auto);
    }

    [Fact]
    public void Picked_holder_value_read_and_working_names_it()
    {
        var pk = Pack();
        var t = Run(State(), pk, " ford dump ", sides: NorthOnly);
        Assert.Null(t.Error);
        var dump = Var(t, "Dump");
        Assert.Equal(10, dump.Value!.Value.Number);
        Assert.Equal("Stores of Ford Dump", dump.Source);
        Assert.Equal(10, t.Result!.Value.Number);
        Assert.Contains("Stores of Ford Dump 10 − 3 = 7", t.Working);
        // An unset value reads the property's Default (0), as a fixed Holder binding does.
        var fresh = HolderOwnersTests.NewState();
        Assert.Equal(0, Var(Run(fresh, pk, "Mill Dump"), "Dump").Value!.Value.Number);
    }

    [Fact]
    public void Holder_variable_output_writes_chosen_holder()
    {
        var pk = Pack();
        var s = State();
        var t = Run(s, pk, "North Depot", sides: NorthOnly);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(LedgerKey.Holder(Depot), o.Key);
        Assert.Equal(30, o.Before);
        Assert.Equal(27, o.After);
        Assert.Equal(OutputTarget.Holder, o.Target);
        Assert.Equal("North Depot", o.Subject);
        var delta = Assert.Single(Runner(pk).Commands(t, null).OfType<LedgerDelta>());
        Assert.Equal(LedgerKey.Holder(Depot), delta.Key);
        var ford = Run(s, pk, "Ford Dump", sides: NorthOnly);
        Assert.Equal(LedgerKey.Holder("h-ford"), Assert.Single(Runner(pk).Commands(ford, null).OfType<LedgerDelta>()).Key);
        // A holder that does not keep the property fails the run (the validator refuses such a pack).
        // (Owner is kept by Mill Dump, so the binding reads it; Stores only by Ford Dump and North Depot here.)
        var narrow = pk with
        {
            Properties = pk.Properties.Select(p => p.Id == "p-stores" ? p with { Holders = new() { Ford, Depot } } : p).ToList(),
            Formulas = new() { Draw with { Bindings = new() { Dump with { Property = "p-owner" }, Draw.Bindings[1] } } },
        };
        Assert.Equal("Mill Dump does not keep Stores", Run(s, narrow, "Mill Dump").Error);
    }

    [Fact]
    public void Holder_variable_reveal_reads_chosen_holder()
    {
        var reveal = Draw with
        {
            Outputs = new()
            {
                new FormulaOutput { Action = OutputAction.Reveal, Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores" },
            },
        };
        var pk = Pack(reveal);
        var t = Run(State(), pk, "Mill Dump", sides: SouthOnly);
        Assert.Null(t.Error);
        var r = Assert.Single(t.Reveals);
        Assert.Equal("Mill Dump", r.Subject);
        Assert.Equal(20, r.Value);
        Assert.Contains(Runner(pk).Commands(t, null).OfType<AddLogLine>(), l => l.Text == "Mill Dump: Stores 20");
        // On the chooser's own view the value is seen (a picked holder is always the chooser's or no side's).
        var view = PlayerView.For(State(), pk, Model, SouthOnly);
        Assert.Equal(20, Assert.Single(Run(view, pk, "Mill Dump").Reveals).Value);
    }

    [Fact]
    public void Pick_memoised_once_per_run()
    {
        var pk = Pack();
        HolderChoices.Lists = 0;
        var t = Run(State(), pk, "Ford Dump", sides: NorthOnly);   // the binding and the output share one pick
        Assert.Null(t.Error);
        Assert.Equal(1, HolderChoices.Lists);
        // An output naming a picker nothing else reads still asks it.
        HolderChoices.Lists = 0;
        var outOnly = Pack(Draw with { Text = "Need" });
        Assert.Equal("Waiting for input", Var(Run(State(), outOnly, null, sides: NorthOnly), "Dump").MissingReason);
        Assert.Equal(1, HolderChoices.Lists);
    }

    [Fact]
    public void Fully_automatic_stops_for_holder_pick()
    {
        var pk = Pack();
        var t = Run(State(), pk, null, sides: NorthOnly, auto: true);
        var dump = Var(t, "Dump");
        Assert.Equal("Waiting for input", dump.MissingReason);
        Assert.False(dump.Auto);
        Assert.Null(t.Result);
        Assert.Empty(t.Outputs);
        Assert.Empty(Runner(pk).Commands(t, null));
    }

    [Fact]
    public void Undo_takes_back_run_in_one_step()
    {
        var pk = Pack();
        var g = new GameSession(State(), "sha");
        var t = Run(g.State, pk, "Ford Dump", sides: NorthOnly);
        using (g.BeginAction()) foreach (var c in Runner(pk).Commands(t, null)) g.Execute(c);
        Assert.Equal(7, Ledger.Get(g.State, LedgerKey.Holder(Ford), "p-stores"));
        g.Undo();
        Assert.Equal(10, Ledger.Get(g.State, LedgerKey.Holder(Ford), "p-stores"));
        Assert.Equal(20, Ledger.Get(g.State, LedgerKey.Holder(Mill), "p-stores"));
        g.Redo();
        Assert.Equal(7, Ledger.Get(g.State, LedgerKey.Holder(Ford), "p-stores"));
    }
}
