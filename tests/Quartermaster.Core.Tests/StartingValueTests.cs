using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>#4: starting values worked out from an expression (PropertyDef.DefaultExpr) when a piece has no sheet value.</summary>
public class StartingValueTests
{
    private static readonly string BoatType = PieceTypeParser.EncodeState(new[] { "propertysheet;0Crew~0Base;Sheet;", "mark;Kind", "piece;;;unit.svg;Boat" });

    private static string BoatState(string crew, string kind, string @base = "") => PieceTypeParser.EncodeState(new[] { crew + "~" + @base, kind, "" });

    private static GameState S(params (string Id, string Crew, string Kind, string Base)[] boats)
    {
        var s = new GameState { GameId = "g" };
        s.MapOrder.Add("m1");
        foreach (var b in boats) s.Apply(new AddPiece(b.Id, BoatType, BoatState(b.Crew, b.Kind, b.Base), "m1", new PointD(0, 0), null));
        return s;
    }

    private static RulesPack Pack(params PropertyDef[] props) => new()
    {
        Properties = props.ToList(),
        Holders = new() { new HolderDef { Id = "depot", Name = "Depot" } },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-crew", Name = "CrewSizes", Kind = TableKind.OneD,
                RowKeys = new() { "Large", "Small" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "6" }, new() { "3" } },
            },
        },
        Formulas = new() { new FormulaDef { Id = "f-x", Name = "X", Text = "1" } },
    };

    private static (string, string, double)[] Seeds(SeedLedger? s) => s!.Seeds.Select(x => (x.Key, x.PropertyId, x.Value)).ToArray();

    [Fact]
    public void Expression_seeds_when_no_sheet_value()
    {
        var s = S(("a", "", "Large", ""), ("b", "", "Small", ""));
        var pack = Pack(new PropertyDef { Id = "crew", Name = "Boat crew", Scope = PropertyScope.Pieces, Default = 1, DefaultExpr = "lookup(CrewSizes, marker(\"Kind\"))" });
        var log = new DiagnosticLog();
        var seed = LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model, log);
        Assert.Empty(log.Items);
        Assert.Equal([("p:a", "crew", 6.0), ("p:b", "crew", 3.0)], Seeds(seed));
    }

    [Fact]
    public void Sheet_value_wins_over_expression()
    {
        var s = S(("a", "5", "Large", ""), ("b", "", "Large", ""));
        var pack = Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Pieces, SheetField = "Crew", DefaultExpr = "99" });
        Assert.Equal([("p:a", "crew", 5.0), ("p:b", "crew", 99.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model)));
    }

    [Fact]
    public void Expression_reads_sibling_and_earlier_seed()
    {
        var s = S(("a", "", "Large", "4"));
        var pack = Pack(
            new PropertyDef { Id = "base", Name = "Base", Scope = PropertyScope.Pieces, SheetField = "Base" },
            new PropertyDef { Id = "double", Name = "Double", Scope = PropertyScope.Pieces, DefaultExpr = "Base * 2" },
            new PropertyDef { Id = "total", Name = "Total", Scope = PropertyScope.Pieces, DefaultExpr = "Double + 1" });
        Assert.Equal([("p:a", "base", 4.0), ("p:a", "double", 8.0), ("p:a", "total", 9.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model)));
    }

    [Fact]
    public void Expression_honours_applies_to()
    {
        var s = S(("a", "", "Large", ""), ("b", "", "Small", ""));
        var pack = Pack(new PropertyDef { Id = "oars", Name = "Oars", Scope = PropertyScope.Pieces, DefaultExpr = "7", AppliesTo = "marker(\"Kind\") == \"Large\"" });
        Assert.Equal([("p:a", "oars", 7.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model)));
        Assert.Null(LedgerSeeder.BuildSeeds(s, pack, p => p.Id == "b", RunnerFx.Model));
    }

    [Fact]
    public void Expression_error_seeds_default_and_warns_once()
    {
        var s = S(("a", "", "Large", ""), ("b", "", "Small", ""));
        var pack = Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Pieces, Default = 2, DefaultExpr = "Nope + 1" });
        var log = new DiagnosticLog();
        Assert.Equal([("p:a", "crew", 2.0), ("p:b", "crew", 2.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model, log)));
        var w = Assert.Single(log.Items);
        Assert.Equal((DiagnosticSeverity.Warning, "rules"), (w.Severity, w.Category));
        Assert.Equal("Starting value of \"Crew\" failed on \"Boat\": Unknown property 'Nope'; it starts at 2", w.Message);

        // The calls a starting value may not make fail at run time too, so the piece gets the Default.
        foreach (var bad in new[] { "dice(1, 6)", "holder(Depot, Crew)", "runs(\"f-x\")", "countof()", "sum(Crew)", "1 +" })
        {
            var p = Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Both, Default = 2, DefaultExpr = bad });
            var l = new DiagnosticLog();
            Assert.Equal([("p:a", "crew", 2.0), ("p:b", "crew", 2.0)], Seeds(LedgerSeeder.BuildSeeds(s, p, _ => true, RunnerFx.Model, l)));
            Assert.Contains("Starting value of \"Crew\" failed on \"Boat\": ", Assert.Single(l.Items).Message);
        }
    }

    [Fact]
    public void Non_number_result_seeds_default()
    {
        var s = S(("a", "", "Large", ""));
        var pack = Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Pieces, Default = 3, DefaultExpr = "marker(\"Kind\")" });
        var log = new DiagnosticLog();
        Assert.Equal([("p:a", "crew", 3.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model, log)));
        Assert.Equal("Starting value of \"Crew\" failed on \"Boat\": the starting value is not a number; it starts at 3", Assert.Single(log.Items).Message);
    }

    [Fact]
    public void Expression_evaluated_once_per_piece()
    {
        var s = S(("a", "", "Large", ""));
        var pack = Pack(
            new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Pieces, DefaultExpr = "lookup(CrewSizes, marker(\"Kind\"))" },
            new PropertyDef { Id = "bad", Name = "Bad", Scope = PropertyScope.Pieces, Default = 4, DefaultExpr = "Nope" });
        var seed = LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model, new DiagnosticLog());
        Assert.Equal([("p:a", "crew", 6.0), ("p:a", "bad", 4.0)], Seeds(seed));
        s.Apply(seed!);
        Assert.Null(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model, new DiagnosticLog()));
    }

    [Fact]
    public void Both_scope_holders_keep_default()
    {
        var s = S(("a", "", "Large", ""));
        var pack = Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Both, Default = 1, DefaultExpr = "5" });
        Assert.Equal([("p:a", "crew", 5.0)], Seeds(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model)));
        Assert.Null(LedgerSeeder.BuildSeeds(s, Pack(new PropertyDef { Id = "crew", Name = "Crew", Scope = PropertyScope.Holders, DefaultExpr = "5" }), _ => true, RunnerFx.Model));
    }
}
