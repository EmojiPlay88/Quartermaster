using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class DiceExprTests
{
    [Theory]
    [InlineData("2d6", 2, 6, 0)]
    [InlineData("d6", 1, 6, 0)]
    [InlineData(" 3D10 + 2 ", 3, 10, 2)]
    [InlineData("2d6-3", 2, 6, -3)]
    public void DiceExpr_Parse_Valid(string text, int count, int sides, int mod)
    {
        var (spec, error) = DiceExpr.Parse(text);
        Assert.Null(error);
        Assert.Equal(new DiceSpec(count, sides, mod), spec);
    }

    [Theory]
    [InlineData("0d6", "Use 1–100 dice")]
    [InlineData("3d1", "Dice need 2–1000 sides")]
    [InlineData("200d6", "Use 1–100 dice")]
    [InlineData("d", "Dice must look like 2d6 or 3d10+2")]
    [InlineData("2d6+", "Dice must look like 2d6 or 3d10+2")]
    [InlineData("abc", "Dice must look like 2d6 or 3d10+2")]
    public void DiceExpr_Parse_BadInputs_ReturnsError(string text, string message)
    {
        var (spec, error) = DiceExpr.Parse(text);
        Assert.Null(spec);
        Assert.Equal(message, error);
    }

    [Fact]
    public void DiceExpr_Parse_HugeNumbers_DoNotThrow()
    {
        Assert.NotNull(DiceExpr.Parse("99999999999999999999d6").Error);
        Assert.NotNull(DiceExpr.Parse("2d6+5000").Error);
    }

    [Fact]
    public void DiceExpr_Format()
    {
        Assert.Equal("2d6+1", DiceExpr.Format(new DiceSpec(2, 6, 1)));
        Assert.Equal("1d6", DiceExpr.Format(new DiceSpec(1, 6, 0)));
        Assert.Equal("3d10-2", DiceExpr.Format(new DiceSpec(3, 10, -2)));
    }
}
