using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B7: a guest's choices and picks are judged on the guest's own view on the host (Preview and Apply), so the host's verdicts never answer
/// a yes/no question about a value hidden from the guest: a value its own list offers is accepted whatever a hidden value says, and only a
/// value not in its list is refused, with the neutral text. Invented Red/Blue names and numbers only.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class ChoiceOracleTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7oracle-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private static readonly IReadOnlySet<string> Blue = B7FormulaMaskingTests.Blue;

    public ChoiceOracleTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static VariableBinding W => new() { Variable = "W", Kind = BindingKind.Unit, Property = "water" };

    /// <summary>Red's (Private) plans are 47474: on the full state the filter keeps Clear (2 × 20000 below) and excludes Sand (3 × 20000).</summary>
    private const string PlansFilter = "holder(RedDepot, Plans) > lookup(TestTerrainCost, row()) * 20000";

    private static readonly FormulaDef[] Extra =
    {
        new()
        {
            Id = "f-terrain", Name = "Terrain march", Text = "W + lookup(TestTerrainCost, K)",
            Bindings = new() { W, new VariableBinding { Variable = "K", Kind = BindingKind.Input, ChoicesTable = "t-cost", ChoicesFilter = PlansFilter } },
            Outputs = new() { B7FormulaMaskingTests.ToWater },
        },
        Pick("f-pick-holder", "name() == \"Blue 1\" or holder(SouthDepot, Reserve) > 0"),
        Pick("f-pick-keyed", "name() == \"Blue 1\" or keyed(Mines, hex()) > 0"),
    };

    /// <summary>Adds Morale of a picked piece to Blue's water; the candidates' filter reads a value hidden from Blue for Red's pieces.</summary>
    private static FormulaDef Pick(string id, string filter) => new()
    {
        Id = id, Name = "Pick " + id, Text = "W + P",
        Bindings = new() { W, new VariableBinding { Variable = "P", Kind = BindingKind.Piece, Property = "morale", PieceFilter = filter } },
        Outputs = new() { B7FormulaMaskingTests.ToWater },
    };

    /// <summary>The masking fixture plus a Red holder "SouthDepot" keeping a Private "Reserve" of 4.</summary>
    private ModuleSession Open()
    {
        var s = B7FormulaMaskingTests.Open(_tmp, _fixtures, _sessions, Extra);
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef { Id = "reserve", Name = "Reserve", Scope = PropertyScope.Holders, Visibility = Visibility.Private }).ToList(),
            Holders = p.Holders.Append(new HolderDef { Id = "southdepot", Name = "SouthDepot", SideId = "red" }).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("southdepot"), "reserve", 0, 4, "manual", null, null, null, null));
        return s;
    }

    private static IntentExecutor Host(ModuleSession s) => new(s, new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s)));

    private static FormulaVerdict Send(ModuleSession s, string formulaId, Dictionary<string, string> typed, bool preview,
        Dictionary<string, string>? picks = null) =>
        Host(s).Execute("p2", Blue, new FormulaIntent(formulaId, PhaseFixture.BlueOne, null, typed, false, preview, picks)).Verdict!;

    /// <summary>What a verdict says apart from the values: applied, message and each box's state.</summary>
    private static string States(FormulaVerdict v) =>
        $"{v.Applied}|{v.Message}|{string.Join(",", v.Boxes.Select(b => b.Var + "=" + b.State))}";

    private static Dictionary<string, string> Terrain(string k) => new() { ["W"] = "10", ["K"] = k };

    [Fact]
    public void Guest_preview_hidden_excluded_choice_same_as_listed()
    {
        var s = Open();
        var f = s.Rules!.ById<FormulaDef>("f-terrain")!;
        // The full state would exclude Sand; Blue's own list (its view) offers both.
        Assert.Equal(new[] { "Clear" }, InputChoices.For(s.Game!.State, s.Rules, s.Model, f, "K", PhaseFixture.BlueOne));
        var own = InputChoices.For(PlayerView.For(s.Game.State, s.Rules, s.Model, Blue), s.Rules, s.Model, f, "K", PhaseFixture.BlueOne);
        Assert.Equal(new[] { "Clear", "Sand" }, own);
        var clear = Send(s, "f-terrain", Terrain("Clear"), preview: true);
        var sand = Send(s, "f-terrain", Terrain("Sand"), preview: true);
        Assert.Equal(States(clear), States(sand));
        Assert.Null(sand.Message);
        Assert.DoesNotContain(InputChoices.NotOneText, States(sand));
    }

    [Fact]
    public void Guest_apply_hidden_excluded_choice_applies()
    {
        var s = Open();
        var v = Send(s, "f-terrain", Terrain("Sand"), preview: false);
        Assert.True(v.Applied, v.Message);
        Assert.Equal(10 + 3, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_unlisted_choice_not_one_of_choices()
    {
        foreach (var preview in new[] { true, false })
        {
            var s = Open();
            var v = Send(s, "f-terrain", Terrain("Iron"), preview);
            Assert.False(v.Applied);
            Assert.Equal(InputChoices.NotOneText, v.Message);
            Assert.Null(Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        }
    }

    /// <summary>Every candidate of the full state sent as a pick: the host accepts exactly the labels of Blue's own list.</summary>
    private void SameListAsView(string formulaId)
    {
        var s = Open();
        var b = s.Rules!.ById<FormulaDef>(formulaId)!.Bindings.Single(x => x.Variable == "P");
        var full = Quartermaster.Core.PieceChoices.For(s.Game!.State, s.Rules, s.Model, b, null, PhaseFixture.BlueOne);
        var own = Quartermaster.Core.PieceChoices.For(PlayerView.For(s.Game.State, s.Rules, s.Model, Blue), s.Rules, s.Model, b, null, PhaseFixture.BlueOne);
        Assert.Equal(own, Quartermaster.Core.PieceChoices.For(s.Game.State, s.Rules, s.Model, b, Blue, PhaseFixture.BlueOne));   // the host lists Blue's view
        Assert.Equal(PhaseFixture.BlueOne, Assert.Single(own).PieceId);
        Assert.True(full.Count > own.Count);   // the full state would offer Red's pieces too
        foreach (var c in full)
        {
            var typed = new Dictionary<string, string> { ["W"] = "10", ["P"] = c.Label };
            foreach (var preview in new[] { true, false })
            {
                var v = Send(Open(), formulaId, typed, preview, new() { ["P"] = c.PieceId });
                if (own.Contains(c))
                {
                    Assert.Null(v.Message);
                    if (!preview) Assert.True(v.Applied);
                }
                else Assert.Equal(Quartermaster.Core.PieceChoices.Unknown, v.Message);
            }
        }
    }

    [Fact]
    public void Guest_pick_holder_filter_same_list_as_view() => SameListAsView("f-pick-holder");

    [Fact]
    public void Guest_pick_keyed_filter_same_list_as_view() => SameListAsView("f-pick-keyed");

    [Fact]
    public void Guest_pick_label_not_in_own_list_unknown()
    {
        var s = Open();
        var b = s.Rules!.ById<FormulaDef>("f-pick-holder")!.Bindings.Single(x => x.Variable == "P");
        var red = Quartermaster.Core.PieceChoices.For(s.Game!.State, s.Rules, s.Model, b, null, PhaseFixture.BlueOne).First(c => c.PieceId == PhaseFixture.RedOne);
        foreach (var preview in new[] { true, false })
        {
            var v = Send(s, "f-pick-holder", new() { ["W"] = "10", ["P"] = red.Label }, preview, new() { ["P"] = red.PieceId });
            Assert.False(v.Applied);
            Assert.Equal("Choose one of the listed pieces", v.Message);
        }
        Assert.Null(Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }
}
