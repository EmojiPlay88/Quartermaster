using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class PieceTypeTests
{
    private static string Enc(char d, params string[] f) => SequenceEncoder.Encode(f, d);
    private static string Chain(params string[] levels)
    {
        // outermost first; nest from the inside out
        var cur = levels[^1];
        for (var i = levels.Length - 2; i >= 0; i--) cur = SequenceEncoder.Encode(new[] { levels[i], cur }, '\t');
        return cur;
    }
    private static string Basic(string image = "unit.svg", string name = "Test Unit") => "piece;" + Enc(';', "", "", image, name);
    private static string Proto(string name) => "prototype;" + name;
    private static string LayerType() =>
        "emb2;" + Enc(';', "Toggle", "130", "A", "Up", "130", "", "Down", "130", "", "Reset", "", "1", "false", "3", "-4",
            "a.png,b.png", "Up,Down", "true", "Lyr", "", "", "false", "", "1", "2", "true");

    [Fact]
    public void Parse_BasicOnly()
    {
        var t = PieceTypeParser.Parse(Basic(), new DiagnosticLog(), null);
        Assert.Single(t.Traits);
        Assert.Equal("unit.svg", t.Basic.Image);
        Assert.Equal("Test Unit", t.Basic.Name);
    }

    [Fact]
    public void Parse_ChainOrderOutermostFirst()
    {
        var t = PieceTypeParser.Parse(Chain("label;" + Enc(';', ""), Proto("Proto"), Basic()), new DiagnosticLog(), null);
        Assert.Equal(new[] { "label", "prototype", "piece" }, t.Traits.Select(x => x.Id).ToArray());
        Assert.Equal("Proto", t.Get<PrototypeRef>()!.PrototypeName);
    }

    [Fact]
    public void Parse_Layer_ReadsImagesAndNames()
    {
        var t = PieceTypeParser.Parse(Chain(LayerType(), Basic()), new DiagnosticLog(), null);
        var l = Assert.IsType<LayerDef>(t.Traits[0]);
        Assert.Equal(new[] { "a.png", "b.png" }, l.Images);
        Assert.Equal(new[] { "Up", "Down" }, l.LevelNames);
        Assert.Equal(3, l.OffsetX);
        Assert.Equal(-4, l.OffsetY);
        Assert.Equal("Toggle", l.ActivateCommand);
        Assert.Equal("Up", l.UpCommand);
        Assert.Equal("Down", l.DownCommand);
        Assert.Equal("Reset", l.ResetCommand);
        Assert.Equal(1, l.ResetLevel);
        Assert.True(l.Loop);
        Assert.Equal("Lyr", l.Name);
        Assert.False(l.DrawUnderneath);
        Assert.True(l.AlwaysActive);
    }

    [Fact]
    public void UnknownTrait_IsInertAndLogged()
    {
        var log = new DiagnosticLog();
        var t = PieceTypeParser.Parse(Chain("zzz;1;2", Basic()), log, "ctx");
        var u = Assert.IsType<UnknownTrait>(t.Traits[0]);
        Assert.Equal("zzz", u.Id);
        var d = Assert.Single(log.Items);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("trait", d.Category);
    }

    [Fact]
    public void TruncatedFields_UseDefaults()
    {
        var t = PieceTypeParser.Parse(Chain("label", Basic()), new DiagnosticLog(), null);
        var l = Assert.IsType<LabelDef>(t.Traits[0]);
        Assert.Equal(10, l.FontSize);
        Assert.Equal(0, l.FontStyle);
        Assert.Null(l.Background);
        Assert.Equal(new RgbColor(0, 0, 0), l.Foreground);
        Assert.Equal('t', l.VerticalPos);
        Assert.Equal('c', l.HorizontalPos);
    }

    [Fact]
    public void MissingBasic_IsSynthesizedWithDiagnostic()
    {
        var log = new DiagnosticLog();
        var t = PieceTypeParser.Parse("mark;a,b", log, null);
        Assert.IsType<BasicPieceDef>(t.Traits[^1]);
        Assert.Equal(new[] { "a", "b" }, t.Get<MarkerDef>()!.Keys);
        Assert.Single(log.Items);
    }

    [Fact]
    public void MoreTraits_ReadFields()
    {
        var log = new DiagnosticLog();
        var imm = PieceTypeParser.Parse("immob;" + Enc(';', "ic", "V", "", "d"), log, null).Get<ImmobileDef>()!;
        Assert.True(imm.MoveLocked); Assert.True(imm.ShiftToSelect); Assert.True(imm.AltToSelect); Assert.False(imm.SelectionLocked);
        var ps = PieceTypeParser.Parse("propertysheet;" + Enc(';', "0Hp~2Mp", "Sheet"), log, null).Get<PropertySheetDef>()!;
        Assert.Equal("Sheet", ps.MenuName);
        Assert.Equal(new[] { ("Hp", 0), ("Mp", 2) }, ps.Rows);
        var dp = PieceTypeParser.Parse("PROP;" + Enc(';', "hp", "true,1,9,true", Enc(',', Enc(':', "Add", "k", Enc(',', "I", "2")), Enc(':', "Sub", "", Enc(',', "P", "x")))), log, null).Get<DynamicPropertyDef>()!;
        Assert.Equal("hp", dp.Key); Assert.True(dp.Numeric); Assert.Equal(1, dp.Min); Assert.Equal(9, dp.Max); Assert.True(dp.Wrap);
        Assert.Equal(new[] { ("Add", "I", "2"), ("Sub", "P", "x") }, dp.Commands);
        var st = PieceTypeParser.Parse("sendto;" + Enc(';', "Go", "k", "Main", "B1", "5", "6", "", "", "0", "0", "1", "1", "d", "Z", "Zn"), log, null).Get<SendToDef>()!;
        Assert.Equal(SendToDestination.Zone, st.DestinationType); Assert.Equal("Zn", st.ZoneName); Assert.Equal("5", st.X); Assert.Equal("Main", st.MapName);
        var rot = PieceTypeParser.Parse("rotate;" + Enc(';', "6", "k1", "k2", "Cw", "Ccw"), log, null).Get<RotatorDef>()!;
        Assert.Equal(6, rot.ValidAngles); Assert.Equal("Cw", rot.RotateCwCommand); Assert.Equal("Ccw", rot.RotateCcwCommand);
        var pm = PieceTypeParser.Parse("placemark;" + Enc(';', "Place", "k", "+/id/stuff", "null", "1", "2", "false", "", "", "", "0", "true"), log, null).Get<PlaceMarkerDef>()!;
        Assert.Equal("Place", pm.CommandName); Assert.Equal(1, pm.OffsetX); Assert.Equal(2, pm.OffsetY); Assert.True(pm.AboveTarget);
        var nr = PieceTypeParser.Parse("nonRect2;" + Enc(';', "1.0", "m", "0", "0"), log, null).Get<NonRectangularDef>()!;
        Assert.Equal("m;0;0", nr.ShapeSpec);
        var ob = PieceTypeParser.Parse("obs;" + Enc(';', "k", "img.png", "Mask", "Go.png", "mask.png"), log, null).Get<ObscurableDef>()!;
        Assert.Equal("Mask", ob.CommandName); Assert.Equal('G', ob.DisplayStyle); Assert.Equal("mask.png", ob.MaskImage);
        Assert.DoesNotContain(log.Items, d => !d.Message.Contains("no BasicPiece"));
    }

    private static PieceType P(string s, DiagnosticLog log) => PieceTypeParser.Parse(s, log, null);

    [Fact]
    public void MissingPrototype_BecomesInert()
    {
        var log = new DiagnosticLog();
        var t = P(Chain("mark;k", Proto("Nope"), Basic()), log);
        var e = new PrototypeExpander(new Dictionary<string, PieceType>(), log).Expand(t);
        Assert.Equal(new[] { "mark", "return", "piece" }, e.Type.Traits.Select(x => x.Id).ToArray());
        Assert.IsType<InertTrait>(e.Type.Traits[1]);
        Assert.Contains(log.Items, d => d.Category == "trait");
        Assert.Equal(new[] { 0, 1, 2 }, e.StateIndex);
    }

    [Fact]
    public void CyclicPrototype_BecomesInert()
    {
        var log = new DiagnosticLog();
        var protos = new Dictionary<string, PieceType>
        {
            ["A"] = P(Chain(Proto("B"), Basic()), log),
            ["B"] = P(Chain("mark;b", Proto("A"), Basic()), log),
        };
        var e = new PrototypeExpander(protos, log).Expand(P(Chain(Proto("A"), Basic()), log));
        Assert.Equal(new[] { "mark", "return", "piece" }, e.Type.Traits.Select(x => x.Id).ToArray());
        Assert.Single(log.Items);
    }

    [Fact]
    public void Expand_SplicesPrototypeTraits_AndMapsStateIndex()
    {
        var log = new DiagnosticLog();
        var protos = new Dictionary<string, PieceType>
        {
            ["Proto"] = P(Chain("mark;m", "immob;" + Enc(';', "", ""), Basic("p.png", "P")), log),
        };
        var t = P(Chain("label;" + Enc(';', ""), Proto("Proto"), "hide;" + Enc(';', "k"), Basic()), log);
        var e = new PrototypeExpander(protos, log).Expand(t);
        Assert.Equal(new[] { "label", "mark", "immob", "hide", "piece" }, e.Type.Traits.Select(x => x.Id).ToArray());
        Assert.Equal(new[] { 0, -1, -1, 2, 3 }, e.StateIndex);
        Assert.Equal("unit.svg", e.Type.Basic.Image);
        Assert.Empty(log.Items);
    }

    [Fact]
    public void SplitState_PadsToTraitCount()
    {
        var state = Chain("s0", "", "bp;state");
        Assert.Equal(new[] { "s0", "", "bp;state" }, PieceTypeParser.SplitState(state, 3));
        Assert.Equal(new[] { "x", "", "" }, PieceTypeParser.SplitState("x", 3));
        Assert.Equal(new[] { "s0", SequenceEncoder.Encode(new[] { "", "bp;state" }, '\t') }, PieceTypeParser.SplitState(state, 2));
    }

    [Fact]
    public void RgbColor_Parse()
    {
        Assert.Null(RgbColor.Parse(null));
        Assert.Null(RgbColor.Parse(" "));
        Assert.Equal(new RgbColor(1, 2, 3), RgbColor.Parse("1,2,3"));
        Assert.Null(RgbColor.Parse("x"));
    }
}
