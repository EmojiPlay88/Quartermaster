using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class SideChoiceModelTests
{
    private static SideDef S(string id, string name = "") => new() { Id = id, Name = name };

    [Fact]
    public void Single_side_preticked_when_nothing_ticked() =>
        Assert.Equal(new[] { "red" }, SideChoiceModel.InitialTicks(new[] { S("red", "Red") }, null));

    [Fact]
    public void Ticked_kept_when_given()
    {
        Assert.Equal(new[] { "blue" }, SideChoiceModel.InitialTicks(new[] { S("red") }, new[] { "blue" }));
        Assert.Empty(SideChoiceModel.InitialTicks(new[] { S("red") }, Array.Empty<string>()));
    }

    [Fact]
    public void Two_sides_none_preticked() =>
        Assert.Empty(SideChoiceModel.InitialTicks(new[] { S("red"), S("blue") }, null));

    [Fact]
    public void Label_falls_back_to_id()
    {
        Assert.Equal("Red", SideChoiceModel.Label(S("red", "Red")));
        Assert.Equal("red", SideChoiceModel.Label(S("red")));
    }
}
