using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B3 #68: layer("name") reads the level of a layer on the piece being evaluated (0 when inactive). Invented data.</summary>
public class LayerFunctionTests
{
    private static GameState Fort(string levels)
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Fort", traits: new[] { (EffectFx.Levels, levels), (EffectFx.Damage, ""), (EffectFx.Mask, "null") });
        EffectFx.Add(s, "b2", "South Guns", x: 160);
        return s;
    }

    private static FormulaTrace Read(GameState s, string piece, string text = "layer(\"Levels\")") =>
        EffectFx.Run(s, EffectFx.Pack(EffectFx.F("Read", text) with { Bindings = new() }), piece);

    [Fact]
    public void Layer_reads_level_and_zero_when_inactive()
    {
        Assert.Equal(0, Read(Fort(""), "b1").Result!.Value.Number);     // default: inactive
        Assert.Equal(3, Read(Fort("3"), "b1").Result!.Value.Number);
        Assert.Equal(0, Read(Fort("-2"), "b1").Result!.Value.Number);   // level 2, inactive
        Assert.Equal(4, Read(Fort("4"), "b1", "layer(\" levels \")").Result!.Value.Number);   // trimmed, case-insensitive
    }

    [Fact]
    public void Layer_in_filter()
    {
        var s = Fort("3");
        EffectFx.Add(s, "b3", "South Camp", x: 110, traits: new[] { (EffectFx.Levels, "1") });
        var pack = EffectFx.Pack();
        Assert.True(PieceFilter.Matches("layer(\"Levels\") >= 2", s.Pieces["b1"], s, EffectFx.Model, pack));
        Assert.False(PieceFilter.Matches("layer(\"Levels\") >= 2", s.Pieces["b3"], s, EffectFx.Model, pack));
        Assert.False(PieceFilter.Matches("layer(\"Levels\") >= 0", s.Pieces["b2"], s, EffectFx.Model, pack));   // no such layer: no match
        // an aggregate filter: a piece without the layer is no match, not an error
        Assert.Equal(1, Read(s, "b1", "countof(\"layer(\\\"Levels\\\") >= 2\")").Result!.Value.Number);
    }

    [Fact]
    public void Layer_on_placeholder_hidden_data()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Guns");
        EffectFx.Add(s, "r1", "North Fort", x: 160, traits: new[] { (EffectFx.Levels, "3"), (EffectFx.Mask, "red;") });
        var pack = EffectFx.Pack();
        var view = PlayerView.For(s, pack, EffectFx.Model, EffectFx.South);
        Assert.True(PlayerView.IsPlaceholder(view.Pieces["r1"]));
        var ex = Assert.Throws<EvalError>(() => PieceFilter.CompileStrict("layer(\"Levels\") > 0", view, pack, EffectFx.Model)(view.Pieces["r1"]));
        Assert.True(ex.HiddenData);
        Assert.Equal("layer() cannot be read in this view", ex.Message);
        // the owner's view reads it
        var north = PlayerView.For(s, pack, EffectFx.Model, EffectFx.North);
        Assert.True(PieceFilter.CompileStrict("layer(\"Levels\") == 3", north, pack, EffectFx.Model)(north.Pieces["r1"]));
    }

    [Fact]
    public void Layer_missing_name_fails()
    {
        Assert.Equal("South Guns has no layer \"Levels\"", Read(Fort("1"), "b2").Error);
        Assert.Equal("South Fort has no layer \"Walls\"", Read(Fort("1"), "b1", "layer(\"Walls\")").Error);
        Assert.Equal("Layer \"Damage\" follows a property; read the property instead", Read(Fort("1"), "b1", "layer(\"Damage\")").Error);
        Assert.Equal("This formula needs a unit", Read(Fort("1"), null!).Error);
    }

    [Fact]
    public void Layer_missing_on_nameless_piece_says_unknown_piece()
    {
        var s = Fort("1");
        EffectFx.Add(s, "b4", "", x: 210);
        Assert.Equal("Unknown piece has no layer \"Levels\"", Read(s, "b4").Error);
    }
}
