using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented pack, formula and numbers throughout; temp cache and rules root only.</summary>
public class RulesEndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-e2e-" + Guid.NewGuid().ToString("N"));

    public RulesEndToEndTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static string Add(string id, string type, string state) =>
        "+/" + SequenceEncoder.Encode(new[] { id, type, state }, '/');

    private static FixtureModule Fixture()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        var type = "propertysheet;0Fuel;Sheet;\tpiece;;;unit.svg;A";
        var state = "40\tMain Map;100;50;1;0";
        return new FixtureModule().WithBuildFile(xml)
            .AddSave("s.vsav", new[] { "begin_save", "", Add("p1", type, state), "end_save" });
    }

    private ModuleSession Open(FixtureModule fx) =>
        ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));

    [Fact]
    public void EndToEnd_PackFormulaLedgerSaveLoad()
    {
        using var fx = Fixture();
        var packPath = Path.Combine(_tmp, "e2e.qrules");
        var savePath = Path.Combine(_tmp, "e2e.qsave");

        // 1-4: author a pack, save it, reopen everything
        using (var a = Open(fx))
        {
            a.NewRules("E2E");
            a.UpdateRules(p =>
            {
                p.Properties.Add(new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" });
                p.Properties.Add(new PropertyDef { Id = "divisor", Name = "Divisor", Scope = PropertyScope.Holders, Default = 1 });
                p.Tables.Add(new TableDef
                {
                    Id = "t-rates", Name = "Rates", Kind = TableKind.OneD,
                    RowKeys = new() { "1-2", "3-5", "6+" }, ColumnKeys = new() { "Value" },
                    Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } },
                });
                p.Formulas.Add(new FormulaDef
                {
                    Id = "f-fuel", Name = "Fuel Use", Text = "MP * lookup(Rates, Kind) / Div",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "MP", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "Kind", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "Div", Kind = BindingKind.Global, Property = "divisor" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
                });
                return p;
            });
            a.SaveRules(packPath);
        }

        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        s.OpenRules(packPath);
        var game = s.Game!;
        Assert.Equal(40, Ledger.Get(game.State, "p:p1", "fuel"));
        game.State.Ledgers[LedgerKey.Holder("global")] = new() { ["divisor"] = 2 };

        // 5: typed inputs -> 12 * Rates[4] (=2) / 2 = 12
        var trace = s.Runner!.Run(game.State, new RunRequest("f-fuel", "p1",
            new Dictionary<string, string> { ["MP"] = "12", ["Kind"] = "4" },
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(trace.Error);
        Assert.Equal(12, trace.Result!.Value.Number);
        foreach (var c in s.Runner.Commands(trace, "e2e")) game.Execute(c);

        // 6
        Assert.Equal(28, Ledger.Get(game.State, "p:p1", "fuel"));
        var hist = Ledger.History(game.State, "p:p1", "fuel");
        Assert.Equal(2, hist.Count);
        Assert.Equal(new[] { "seed", "formula" }, hist.Select(h => h.Source));

        // 7: native save/load keeps ledger, history and rules hash
        s.SaveNative(savePath);
        var expectedSha = RulesPackIo.ComputeSha256(s.Rules!);
        Assert.Equal(expectedSha, NativeSave.Read(savePath).RulesSha256);
        s.StartNewGame();
        s.LoadNative(savePath);
        Assert.Equal(28, Ledger.Get(s.Game!.State, "p:p1", "fuel"));
        Assert.Equal(2, Ledger.History(s.Game.State, "p:p1", "fuel").Count);
        Assert.Equal(expectedSha, s.Game.RulesSha256);
        Assert.DoesNotContain(s.Log.Items, d => d.Message.StartsWith("Save was made"));

        // 8: delete the formula, reload -> warning, history shows the deleted formula
        s.UpdateRules(p => { p.Formulas.Clear(); return p; });
        s.SaveRules();
        s.LoadNative(savePath);
        Assert.Contains(s.Log.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Category == "rules"
            && d.Message == "Save was made with a different version of the rules pack");
        var entry = Ledger.History(s.Game!.State, "p:p1", "fuel").Single(h => h.Source == "formula");
        Assert.Contains("deleted formula", Ledger.Describe(entry, s.Rules));
        Assert.Equal(28, Ledger.Get(s.Game.State, "p:p1", "fuel"));
    }
}
