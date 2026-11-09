using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>Phase strip, phase panel, Next Phase and Step Back texts. Invented pack from PhaseFixture; temp roots only.</summary>
public class PhaseTextsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phasetexts-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseTextsTests() => Directory.CreateDirectory(_tmp);

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
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        return (s, new PhaseController(s, (cmd, _) => cmd));
    }

    private static PhaseView View(IReadOnlyList<Decision>? decisions = null, string? playing = null, string? side = "Red", string? next = "Combat") =>
        new(false, false, 2, "Round 1", side,
            new[] { new PhaseChip("a", "Weather", ChipState.Done), new PhaseChip("b", "Movement", ChipState.Current, "Move notes"), new PhaseChip("c", "Combat", ChipState.Upcoming) },
            new PhaseDef { Id = "b", Name = "Movement", Checklist = new() { "Check depot stores" } }, next,
            decisions ?? Array.Empty<Decision>(), Array.Empty<WarningHit>(), null, playing);

    private static Decision Check(int i) => new(DecisionKind.Checklist, "Item " + i, i, null, Array.Empty<string>());

    [Fact]
    public void Strip_texts_before_start_and_in_turn()
    {
        var (_, c) = Make(Difficulty.FullyAuto);
        var before = PhaseTexts.Strip(c.View());
        Assert.Equal(new[] { "Before turn 1", "Weather", "Supply", "Movement", "Combat", "End" }, before.Select(x => x.Text));
        Assert.Null(before[0].State);
        Assert.All(before.Skip(1), x => Assert.Equal(ChipState.Upcoming, x.State));

        c.Start();
        c.Advance();   // Supply, Red's phase
        var turn = PhaseTexts.Strip(c.View());
        Assert.Equal("Turn 1", turn[0].Text);
        Assert.Null(turn[0].State);
        var cur = Assert.Single(turn, x => x.State == ChipState.Current);
        Assert.Equal("Red player — Supply", cur.Text);
        Assert.Equal(ChipState.Done, turn.Single(x => x.Text == "Weather").State);

        var staged = PhaseTexts.Strip(View());
        Assert.Equal(new[] { "Turn 2", "Round 1", "Weather", "Red player — Movement", "Combat" }, staged.Select(x => x.Text));
        Assert.Equal("Move notes", staged[3].Tooltip);
        Assert.Equal("Blue is playing Movement", PhaseTexts.Strip(View(playing: "Blue is playing Movement"))[3].Text);
        Assert.Equal("Movement", PhaseTexts.Strip(View(side: ""))[3].Text);

        var none = Assert.Single(PhaseTexts.Strip(new PhaseView(true, false, 1, null, null, Array.Empty<PhaseChip>(), null, null,
            Array.Empty<Decision>(), Array.Empty<WarningHit>(), null)));
        Assert.Equal(PhaseTexts.NoPhasesText, none.Text);
        Assert.Equal("No phases defined — add them in the Rules Workbench (Phases tab)", PhaseTexts.NoPhasesText);
    }

    [Fact]
    public void Panel_next_text_includes_next_phase_name()
    {
        var (_, c) = Make(Difficulty.FullyAuto);
        var before = PhaseTexts.Panel(c.View(), null);
        Assert.Equal("Start game ▶", before.NextText);
        Assert.Equal(PhaseTexts.StartGameText, before.NextText);
        Assert.Equal("Before turn 1", before.Title);
        Assert.True(before.NextEnabled);

        c.Start();
        var weather = PhaseTexts.Panel(c.View(), null);
        Assert.Equal("Next Phase ▶ Supply", weather.NextText);
        Assert.Equal("Weather", weather.Title);
        Assert.True(weather.NextEnabled);
        Assert.False(PhaseTexts.Panel(c.View(), null, replaying: true).NextEnabled);

        var last = PhaseTexts.Panel(View(next: null), null);
        Assert.Equal("Next Phase ▶", last.NextText);
        Assert.False(last.NextEnabled);
        Assert.Equal("Red player — Movement", last.Title);
    }

    [Fact]
    public void Panel_count_text_singular_and_plural()
    {
        var none = PhaseTexts.Panel(View(), null);
        Assert.Equal("0 decisions left", none.CountText);
        Assert.False(none.CountRed);
        var one = PhaseTexts.Panel(View(new[] { Check(0) }), null);
        Assert.Equal("1 decision left", one.CountText);
        Assert.True(one.CountRed);
        Assert.Equal("2 decisions left", PhaseTexts.Panel(View(new[] { Check(0), Check(1) }), null).CountText);
    }

    [Fact]
    public void Checklist_hidden_in_other_sides_phase_text()
    {
        var rules = new RulesPack { RuleRefs = new() { new RuleRef { Id = "rr", Label = "Movement", DocumentEntry = "rules.pdf", Page = 12 } } };
        var v = View() with { Current = new PhaseDef { Id = "b", Name = "Movement", RuleRefId = "rr" } };
        var mine = PhaseTexts.Panel(v, "red", rules: rules);
        Assert.False(mine.ChecklistHidden);
        Assert.Equal("Rule: Movement (p. 12)", mine.RuleText);

        var theirs = PhaseTexts.Panel(v with { PlayingText = "Blue is playing Movement" }, "red", rules: rules);
        Assert.True(theirs.ChecklistHidden);
        Assert.Equal("Blue is playing Movement", theirs.Title);
        Assert.False(theirs.NextEnabled);
        Assert.Null(theirs.RuleText);
        Assert.Equal("Rule: Movement", PhaseTexts.RuleLinkText(new RuleRef { Label = "Movement" }));
    }

    [Fact]
    public void Warning_lines_and_ack_rule()
    {
        var hits = new[]
        {
            new WarningHit(0, "Out of fuel {unit}", new[] { "r1" }, new[] { "Out of fuel Red 1" }, true, false),
            new WarningHit(1, "Low water", new[] { "r1", "r2" }, new[] { "Red 1", "Red 2" }, true, true),
            new WarningHit(2, "Spread out", new[] { "r1", "r2" }, new[] { "Red 1", "Red 2" }, false, false),
        };
        var lines = PhaseTexts.Panel(View() with { Warnings = hits }, null).Warnings;
        Assert.Equal(new[] { "Out of fuel — 1 unit (decision)", "Low water — 2 units (acknowledged)", "Spread out — 2 units" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { true, false, false }, lines.Select(l => l.CanAck));
        Assert.Equal(new[] { 0, 1, 2 }, lines.Select(l => l.Index));
        Assert.False(PhaseTexts.CanAck(hits[0], replaying: true));
    }

    [Fact]
    public void Next_phase_head_singular_and_plural()
    {
        Assert.Equal("1 decision is still open in this phase:", PhaseTexts.NextPhaseHead(1));
        Assert.Equal("3 decisions are still open in this phase:", PhaseTexts.NextPhaseHead(3));
    }

    [Fact]
    public void Step_back_cells_and_confirm_text()
    {
        var at = new DateTimeOffset(2026, 11, 9, 14, 5, 0, TimeSpan.Zero);
        var row = new CheckpointRow(3, 2, "Round 1", "Red", "Supply", at, 4);
        Assert.Equal(new[] { "2", "Round 1", "Red", "Supply", at.ToLocalTime().ToString("HH:mm"), "4" }, PhaseTexts.StepBackCells(row));
        Assert.Equal(new[] { "1", "", "", "Turn 1", "", "0" }, PhaseTexts.StepBackCells(new CheckpointRow(0, 1, null, null, "Turn 1", null, 0)));
        Assert.Equal(new[] { "Turn", "Stage", "Side", "Phase", "Reached", "Actions" }, PhaseTexts.StepBackColumns);

        Assert.Equal("Turn 2 · Supply", PhaseTexts.StepBackWhere(row));
        Assert.Equal("Turn 1", PhaseTexts.StepBackWhere(new CheckpointRow(0, 1, null, null, "Turn 1", null, 0)));
        Assert.Equal("Step back to the start of Turn 2 · Supply?" + Environment.NewLine + "This undoes everything done since then. You can redo it afterwards.",
            PhaseTexts.StepBackConfirm(row));
    }
}
