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
}
