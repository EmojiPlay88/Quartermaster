using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PieceFilterTests
{
    private static PieceInstance Marked()
    {
        var type = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side" }, ','), "piece;;;unit.svg;Rifle" }, '\t');
        return new PieceInstance { Id = "m1", TypeString = type, MapId = "Main Map", TraitStates = new() { "Red", "" } };
    }

    private static bool M(string? expr, PieceInstance p, GameState s) =>
        PieceFilter.Matches(expr, p, s, RunnerFx.Model, RunnerFx.Pack());

    private static bool M(string? expr, PieceInstance p, GameState s, RulesPack pack) =>
        PieceFilter.Matches(expr, p, s, RunnerFx.Model, pack);

    private static RulesPack With(params PropertyDef[] extra)
    {
        var pack = RunnerFx.Pack();
        pack.Properties.AddRange(extra);
        return pack;
    }

    private static PropertyDef Piece(string id, string name, double def = 0, string? appliesTo = null, Visibility vis = Visibility.Public) =>
        new() { Id = id, Name = name, Scope = PropertyScope.Pieces, Default = def, AppliesTo = appliesTo, Visibility = vis };

    [Fact]
    public void Declared_property_without_value_reads_its_Default()
    {
        var pack = With(Piece("owner", "Owner"));
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.True(M("Owner == 0", p, s, pack));
        Assert.False(M("Owner == 1", p, s, pack));
        Assert.False(M("has(Owner)", p, s, pack));
        Assert.True(M("Fuel == 0", p, s, pack));
        s.Ledgers[LedgerKey.Piece("m1")] = new() { ["owner"] = 1 };
        Assert.True(M("Owner == 1", p, s, pack));
    }

    [Fact]
    public void Holder_property_has_no_default_in_a_filter()
    {
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.False(M("Supply == 0", p, s));
    }

    [Fact]
    public void Property_not_applying_to_piece_has_no_default()
    {
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.False(M("Ammo == 3", p, s, With(Piece("ammo", "Ammo", 3, "name() == \"Tank\""))));
        Assert.True(M("Ammo == 3", p, s, With(Piece("ammo", "Ammo", 3, "name() == \"Rifle\""))));
    }

    [Fact]
    public void Applies_to_reading_another_default_works()
    {
        var pack = With(Piece("kind", "Kind", 2), Piece("load", "Load", 5, "Kind == 2"));
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.True(M("Load == 5", p, s, pack));
    }

    [Fact]
    public void Self_referencing_applies_to_does_not_recurse()
    {
        var pack = With(Piece("loop", "Loop", 1, "Loop == 1"), Piece("a", "A", 0, "B == 0"), Piece("b", "B", 0, "A == 0"));
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.False(M("Loop == 1", p, s, pack));
        Assert.False(M("A == 0", p, s, pack));
    }

    [Fact]
    public void Player_view_does_not_default_private_values()
    {
        var pack = With(Piece("secret", "Secret", 0, null, Visibility.Private), Piece("open", "Open"));
        var s = RunnerFx.State();
        s.IsPlayerView = true;
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.False(M("Secret == 0", p, s, pack));
        Assert.True(M("Open == 0", p, s, pack));
    }

    [Fact]
    public void PieceFilter_MarkerAndName()
    {
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.True(M(null, p, s));
        Assert.True(M("", p, s));
        Assert.True(M("marker(\"Side\") == \"Red\"", p, s));
        Assert.False(M("marker(\"Side\") == \"Blue\"", p, s));
        Assert.True(M("name() == \"Rifle\"", p, s));
        Assert.False(M("has(Fuel)", p, s));
        s.Ledgers[LedgerKey.Piece("m1")] = new() { ["fuel"] = 5 };
        Assert.True(M("has(Fuel) and Fuel > 3", p, s));
        Assert.False(M("Supply > 3", p, s));
    }

    [Fact]
    public void PieceFilter_BadExpression_False()
    {
        var s = RunnerFx.State();
        var p = Marked();
        Assert.False(M("((", p, s));
        Assert.False(M("marker(\"Nope\") == \"x\"", p, s));
        Assert.False(M("Missing + 1", p, s));
    }

    [Fact]
    public void FormulasFor_OrderedAndFiltered()
    {
        var pack = RunnerFx.Pack();
        pack.Formulas.Add(new FormulaDef { Id = "a", Name = "Alpha", Text = "1", Bindings = new() { new VariableBinding { Variable = "X", Kind = BindingKind.Unit, Property = "fuel" } } });
        pack.Formulas.Add(new FormulaDef { Id = "g", Name = "Global only", Text = "1" });
        pack.Formulas.Add(new FormulaDef { Id = "r", Name = "Red only", Text = "1", AppliesTo = "marker(\"Side\") == \"Red\"",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Add } } });
        var s = RunnerFx.State();
        var red = Marked();
        var names = PieceFilter.FormulasFor(pack, red, s, RunnerFx.Model).Select(f => f.Name).ToList();
        Assert.Equal(new[] { "Alpha", "Fuel Use", "Red only" }, names);
        var plain = s.Pieces["u1"];
        Assert.Equal(new[] { "Alpha", "Fuel Use" }, PieceFilter.FormulasFor(pack, plain, s, RunnerFx.Model).Select(f => f.Name).ToList());
    }

    [Fact]
    public void Filter_reads_turn_and_stage()
    {
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        s.Position = new PhasePosition(3, 2, "ph1", 0);
        Assert.True(M("turn() == 3 and stage() == 2", p, s));
        Assert.False(M("turn() == 2", p, s));
        s.Position = null;
        s.Turn = new TurnState(5, null);
        Assert.True(M("turn() == 5 and stage() == 1", p, s));
    }
}
