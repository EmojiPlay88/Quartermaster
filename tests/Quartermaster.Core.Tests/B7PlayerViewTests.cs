using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.HiddenValuesTests;

namespace Quartermaster.Core.Tests;

/// <summary>B7 #74a (Public values of another side's holder) and #73 (game over shows everything) in a player view. Invented Riverland data.</summary>
public class B7PlayerViewTests
{
    private static ModuleModel Model => RunnerFx.Model;
    private static readonly IReadOnlySet<string> SouthOnly = new HashSet<string> { South };
    private static readonly string Depot = LedgerKey.Holder(NorthDepot);

    private static RulesPack PackWithFormula()
    {
        var pk = Pack();
        pk.Formulas.Add(new FormulaDef
        {
            Id = "f-haul", Name = "Haul", Text = "2",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = NorthDepot, Property = "p-stores", Op = OutputOp.Add } },
        });
        return pk;
    }

    private static GameState NewState()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { North }), new PlayerDef("p2", "Bob", new[] { South }) }));
        return s;
    }

    private static LedgerDelta Delta(string key, string prop, double before, double after, string? valueKey = null) =>
        new(key, prop, before, after, "manual", null, null, null, null) { ValueKey = valueKey };

    private static void RunHaul(GameState s, RulesPack pk)
    {
        var runner = new FormulaRunner(pk, Model, (n, sides) => Enumerable.Repeat(1, n).ToList());
        var t = runner.Run(s, new RunRequest("f-haul", null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(t.Error);
        foreach (var c in runner.Commands(t, null, null, null, North)) s.Apply(c);
    }

    [Fact]
    public void View_keeps_public_value_of_other_side_holder()
    {
        var pk = PackWithFormula();
        var s = NewState();
        s.Apply(Delta(Depot, "p-stores", 0, 5));
        RunHaul(s, pk);
        var manual = s.Log.First(l => l.Kind == LogKind.Ledger);
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.True(view.IsPlayerView);
        Assert.Equal(7, view.Ledgers[Depot]["p-stores"]);
        Assert.Equal(2, view.LedgerHistory.Count(e => e.Key == Depot && e.PropertyId == "p-stores"));
        Assert.Contains(view.Log, l => l.Kind == LogKind.Ledger && l.Text == manual.Text);
        Assert.Contains(view.Log, l => l.Text == "Northern League ledger changed");   // the run's line is the north's (its stamp)
        Assert.DoesNotContain(view.Log, l => l.Kind is LogKind.Formula or LogKind.Calc && l.Text != "Northern League ledger changed");
    }

    [Fact]
    public void View_drops_private_value_of_other_side_holder()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-stores", 0, 5));
        s.Apply(Delta(Depot, "p-reserve", 0, 4));
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.False(view.Ledgers[Depot].ContainsKey("p-reserve"));
        Assert.DoesNotContain(view.LedgerHistory, e => e.PropertyId == "p-reserve");
        Assert.Single(view.Log, l => l.Text == "Northern League ledger changed");
        var own = PlayerView.For(s, pk, Model, new HashSet<string> { North });
        Assert.Equal(4, own.Ledgers[Depot]["p-reserve"]);
    }

    [Fact]
    public void Holder_read_in_view_private_missing_not_default()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-stores", 0, 5));   // Reserve never written: the full state reads its Default 7
        Assert.Equal(7, PieceFilter.HolderRead(s, pk, "NorthDepot", "Reserve").Number);
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.True(view.Ledgers.ContainsKey(Depot));
        Assert.Equal(5, PieceFilter.HolderRead(view, pk, "NorthDepot", "Stores").Number);
        var ex = Assert.Throws<EvalError>(() => PieceFilter.HolderRead(view, pk, "NorthDepot", "Reserve"));
        Assert.True(ex.MissingValue);
        var own = PlayerView.For(s, pk, Model, new HashSet<string> { North });
        Assert.Equal(7, PieceFilter.HolderRead(own, pk, "NorthDepot", "Reserve").Number);
    }

    [Fact]
    public void Keyed_holder_public_visible()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-tolls", 0, 3, "2"));
        s.Apply(Delta(Depot, "p-bids", 0, 9, "2"));
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Equal(3, view.Keyed![Depot]["p-tolls"]["2"]);
        Assert.False(view.Keyed[Depot].ContainsKey("p-bids"));
        Assert.False(KeyedValues.HiddenIn(view, pk, Depot, pk.ById<PropertyDef>("p-tolls")!));
        Assert.True(KeyedValues.HiddenIn(view, pk, Depot, pk.ById<PropertyDef>("p-bids")!));
        Assert.False(KeyedValues.HiddenFor(s, pk, SouthOnly, Depot, pk.ById<PropertyDef>("p-tolls")!));
        Assert.True(KeyedValues.HiddenFor(s, pk, SouthOnly, Depot, pk.ById<PropertyDef>("p-bids")!));
        Assert.True(KeyedValues.HiddenFor(s, pk, SouthOnly, LedgerKey.Holder("h-gone"), pk.ById<PropertyDef>("p-tolls")!));   // unknown holder
    }

    [Fact]
    public void Holder_with_only_hidden_values_leaves_no_key()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-reserve", 0, 4));
        s.Apply(Delta(Depot, "p-bids", 0, 9, "2"));
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.False(view.Ledgers.ContainsKey(Depot));   // nothing shows that the depot has a ledger entry at all
        Assert.True(view.Keyed == null || !view.Keyed.ContainsKey(Depot));
        Assert.DoesNotContain(view.LedgerHistory, e => e.Key == Depot);
        Assert.DoesNotContain(Depot, System.Text.Json.JsonSerializer.Serialize(view, QuartermasterJson.Options));
        var own = PlayerView.For(s, pk, Model, new HashSet<string> { North });
        Assert.True(own.Ledgers.ContainsKey(Depot));
        Assert.True(own.Keyed!.ContainsKey(Depot));
    }

    [Fact]
    public void Game_over_view_is_full()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-reserve", 0, 4));
        s.Apply(new EndGame("The Northern League holds the river", new[] { North }, null));
        s.Apply(new QueueRuns(new[] { new PendingRun("r1", "f-x", "Orders", "start", Array.Empty<PendingUnit>(), true) { AskSideId = North } }));
        var view = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.False(view.IsPlayerView);
        Assert.Null(view.ViewSides);
        Assert.Equal(4, view.Ledgers[Depot]["p-reserve"]);
        Assert.Contains(view.Pending, r => r.Id == "r1" && r.AskSideId == North);
        Assert.NotSame(s, view);
        Assert.Equal(s.Log.Count, view.Log.Count);
    }

    [Fact]
    public void Undo_out_of_game_over_hides_again()
    {
        var pk = Pack();
        var g = new GameSession(NewState(), "sha");
        g.Execute(Delta(Depot, "p-reserve", 0, 4));
        g.Execute(new EndGame("The Northern League holds the river", new[] { North }, null));
        Assert.Equal(4, PlayerView.For(g.State, pk, Model, SouthOnly).Ledgers[Depot]["p-reserve"]);
        g.Undo();
        Assert.Null(g.State.GameOver);
        var view = PlayerView.For(g.State, pk, Model, SouthOnly);
        Assert.True(view.IsPlayerView);
        Assert.False(view.Ledgers.ContainsKey(Depot));   // its only stored value is hidden: the key goes too
    }

    [Fact]
    public void HiddenFor_game_over_false()
    {
        var pk = Pack();
        var s = NewState();
        s.Apply(Delta(Depot, "p-reserve", 0, 4));
        var reserve = pk.ById<PropertyDef>("p-reserve")!;
        Assert.True(KeyedValues.HiddenFor(s, pk, SouthOnly, Depot, reserve));
        s.Apply(new EndGame("The Northern League holds the river", new[] { North }, null));
        Assert.False(KeyedValues.HiddenFor(s, pk, SouthOnly, Depot, reserve));
    }
}
