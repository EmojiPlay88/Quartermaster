using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class TraitStateTests
{
    private static LayerDef Layer(bool always = true, int n = 3) => new("emb2")
    {
        Images = Enumerable.Range(0, n).Select(i => $"i{i}.png").ToArray(),
        LevelNames = Enumerable.Range(0, n).Select(i => $"L{i}").ToArray(),
        AlwaysActive = always,
    };

    [Theory]
    [InlineData("2", 2, true)]
    [InlineData("-2", 2, false)]
    [InlineData("1", 1, true)]
    [InlineData("9", 3, true)]
    [InlineData("-9", 3, false)]
    public void LayerLevel_SignedEncoding(string state, int level, bool active)
        => Assert.Equal((level, active), TraitState.LayerLevel(Layer(), state));

    [Fact]
    public void LayerLevel_DefaultDependsOnAlwaysActive()
    {
        Assert.Equal((1, true), TraitState.LayerLevel(Layer(true), ""));
        Assert.Equal((1, false), TraitState.LayerLevel(Layer(false), ""));
    }

    [Fact]
    public void RotationAngle_FixedFacingsAreClockwiseIndices()
    {
        var d = new RotatorDef("rotate") { ValidAngles = 4 };
        Assert.Equal(90, TraitState.RotationAngle(d, "1"));
        Assert.Equal(270, TraitState.RotationAngle(d, "3"));
        Assert.Equal(270, TraitState.RotationAngle(d, "9"));
        Assert.Equal(0, TraitState.RotationAngle(d, ""));
    }

    [Fact]
    public void RotationAngle_FreeNegatesVassalAngle()
    {
        var d = new RotatorDef("rotate") { ValidAngles = 1 };
        Assert.Equal(-30, TraitState.RotationAngle(d, "30.0"));
        Assert.Equal(0, TraitState.RotationAngle(d, "junk"));
    }

    [Fact]
    public void IsHidden_NullMeansVisible()
    {
        var d = new HideableDef("hide");
        Assert.False(TraitState.IsHidden(d, "null"));
        Assert.False(TraitState.IsHidden(d, ""));
        Assert.True(TraitState.IsHidden(d, "Player 1"));
    }

    [Fact]
    public void IsObscured_ReadsFirstToken()
    {
        var d = new ObscurableDef("obs");
        Assert.False(TraitState.IsObscured(d, "null;"));
        Assert.False(TraitState.IsObscured(d, ""));
        Assert.True(TraitState.IsObscured(d, "Side;P"));
    }

    [Fact]
    public void LabelText_TrimsAndStripsSelfReference()
    {
        var d = new LabelDef("label") { Name = "TextLabel" };
        Assert.Equal("Hi", TraitState.LabelText(d, "  Hi "));
        Assert.Equal("ab", TraitState.LabelText(d, "a$TextLabel$b"));
        Assert.Equal("", TraitState.LabelText(d, ""));
    }

    [Fact]
    public void LayerSetLevel_RoundTripsWithLayerLevel()
    {
        var d = Layer(false);
        Assert.Equal((2, true), TraitState.LayerLevel(d, TraitState.LayerSetLevel(d, "", 2, true)));
        Assert.Equal((3, false), TraitState.LayerLevel(d, TraitState.LayerSetLevel(d, "", 3, false)));
        Assert.Equal((3, true), TraitState.LayerLevel(d, TraitState.LayerSetLevel(d, "", 9, true)));
    }

    [Fact]
    public void LayerStep_LoopsOrClamps()
    {
        var loop = Layer();
        Assert.Equal((1, true), TraitState.LayerLevel(loop, TraitState.LayerStep(loop, "3", +1)));
        Assert.Equal((3, true), TraitState.LayerLevel(loop, TraitState.LayerStep(loop, "1", -1)));
        var flat = Layer() with { Loop = false };
        Assert.Equal((3, true), TraitState.LayerLevel(flat, TraitState.LayerStep(flat, "3", +1)));
        Assert.Equal((1, true), TraitState.LayerLevel(flat, TraitState.LayerStep(flat, "1", -1)));
        Assert.Equal((3, false), TraitState.LayerLevel(flat, TraitState.LayerStep(flat, "-2", +1)));
    }

    [Fact]
    public void LayerToggleAndReset()
    {
        var d = Layer(false) with { ResetLevel = 2 };
        Assert.Equal((1, true), TraitState.LayerLevel(d, TraitState.LayerToggleActive(d, "")));
        Assert.Equal((2, true), TraitState.LayerLevel(d, TraitState.LayerReset(d, "-3")));
        var neg = d with { ResetLevel = -2 };
        Assert.Equal((2, false), TraitState.LayerLevel(neg, TraitState.LayerReset(neg, "3")));
    }

    [Theory]
    [InlineData("", 1, 90.0)]
    [InlineData("3", 1, 0.0)]
    [InlineData("0", -1, 270.0)]
    public void RotationStep_FixedFacings(string state, int delta, double expected)
    {
        var d = new RotatorDef("rotate") { ValidAngles = 4 };
        Assert.Equal(expected, TraitState.RotationAngle(d, TraitState.RotationStep(d, state, delta)));
    }

    [Fact]
    public void RotationStep_FreeRotationUnchanged()
        => Assert.Equal("45", TraitState.RotationStep(new RotatorDef("rotate") { ValidAngles = 1 }, "45", 1));

    [Fact]
    public void HiddenAndObscuredToggle_RoundTrip()
    {
        var h = new HideableDef("hide");
        var on = TraitState.HiddenToggle(h, "", "local");
        Assert.True(TraitState.IsHidden(h, on));
        Assert.False(TraitState.IsHidden(h, TraitState.HiddenToggle(h, on, "local")));
        var o = new ObscurableDef("obs");
        var oOn = TraitState.ObscuredToggle(o, "null;", "local");
        Assert.True(TraitState.IsObscured(o, oOn));
        Assert.False(TraitState.IsObscured(o, TraitState.ObscuredToggle(o, oOn, "local")));
    }

    [Fact]
    public void DynamicIncrement_ClampsWrapsAndTolerates()
    {
        var clamp = new DynamicPropertyDef("PROP") { Numeric = true, Min = 0, Max = 5 };
        Assert.Equal("5", TraitState.DynamicIncrement(clamp, "4", 3));
        Assert.Equal("0", TraitState.DynamicIncrement(clamp, "1", -3));
        Assert.Equal("1", TraitState.DynamicIncrement(clamp, "", 1));
        var wrap = clamp with { Wrap = true };
        Assert.Equal("1", TraitState.DynamicIncrement(wrap, "5", 2));
        Assert.Equal("5", TraitState.DynamicIncrement(wrap, "0", -1));
        Assert.Equal("abc", TraitState.DynamicIncrement(clamp, "abc", 1));
        Assert.Equal("12", TraitState.DynamicIncrement(new DynamicPropertyDef("PROP"), "10", 2));
        Assert.Equal("x", TraitState.DynamicValue(clamp, TraitState.DynamicSet(clamp, "x")));
    }

    [Fact]
    public void LayerReset_LevelZero_ClampsToInactiveLevelOne()
    {
        var d = Layer(false) with { ResetLevel = 0 };
        Assert.Equal("-1", TraitState.LayerReset(d, "3"));
    }

    [Fact]
    public void LayerToggleActive_FromZero_TreatsAsLevelOne()
    {
        var d = Layer(false);
        Assert.Equal("1", TraitState.LayerToggleActive(d, "0"));
        Assert.Equal("-1", TraitState.LayerSetLevel(d, "", 0, false));
    }
}
