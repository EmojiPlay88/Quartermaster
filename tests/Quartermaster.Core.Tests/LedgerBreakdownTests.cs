using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class LedgerBreakdownTests
{
    const string M = "−";

    static RulesPack Pack() => new()
    {
        Properties = { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Both, Default = 0 } },
        Holders = { new HolderDef { Id = "depot", Name = "Depot" } },
        Formulas = { new FormulaDef { Id = "f1", Name = "Fuel Use", RuleRefId = "r1" }, new FormulaDef { Id = "f2", Name = "Burn" } },
        RuleRefs = { new RuleRef { Id = "r1", Label = "Rule 4.2" } },
    };

    static GameState S() { var s = new GameState(); s.MapOrder.Add("m1"); return s; }
    static LedgerDelta D(string key, double b, double a, string src = "manual", string? f = null, string? rule = null, string? note = null) =>
        new(key, "fuel", b, a, src, f, rule, note, null);

    [Fact]
    public void Reducer_FillsTurnAndPhase()
    {
        var s = S();
        s.Apply(new SetTurn(2, "Supply A"));
        s.Apply(D("h:depot", 0, 5));
        s.Apply(new SeedLedger([new LedgerSeed("p:a", "fuel", 9)]));
        Assert.All(s.LedgerHistory, e => { Assert.Equal(2, e.Turn); Assert.Equal("Supply A", e.Phase); });
        Assert.Equal(2, s.LedgerHistory.Count);
    }

    static GameState Scripted()
    {
        var s = S();
        s.Apply(new SeedLedger([new LedgerSeed("h:depot", "fuel", 100)]));            // turn 1
        s.Apply(D("h:depot", 100, 90, "formula", "f2"));                              // turn 1
        s.Apply(new SetTurn(2, "Supply A"));
        s.Apply(D("h:depot", 90, 87, "manual", null, null, "typo fix"));              // turn 2
        s.Apply(new SetTurn(3, "Move"));
        s.Apply(D("h:depot", 87, 60, "formula", "f1", "r1"));                         // turn 3
        s.Apply(D("h:depot", 60, 61.5, "formula", "f2"));                             // turn 3
        return s;
    }

    [Fact]
    public void Breakdown_CurrentTurnLines_StartOfTurn_EarlierNet()
    {
        var b = LedgerBreakdown.For(Scripted(), Pack(), null, "h:depot", "fuel");
        Assert.Equal("Fuel 61.5  (Depot)", b.Title);
        var expected = string.Join("\n",
            "Fuel 61.5  (Depot)",
            "      Earlier turns: net +87",
            "87    Start of turn 3",
            M + "27   Fuel Use · Rule 4.2  Turn 3 · Move",
            "+1.5  Burn  Turn 3 · Move");
        Assert.Equal(expected, LedgerBreakdown.Format(b));
    }

    [Fact]
    public void Breakdown_ManualAndSeedLabels()
    {
        var s = S();
        s.Apply(new SeedLedger([new LedgerSeed("h:depot", "fuel", 100)]));
        s.Apply(D("h:depot", 100, 97, note: "oops"));
        var b = LedgerBreakdown.For(s, Pack(), null, "h:depot", "fuel");
        Assert.Equal(["Start of turn 1", "seeded", "manual: oops"], b.Lines.Select(l => l.Label));
        Assert.Equal("+100", b.Lines[1].Amount);
        Assert.Equal(M + "3", b.Lines[2].Amount);
        Assert.Equal("Turn 1", b.Lines[1].Where);
    }

    [Fact]
    public void Breakdown_MoreThan12_Collapses()
    {
        var s = S();
        for (int i = 0; i < 15; i++) s.Apply(D("h:global", i, i + 1));
        var b = LedgerBreakdown.For(s, Pack(), null, "h:global", "fuel");
        Assert.Equal("Fuel 15  (Global)", b.Title);
        Assert.Equal(1 + 1 + 12, b.Lines.Count);
        Assert.Equal("Start of turn 1", b.Lines[0].Label);
        Assert.Equal("… 3 more changes", b.Lines[1].Label);
        Assert.Equal("+3", b.Lines[1].Amount);
        Assert.Equal("+1", b.Lines[2].Amount);
    }

    [Fact]
    public void Breakdown_MoreThan12_HiddenNetIsSignedAndSumsUp()
    {
        var s = S();
        for (int i = 0; i < 14; i++) s.Apply(D("h:global", i * 10, i * 10 - 5)); // each -5
        var b = LedgerBreakdown.For(s, Pack(), null, "h:global", "fuel");
        Assert.Equal(M + "10", b.Lines[1].Amount);
    }

    [Fact]
    public void ManualWithoutNote_RendersJustManual()
    {
        var s = S();
        s.Apply(D("h:depot", 0, 2, note: null));
        s.Apply(D("h:depot", 2, 3, note: ""));
        var b = LedgerBreakdown.For(s, Pack(), null, "h:depot", "fuel");
        Assert.Equal(["Start of turn 1", "manual", "manual"], b.Lines.Select(l => l.Label));
        Assert.Equal("+2 Fuel (manual)", Ledger.Describe(s.LedgerHistory[0], Pack()));
        Assert.Equal("manual", Ledger.SourceLabel(s.LedgerHistory[1], Pack()));
    }

    [Fact]
    public void Breakdown_NullTurnEntries_CountAsEarlier()
    {
        var s = S();
        s.LedgerHistory.Add(new LedgerEntry(1, "h:depot", "fuel", 0, 10, "manual", null, null, "x", null));
        s.Ledgers["h:depot"] = new() { ["fuel"] = 10 };
        s.Apply(D("h:depot", 10, 12));
        var b = LedgerBreakdown.For(s, Pack(), null, "h:depot", "fuel");
        Assert.Equal("Earlier turns: net +10", b.Lines[0].Label);
        Assert.Equal("Start of turn 1", b.Lines[1].Label);
        Assert.Equal("10", b.Lines[1].Amount);
        Assert.Equal(3, b.Lines.Count);
    }

    [Fact]
    public void Breakdown_RemovedPiece_UsesKeyFallbackName()
    {
        var s = S();
        s.Apply(D("p:gone", 0, 4));
        var b = LedgerBreakdown.For(s, Pack(), null, "p:gone", "fuel");
        Assert.Equal("Fuel 4  (gone)", b.Title);
    }

    [Fact]
    public void Breakdown_NoHistory()
    {
        var b = LedgerBreakdown.For(S(), Pack(), null, "h:depot", "fuel");
        Assert.Equal(["no changes recorded"], b.Lines.Select(l => l.Label));
    }

    [Fact]
    public void ForTotal_SortsAndTruncates()
    {
        var parts = Enumerable.Range(1, 14).Select(i => ($"u{i}", i % 2 == 0 ? -(double)i : i)).ToList();
        var b = LedgerBreakdown.ForTotal("Fuel 7  (total)", parts);
        Assert.Equal("Fuel 7  (total)", b.Title);
        Assert.Equal(13, b.Lines.Count);
        Assert.Equal("u14", b.Lines[0].Label);
        Assert.Equal(M + "14", b.Lines[0].Amount);
        Assert.Equal("u13", b.Lines[1].Label);
        Assert.Equal("+13", b.Lines[1].Amount);
        Assert.Equal("… 2 more", b.Lines[12].Label);
    }

    static string Tmp() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");

    static GameSession Played()
    {
        var sess = new GameSession(S(), "h");
        sess.Execute(new SetTurn(2, "Supply A"));
        sess.Execute(D("h:depot", 0, 5));
        sess.Execute(D("h:depot", 5, 7));
        return sess;
    }

    [Fact]
    public void Format5Save_KeepsUndo_EntriesGainTurn()
    {
        var path = Tmp();
        try
        {
            NativeSave.Write(path, Played());
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            root["format"] = 5;
            foreach (var e in root["snapshot"]!["ledgerHistory"]!.AsArray()) { e!.AsObject().Remove("turn"); e.AsObject().Remove("phase"); }
            File.WriteAllText(path, root.ToJsonString());
            var back = NativeSave.Read(path);
            Assert.True(back.CanUndo);
            Assert.False(back.RebasedOnLoad);
            Assert.All(back.State.LedgerHistory, e => Assert.Equal(2, e.Turn));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Format6_RoundTrip()
    {
        var path = Tmp();
        try
        {
            var sess = Played();
            NativeSave.Write(path, sess);
            Assert.Equal(6, NativeSave.FormatVersion);
            var back = NativeSave.Read(path);
            Assert.True(back.CanUndo);
            Assert.False(back.RebasedOnLoad);
            Assert.Equal(JsonSerializer.Serialize(sess.State, QuartermasterJson.Options), JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
            Assert.Equal("Supply A", back.State.LedgerHistory[0].Phase);
        }
        finally { File.Delete(path); }
    }
}
