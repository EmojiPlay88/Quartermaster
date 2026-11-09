using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Formula window, batch grid and missing-input rules shared by both apps. Invented pack from PhaseFixture; temp roots only.</summary>
public class FormulaWindowRulesTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-fwr-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public FormulaWindowRulesTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static BatchRowView Row(bool applied = false, string result = "", string exceptions = "", params bool[] wrong) =>
        new("r1", "Red 1", wrong.Select(_ => "1").ToList(), wrong, result, "ledger", exceptions, applied, false);

    [Fact]
    public void Key_dedupes_same_formula_piece_run()
    {
        Assert.Equal(FormulaWindowRules.Key("f", "p", "r"), FormulaWindowRules.Key("f", "p", "r"));
        Assert.NotEqual(FormulaWindowRules.Key("f", "p", "r"), FormulaWindowRules.Key("f", "p", null));
        Assert.NotEqual(FormulaWindowRules.Key("f", "p", "r"), FormulaWindowRules.Key("f", null, "r"));
        Assert.NotEqual(FormulaWindowRules.Key("f", "p", "r"), FormulaWindowRules.Key("g", "p", "r"));
        Assert.Equal("Fuel: Red 1", FormulaWindowRules.Title(new FormulaDef { Id = "f", Name = "Fuel" }, "Red 1"));
        Assert.Equal("Fuel", FormulaWindowRules.Title(new FormulaDef { Id = "f", Name = "Fuel" }, null));
    }

    [Fact]
    public void Mode_per_difficulty()
    {
        Assert.Equal((true, false, true), FormulaWindowRules.Mode(Difficulty.Hardcore));
        Assert.Equal((true, true, true), FormulaWindowRules.Mode(Difficulty.SemiAuto));
        Assert.Equal((false, false, false), FormulaWindowRules.Mode(Difficulty.FullyAuto));
    }

    [Fact]
    public void Refusal_for_other_side_unit()
    {
        var (s, _) = Make(Difficulty.FullyAuto);
        Assert.Null(FormulaWindowRules.Refusal(s, PhaseFixture.RedOne));
        s.SetViewSides(new HashSet<string> { "blue" });
        Assert.Equal("That unit belongs to a side you don't hold.", FormulaWindowRules.Refusal(s, PhaseFixture.RedOne));
        Assert.Null(FormulaWindowRules.Refusal(s, PhaseFixture.BlueOne));
    }

    [Fact]
    public void Should_close_when_run_no_longer_pending_or_piece_gone()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(130, 50), null) });
        var run = s.Game!.State.Pending.Single();
        Assert.False(FormulaWindowRules.ShouldClose(s, c, PhaseFixture.RedOne, run.Id));
        Assert.False(FormulaWindowRules.ShouldClose(s, null, PhaseFixture.RedOne, "unknown"));   // no controller: no run check
        Assert.True(FormulaWindowRules.ShouldClose(s, c, PhaseFixture.RedOne, "unknown"));
        Assert.True(FormulaWindowRules.ShouldClose(s, c, "gone", null));
        Assert.False(FormulaWindowRules.ShouldClose(s, c, null, null));
    }

    [Fact]
    public void Batch_cell_applied_and_wrong_tones()
    {
        var wrong = Row(false, "5", "", true, false);
        Assert.Equal(CellTone.Wrong, FormulaWindowRules.VariableCell(wrong, 0).Tone);
        Assert.Equal(CellTone.Normal, FormulaWindowRules.VariableCell(wrong, 1).Tone);
        var done = Row(true, "", "oops", false);
        Assert.Equal(new BatchCell("Applied", CellTone.Applied), FormulaWindowRules.CellOf(done, "Result"));
        Assert.Equal(CellTone.Applied, FormulaWindowRules.VariableCell(done, 0).Tone);
        Assert.Equal(CellTone.Applied, FormulaWindowRules.CellOf(done, "Exceptions").Tone);   // applied rows are grey, not red
        Assert.Equal("7", FormulaWindowRules.CellOf(Row(true, "7"), "Result").Text);
        var bad = Row(false, "", "no fuel", false);
        Assert.Equal(new BatchCell("no fuel", CellTone.Exception), FormulaWindowRules.CellOf(bad, "Exceptions"));
        Assert.Equal("Red 1", FormulaWindowRules.CellOf(bad, "Unit").Text);
        Assert.Equal("ledger", FormulaWindowRules.CellOf(bad, "Ledger").Text);
    }

    [Fact]
    public void Batch_status_texts_match_winforms()
    {
        Assert.Equal("Calculated 3 row(s).", FormulaWindowRules.CalculatedText(3));
        Assert.Equal("Applied 2 row(s); rows with a red cell were skipped.", FormulaWindowRules.AppliedRowsText(2));
        Assert.Equal("Nothing applied: check the red cells, or press Calculate all.", FormulaWindowRules.NothingAppliedText);
        Assert.Equal("Select a row first.", FormulaWindowRules.SelectRowText);
        Assert.Equal("Row applied.", FormulaWindowRules.RowAppliedText);
        Assert.Equal("That row does not match yet: fix the red cells or press Calculate all.", FormulaWindowRules.RowNotMatchingText);
        Assert.Equal("Asking the host…", FormulaWindowRules.AskingHostText);
        Assert.Equal("The host calculated 4 row(s).", FormulaWindowRules.HostCalculatedText(4));
        Assert.Equal("The host applied 1 of 4 row(s).", FormulaWindowRules.HostAppliedText(1, 4));
    }

    [Fact]
    public void Missing_head_texts_singular_plural()
    {
        Assert.Equal("Fuel needs a value for 2 unit(s). The answer is used for every unit of this run.", FormulaWindowRules.MissingHead("Fuel", 2, 1));
        Assert.Equal("Fuel needs values for 2 unit(s). The answer is used for every unit of this run.", FormulaWindowRules.MissingHead("Fuel", 2, 3));
        Assert.Equal("Fuel could not be calculated automatically. Use Per unit… to work through the 2 unit(s).", FormulaWindowRules.MissingHead("Fuel", 2, 0));
        Assert.Equal(FormulaWindowRules.MissingHead("Fuel", 2, 0), FormulaWindowRules.MissingFailed("Fuel", 2));
        Assert.Equal("1 applied; 2 failed. ", FormulaWindowRules.MissingResult(1, 2));
        Assert.Equal("1 applied; 4 failed. a; b; c…", FormulaWindowRules.MissingFailureLine(1, new[] { "a", "b", "c", "d" }));
        Assert.Equal("0 applied; 1 failed. a", FormulaWindowRules.MissingFailureLine(0, new[] { "a" }));
    }

    [Fact]
    public void Missing_ok_needs_all_answers_in_range()
    {
        var inputs = new[] { new MissingInput("A", null, 1, 5), new MissingInput("B", new[] { "x", "y" }, null, null) };
        Assert.False(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?>(), inputs));
        Assert.False(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?> { ["A"] = "3", ["B"] = " " }, inputs));
        Assert.True(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?> { ["A"] = "3", ["B"] = "x" }, inputs));
        Assert.False(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?> { ["A"] = "9", ["B"] = "x" }, inputs));
        Assert.False(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?> { ["A"] = "0", ["B"] = "x" }, inputs));
        Assert.True(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?> { ["A"] = "9", ["B"] = "x" }, inputs, checkRange: false));
        Assert.False(FormulaWindowRules.MissingOkEnabled(new Dictionary<string, string?>(), Array.Empty<MissingInput>()));
    }
}
