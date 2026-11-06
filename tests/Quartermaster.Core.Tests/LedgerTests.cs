using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class LedgerTests
{
    static GameState S() { var s = new GameState(); s.MapOrder.Add("m1"); return s; }
    static LedgerDelta D(string key, string prop, double b, double a, string src = "manual", string? f = null, string? note = null) =>
        new(key, prop, b, a, src, f, null, note, null);

    [Fact]
    public void LedgerDelta_SetsAfter_AndAppendsHistory()
    {
        var s = S();
        s.Apply(D("p:a", "fuel", 100, 73, "formula", "f1"));
        s.Apply(D("p:a", "fuel", 999, 50));
        Assert.Equal(50, Ledger.Get(s, "p:a", "fuel"));
        Assert.Equal(2, s.LedgerHistory.Count);
        Assert.Equal(1, s.LedgerHistory[0].Seq);
        Assert.Equal(2, s.LedgerHistory[1].Seq);
        Assert.Equal("formula", s.LedgerHistory[0].Source);
        Assert.Equal("f1", s.LedgerHistory[0].FormulaId);
        s.Apply(D("p:a", "fuel", 0, double.NaN));
        s.Apply(D("p:a", "fuel", 0, double.PositiveInfinity));
        Assert.Equal(50, Ledger.Get(s, "p:a", "fuel"));
        Assert.Equal(2, s.LedgerHistory.Count);
        Assert.Null(Ledger.Get(s, "p:a", "nope"));
    }

    [Fact]
    public void LedgerDelta_NegativeAfter_Allowed_FlaggedInHistory()
    {
        var s = S();
        s.Apply(D("h:global", "fuel", 5, -3));
        Assert.Equal(-3, Ledger.Get(s, "h:global", "fuel"));
        Assert.True(Ledger.History(s, "h:global", "fuel")[0].After < 0);
    }

    [Fact]
    public void SeedLedger_OnlyFillsMissing()
    {
        var s = S();
        s.Apply(D("p:a", "fuel", 0, 7));
        s.Apply(new SeedLedger([new LedgerSeed("p:a", "fuel", 100), new LedgerSeed("p:a", "ammo", 20), new LedgerSeed("h:x", "fuel", 1)]));
        Assert.Equal(7, Ledger.Get(s, "p:a", "fuel"));
        Assert.Equal(20, Ledger.Get(s, "p:a", "ammo"));
        Assert.Equal(1, Ledger.Get(s, "h:x", "fuel"));
        Assert.Equal(3, s.LedgerHistory.Count);
        Assert.Equal("seed", s.LedgerHistory[1].Source);
        Assert.Equal(2, s.LedgerHistory[1].Seq);
    }

    [Fact]
    public void RollDice_AppendsDiceHistory()
    {
        var s = S();
        s.Apply(new RollDice("attrition", 2, 6, [3, 5]));
        s.Apply(new RollDice("x", 1, 6, [1]));
        Assert.Equal(2, s.DiceHistory.Count);
        Assert.Equal(2, s.DiceHistory[1].Seq);
        Assert.Equal("attrition", s.DiceHistory[0].Purpose);
        Assert.Equal([3, 5], s.DiceHistory[0].Results);
    }

    [Fact]
    public void RemovePiece_RemovesLedgerKeepsHistory()
    {
        var s = S();
        s.Apply(new AddPiece("a", "t", "", "m1", new PointD(0, 0), null));
        s.Apply(D(LedgerKey.Piece("a"), "fuel", 0, 4));
        s.Apply(D(LedgerKey.Holder("global"), "fuel", 0, 9));
        s.Apply(new RemovePiece("a"));
        Assert.Null(Ledger.Get(s, "p:a", "fuel"));
        Assert.Equal(9, Ledger.Get(s, "h:global", "fuel"));
        Assert.Equal(2, s.LedgerHistory.Count);
        Assert.Single(Ledger.History(s, "p:a", "fuel"));
    }

    [Fact]
    public void Seeder_ReadsBoundSheetField_IgnoresNonNumeric()
    {
        var s = S();
        var type = PieceTypeParser.EncodeState(new[] { "propertysheet;0Name~0Fuel~0Ammo~0Notes;Sheet;", "piece;;;unit.svg;Unit" });
        var type2 = PieceTypeParser.EncodeState(new[] { "propertysheet;0Fuel;Sheet;", "piece;;;unit.svg;Unit" });
        s.Apply(new AddPiece("a", type, "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("b", type, "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("c", type2, "", "m1", new PointD(0, 0), null));
        s.Apply(new SetTraitState("a", 0, "Tank~ 120.5 ~many~hi"));
        s.Apply(new SetTraitState("b", 0, "Tank~x~5~"));
        s.Apply(new SetTraitState("c", 0, "33"));
        var pack = new RulesPack
        {
            Properties =
            {
                new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "fuel" },
                new PropertyDef { Id = "ammo", Name = "Ammo", Scope = PropertyScope.Both, SheetField = "Ammo" },
                new PropertyDef { Id = "h", Name = "H", Scope = PropertyScope.Holders, SheetField = "Fuel" },
                new PropertyDef { Id = "nb", Name = "NoBind", Scope = PropertyScope.Pieces },
            }
        };
        s.Apply(D("p:a", "ammo", 0, 1));
        var seed = LedgerSeeder.BuildSeeds(s, pack, p => p.Id != "b")!;
        Assert.Equal(
            [("p:a", "fuel", 120.5), ("p:c", "fuel", 33)],
            seed.Seeds.Select(x => (x.Key, x.PropertyId, x.Value)).ToArray());
        Assert.Null(LedgerSeeder.BuildSeeds(s, pack, _ => false));
        s.Apply(seed);
        Assert.Null(LedgerSeeder.BuildSeeds(s, pack, p => p.Id != "b"));
    }

    [Fact]
    public void Seeder_ShortSheetState_AndMalformedType_SkipsAndCompletes()
    {
        var s = S();
        var sheet = PieceTypeParser.EncodeState(new[] { "propertysheet;0Name~0Fuel;Sheet;", "piece;;;unit.svg;Unit" });
        s.Apply(new AddPiece("short", sheet, "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("bad", ";;;;;(((", "", "m1", new PointD(0, 0), null));
        s.Apply(new AddPiece("ok", sheet, "", "m1", new PointD(0, 0), null));
        s.Apply(new SetTraitState("ok", 0, "Tank~7"));
        var pack = new RulesPack { Properties = { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" } } };
        var log = new DiagnosticLog();
        var seed = LedgerSeeder.BuildSeeds(s, pack, _ => true, null, log);
        Assert.Contains(("p:ok", "fuel", 7.0), seed!.Seeds.Select(x => (x.Key, x.PropertyId, x.Value)));
        Assert.DoesNotContain(seed.Seeds, x => x.Key == "p:short");
    }

    [Fact]
    public void NativeSave_LedgerAndRulesHash_RoundTrip()
    {
        var sess = new GameSession(S(), "mod") { RulesSha256 = "rules1" };
        sess.Execute(D("h:global", "fuel", 0, 12.5, "formula", "f1", null));
        sess.Execute(new SeedLedger([new LedgerSeed("p:a", "ammo", 3)]));
        sess.Execute(new RollDice("p", 2, 6, [2, 6]));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            Assert.Contains("\"format\":2", File.ReadAllText(path).Replace(" ", ""));
            var back = NativeSave.Read(path);
            Assert.Equal("rules1", back.RulesSha256);
            Assert.Equal(12.5, Ledger.Get(back.State, "h:global", "fuel"));
            Assert.Equal(2, back.State.LedgerHistory.Count);
            Assert.Equal([2, 6], back.State.DiceHistory[0].Results);
            Assert.IsType<RollDice>(back.Log[2]);
            Assert.Equal(JsonSerializer.Serialize(sess.State, QuartermasterJson.Options), JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
            File.WriteAllText(path, "{\"format\":2,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[],\"ledgers\":null,\"ledgerHistory\":[],\"diceHistory\":[]},\"log\":[]}");
            Assert.Throws<InvalidDataException>(() => NativeSave.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_ReadsFormat1_EmptyLedgers()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, "{\"format\":1,\"moduleSha256\":\"x\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[\"m\"]},\"log\":[]}");
            var back = NativeSave.Read(path);
            Assert.Null(back.RulesSha256);
            Assert.Empty(back.State.Ledgers);
            Assert.Empty(back.State.LedgerHistory);
            Assert.Empty(back.State.DiceHistory);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Describe_DeletedFormula()
    {
        var pack = new RulesPack
        {
            Properties = { new PropertyDef { Id = "fuel", Name = "Fuel" } },
            Formulas = { new FormulaDef { Id = "f1", Name = "Fuel Use" } },
        };
        var e = new LedgerEntry(1, "p:a", "fuel", 100, 73, "formula", "f1", null, null, null);
        Assert.Equal("−27 Fuel (Fuel Use)", Ledger.Describe(e, pack));
        Assert.Contains("(deleted formula)", Ledger.Describe(e with { FormulaId = "gone" }, pack));
        Assert.Contains("(deleted formula)", Ledger.Describe(e, null));
        Assert.Equal("+2.5 Fuel (manual: oops)", Ledger.Describe(e with { Before = 1, After = 3.5, Source = "manual", Note = "oops" }, pack));
        Assert.Equal("0 (deleted property) (seed)", Ledger.Describe(e with { PropertyId = "zz", After = 100, Source = "seed" }, pack));
    }

    [Fact]
    public void Commands_JsonDiscriminators()
    {
        GameCommand[] cs = [D("p:a", "f", 1, 2), new SeedLedger([new LedgerSeed("p:a", "f", 1)]), new RollDice("x", 1, 6, [4])];
        var json = JsonSerializer.Serialize(cs, QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"ledgerDelta\"", json);
        Assert.Contains("\"$t\":\"seedLedger\"", json);
        Assert.Contains("\"$t\":\"rollDice\"", json);
        var back = JsonSerializer.Deserialize<GameCommand[]>(json, QuartermasterJson.Options)!;
        Assert.IsType<LedgerDelta>(back[0]); Assert.IsType<SeedLedger>(back[1]); Assert.IsType<RollDice>(back[2]);
        Assert.Equal(4, ((RollDice)back[2]).Results[0]);
    }

    [Fact]
    public void LedgerKey_Parse()
    {
        Assert.True(LedgerKey.TryParse("p:abc", out var ip, out var id)); Assert.True(ip); Assert.Equal("abc", id);
        Assert.True(LedgerKey.TryParse("h:global", out ip, out id)); Assert.False(ip);
        Assert.False(LedgerKey.TryParse("zzz", out _, out _));
    }
}
