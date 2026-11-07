using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class BannerTests
{
    private static PhaseView View(bool noPhases = false, bool before = false, string? side = null, string name = "Supply", string instr = "Check fuel. Then move.", int decisions = 0) =>
        new(noPhases, before, 1, null, side, new List<PhaseChip>(), noPhases || before ? null : new PhaseDef { Id = "p", Name = name, Instructions = instr },
            null, Enumerable.Range(0, decisions).Select(i => new Decision(DecisionKind.Warning, "d" + i, i, null, new List<string>())).ToList(),
            new List<WarningHit>(), null);

    [Fact]
    public void Banner_BeforeStart() => Assert.Equal(("Press Start game ▶ to begin turn 1.", (string?)null), Banner.For(View(before: true)));

    [Fact]
    public void Banner_NoPhases() => Assert.Equal(("No phases defined — free play.", (string?)null), Banner.For(View(noPhases: true)));

    [Fact]
    public void Banner_InPhase_WithSideAndDecisions()
    {
        var (text, tip) = Banner.For(View(side: "Red", decisions: 2));
        Assert.Equal("Now: Red player — Supply: Check fuel. · 2 decisions left", text);
        Assert.Equal("Check fuel. Then move.", tip);
    }

    [Fact]
    public void Banner_OtherSidePlaying_NoInstructionsOrDecisions()
    {
        var (text, tip) = Banner.For(View(side: "Blue") with { PlayingText = "Blue is playing Supply" });
        Assert.Equal("Now: Blue is playing Supply", text);
        Assert.Null(tip);
    }

    [Fact]
    public void Banner_NoSide()
    {
        Assert.Equal("Now: Supply: Check fuel.", Banner.For(View()).Text);
        Assert.Equal("Now: Supply", Banner.For(View(instr: "")).Text);
        Assert.Null(Banner.For(View(instr: "")).Tooltip);
    }

    [Fact]
    public void Banner_LongInstruction_Truncated()
    {
        var long1 = new string('x', 200) + ". tail";
        var (text, tip) = Banner.For(View(instr: long1));
        Assert.Equal("Now: Supply: " + new string('x', 119) + "…", text);
        Assert.Equal(long1, tip);
    }
}
