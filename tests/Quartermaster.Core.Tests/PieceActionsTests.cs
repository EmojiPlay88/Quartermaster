using Quartermaster.Core;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PieceActionsTests
{
    private const string P = "VASSAL.build.module.map.boardPicker.board.";

    private static string BuildXml(string? zone = null)
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        if (zone != null)
            xml = xml.Replace($"<{P}SquareGrid color", $"<{P}ZonedGrid>{zone}<{P}SquareGrid color")
                     .Replace($"</{P}SquareGrid>", $"</{P}SquareGrid></{P}ZonedGrid>");
        return xml;
    }

    private static ModuleModel Model(string? xml = null)
    {
        using var fx = new FixtureModule().WithBuildFile(xml ?? BuildXml());
        var path = fx.Build();
        using var arch = VmodArchive.Open(path);
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    }

    private static string Chain(params string[] layers) => PieceTypeParser.EncodeState(layers);
    private const string Basic = "piece;;;unit.svg;Unit";

    private static PieceInstance Add(GameState s, string id, string type, string state = "", string? onto = null, PointD? pos = null)
    {
        s.Apply(new AddPiece(id, type, state, "Main Map", pos ?? new PointD(100, 100), onto));
        return s.Pieces[id];
    }

    private static Func<string> Ids()
    {
        var n = 0;
        return () => "new" + (++n);
    }

    private static PieceAction Find(IReadOnlyList<PieceAction> a, string label) => a.Single(x => x.Label == label);

    private static void Run(GameState s, PieceAction a) { foreach (var c in a.Build(s)) s.Apply(c); }

    [Fact]
    public void SendTo_XY_MovesToBoardOffsetPoint()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("sendto;Go;K;Main Map;Main Board;300;400;;;0;0;1;1;;L;;;;", Basic));
        var off = model.Maps[0].Boards[0].Offset;
        var act = Find(PieceActions.For(p, model, s, Ids()), "Go");
        Assert.True(act.Enabled);
        var mv = Assert.IsType<MovePieces>(Assert.Single(act.Build(s)));
        Assert.Equal("Main Map", mv.MapId);
        Assert.Equal(new PointD(300 + off.X, 400 + off.Y), mv.Pos);
        Assert.Equal(new[] { "a" }, mv.PieceIds);
        Run(s, act);
        Assert.Equal(new PointD(300 + off.X, 400 + off.Y), s.Pieces["a"].Pos);
    }

    [Fact]
    public void SendTo_GridLocation_ResolvesThroughNumbering()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("sendto;Go;K;Main Map;Main Board;0;0;;;0;0;1;1;;G;;;;0405", Basic));
        var off = model.Maps[0].Boards[0].Offset;
        var mv = Assert.IsType<MovePieces>(Assert.Single(Find(PieceActions.For(p, model, s, Ids()), "Go").Build(s)));
        Assert.Equal(new PointD(175 + off.X, 225 + off.Y), mv.Pos);
    }

    [Fact]
    public void SendTo_Zone_GoesToZoneCentre()
    {
        var zone = $"<{P}mapgrid.Zone name=\"Box\" path=\"100,100;300,100;300,300;100,300\" locationFormat=\"$name$\"/>";
        var model = Model(BuildXml(zone));
        var s = new GameState();
        var p = Add(s, "a", Chain("sendto;Go;K;Main Map;Main Board;0;0;;;0;0;1;1;;Z;Box;;;", Basic));
        var off = model.Maps[0].Boards[0].Offset;
        var mv = Assert.IsType<MovePieces>(Assert.Single(Find(PieceActions.For(p, model, s, Ids()), "Go").Build(s)));
        Assert.Equal(new PointD(200 + off.X, 200 + off.Y), mv.Pos);
    }

    [Fact]
    public void SendTo_Unresolvable_IsDisabled()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("sendto;Go;K;Main Map;Main Board;$unknown$;5;;;0;0;1;1;;L;;;;", Basic));
        var p2 = Add(s, "b", Chain("sendto;Zap;K;Main Map;Main Board;0;0;;;0;0;1;1;;Z;Nowhere;;;", Basic));
        var act = Find(PieceActions.For(p, model, s, Ids()), "Go");
        Assert.False(act.Enabled);
        Assert.False(string.IsNullOrEmpty(act.DisabledReason));
        Assert.Empty(act.Build(s));
        Assert.False(Find(PieceActions.For(p2, model, s, Ids()), "Zap").Enabled);
    }

    [Fact]
    public void Clone_StacksOntoSource()
    {
        var model = Model();
        var s = new GameState();
        var type = Chain("rotate;4;a;b;CW;CCW;;;Rot", "clone;Clone;C", Basic);
        Add(s, "x", Chain(Basic));
        var p = Add(s, "a", type, Chain("2", "", "Main Map;100;100;;0"), onto: s.Pieces["x"].StackId);
        var act = Find(PieceActions.For(p, model, s, Ids()), "Clone");
        var cmds = act.Build(s);
        var add = Assert.IsType<AddPiece>(Assert.Single(cmds));
        Assert.Equal("new1", add.PieceId);
        Assert.Equal(p.TypeString, add.TypeString);
        Assert.Equal(p.StackId, add.StackOnto);
        s.Apply(add);
        var c = s.Pieces["new1"];
        Assert.Equal(p.StackId, c.StackId);
        Assert.Equal(p.TraitStates, c.TraitStates);
        Assert.Equal(new[] { "x", "a", "new1" }, s.Stacks[p.StackId!].PieceIds);
    }

    [Fact]
    public void Delete_RemovesPiece()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("delete;Delete;D", Basic));
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Delete"));
        Assert.Empty(s.Pieces);
    }

    private const string Layer =
        "emb2;Activate;130;A;Up;130;U;Down;130;D;Reset;R;2;false;0;0;a.png,b.png,c.png;A,B,C;true;Lyr;;;false;;1;1;";

    [Fact]
    public void Layer_UpWraps_WhenLoop()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain(Layer + "true", Basic), Chain("3", "Main Map;100;100;;0"));
        var acts = PieceActions.For(p, model, s, Ids());
        Assert.DoesNotContain(acts, a => a.Label == "Activate"); // always active
        var set = Assert.IsType<SetTraitState>(Assert.Single(Find(acts, "Up").Build(s)));
        Assert.Equal(("a", 0, "1"), (set.PieceId, set.StateIndex, set.NewState));
        Run(s, Find(acts, "Down"));
        Assert.Equal("2", s.Pieces["a"].TraitStates[0]);
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Reset"));
        Assert.Equal("2", s.Pieces["a"].TraitStates[0]);
    }

    [Fact]
    public void Layer_NotLooping_DisablesUpAtTop_AndActivateToggles()
    {
        var model = Model();
        var s = new GameState();
        var type = Chain("emb2;Activate;130;A;Up;130;U;Down;130;D;;;1;false;0;0;a.png,b.png;A,B;false;Lyr;;;false;;1;1;false", Basic);
        var p = Add(s, "a", type, Chain("-2", "Main Map;100;100;;0"));
        var acts = PieceActions.For(p, model, s, Ids());
        Assert.False(Find(acts, "Up").Enabled);
        Run(s, Find(acts, "Activate"));
        Assert.Equal("2", s.Pieces["a"].TraitStates[0]);
    }

    [Fact]
    public void Rotate_Cw_AdvancesAngleIndex()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("rotate;4;a;b;Rotate CW;Rotate CCW;;;Rot", Basic), Chain("3", "Main Map;100;100;;0"));
        var acts = PieceActions.For(p, model, s, Ids());
        Run(s, Find(acts, "Rotate CW"));
        Assert.Equal("0", s.Pieces["a"].TraitStates[0]);
        Run(s, Find(acts, "Rotate CCW"));
        Assert.Equal("3", s.Pieces["a"].TraitStates[0]);
    }

    [Fact]
    public void HideAndObscure_Toggle()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("hide;I;Hide it;;;0.3;;false", "obs;M;m.png;Mask it;I;?", Basic));
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Hide it"));
        Assert.True(TraitState.IsHidden(new HideableDef("hide"), s.Pieces["a"].TraitStates[0]));
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Mask it"));
        Assert.True(TraitState.IsObscured(new ObscurableDef("obs"), s.Pieces["a"].TraitStates[1]));
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Hide it"));
        Assert.False(TraitState.IsHidden(new HideableDef("hide"), s.Pieces["a"].TraitStates[0]));
    }

    [Fact]
    public void DynamicProperty_IncrementSetAndPrompt()
    {
        var model = Model();
        var s = new GameState();
        // PROP;key;constraints(numeric,min,max,wrap);commands(menu:key:kind,expr,...)
        var prop = "PROP;Steps;true,0,5,false;More:K1:I\\,2,Less:K2:I\\,-2,Zero:K3:P\\,0,Ask:K4:R\\,Value?";
        var p = Add(s, "a", Chain(prop, Basic), Chain("4", "Main Map;100;100;;0"));
        var acts = PieceActions.For(p, model, s, Ids());
        Run(s, Find(acts, "More"));
        Assert.Equal("5", s.Pieces["a"].TraitStates[0]);
        Run(s, Find(acts, "Less"));
        Assert.Equal("3", s.Pieces["a"].TraitStates[0]);
        Run(s, Find(acts, "Zero"));
        Assert.Equal("0", s.Pieces["a"].TraitStates[0]);
        var ask = Find(acts, "Ask");
        Assert.Equal("prompt", ask.Group);
        Assert.NotNull(ask.BuildWithInput);
        foreach (var c in ask.BuildWithInput!(s, "4")) s.Apply(c);
        Assert.Equal("4", s.Pieces["a"].TraitStates[0]);
        Assert.Empty(ask.BuildWithInput!(s, "oops")); // numeric property rejects non-numbers
        Assert.Null(Find(acts, "More").BuildWithInput);
    }

    private static string SlotPath(ModuleModel model)
    {
        var slot = model.Slots[0];
        return string.Join('/', slot.NavPath.Append(slot.EntryName));
    }

    [Fact]
    public void PlaceMarker_FromSlotPath_AddsNewPiece()
    {
        var model = Model();
        var s = new GameState();
        var layer = SequenceEncoder.Encode(new[] { "placemark", "Place", "P", SlotPath(model), "null", "10", "20", "false", "", "", "", "0", "false" }, ';');
        var p = Add(s, "a", Chain(layer, Basic), pos: new PointD(100, 100));
        var act = Find(PieceActions.For(p, model, s, Ids()), "Place");
        var add = Assert.IsType<AddPiece>(Assert.Single(act.Build(s)));
        Assert.Equal("new1", add.PieceId);
        Assert.Equal(new PointD(110, 120), add.Pos);
        s.Apply(add);
        var np = s.Pieces["new1"];
        Assert.Equal(new PointD(110, 120), np.Pos);
        Assert.Equal("Main Map;110;120;1;0", np.TraitStates[^1]);
        Assert.Contains("unit.svg", np.TypeString);
    }

    [Fact]
    public void PlaceMarker_Inline_ExpandsPrototypes()
    {
        var model = Model();
        var spec = SequenceEncoder.Encode(new[] { "+", "null", Chain("prototype;Proto", "piece;;;x.svg;Mk"), "" }, '/');
        var s = new GameState();
        var layer = SequenceEncoder.Encode(new[] { "placemark", "Place", "P", spec, "null", "0", "0", "false", "", "", "", "0", "false" }, ';');
        var p = Add(s, "a", Chain(layer, Basic));
        var act = Find(PieceActions.For(p, model, s, Ids()), "Place");
        Run(s, act);
        var traits = PieceTypeParser.Parse(s.Pieces["new1"].TypeString, new DiagnosticLog(), null).Traits;
        Assert.Equal(new[] { "mark", "piece" }, traits.Select(t => t.Id).ToArray());
        Assert.Equal(p.StackId, s.Pieces["new1"].StackId); // zero offset stacks on the source
    }

    [Fact]
    public void Replace_RemovesSourceAndAddsReplacement()
    {
        var model = Model();
        var s = new GameState();
        var layer = SequenceEncoder.Encode(new[] { "replace", "Swap", "S", SlotPath(model), "null", "0", "0", "false", "", "", "", "0", "false" }, ';');
        var p = Add(s, "a", Chain(layer, Basic), pos: new PointD(60, 70));
        var act = Find(PieceActions.For(p, model, s, Ids()), "Swap");
        var cmds = act.Build(s);
        Assert.IsType<AddPiece>(cmds[0]);
        Assert.Equal(new RemovePiece("a"), cmds[1]);
        foreach (var c in cmds) s.Apply(c);
        Assert.False(s.Pieces.ContainsKey("a"));
        Assert.Equal(new PointD(60, 70), s.Pieces[((AddPiece)cmds[0]).PieceId].Pos);
    }

    [Fact]
    public void Footprint_TogglesTrail()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", Chain("footprint;T;Trail;false", Basic));
        Run(s, Find(PieceActions.For(p, model, s, Ids()), "Trail"));
        Assert.True(s.Pieces["a"].TrailVisible);
    }

    [Fact]
    public void ForMap_MarkAllUnmoved_ClearsMoved()
    {
        var model = Model();
        var act = Assert.Single(PieceActions.ForMap(model.Maps[0]));
        Assert.Equal(new ClearMoved("Main Map"), Assert.Single(act.Build(new GameState())));
    }

    [Fact]
    public void PropertySheet_RoundTrip_AndAction()
    {
        var model = Model();
        var s = new GameState();
        var sheet = "propertysheet;0Name~1Notes~3Hits;Sheet;";
        var p = Add(s, "a", Chain(sheet, Basic));
        Assert.Equal("sheet", Find(PieceActions.For(p, model, s, Ids()), "Sheet").Group);
        Assert.Equal(new[] { ("Name", ""), ("Notes", ""), ("Hits", "") }, PieceActions.ReadSheet(p, 0));
        var set = Assert.IsType<SetTraitState>(PieceActions.WriteSheet(p, 0, new[] { "Bob", "a\nb~c", "3/5" }));
        s.Apply(set);
        Assert.Equal(new[] { ("Name", "Bob"), ("Notes", "a\nb~c"), ("Hits", "3/5") }, PieceActions.ReadSheet(p, 0));
        Assert.Equal("Bob~a|b\\~c~3/5", s.Pieces["a"].TraitStates[0]);
    }

    [Fact]
    public void MoveLocked_CanMoveFalse()
    {
        var s = new GameState();
        var locked = Add(s, "a", Chain("immob;;V", Basic));
        var free = Add(s, "b", Chain("immob;;", Basic));
        var plain = Add(s, "c", Chain(Basic));
        Assert.False(PieceActions.CanMove(locked));
        Assert.True(PieceActions.CanMove(free));
        Assert.True(PieceActions.CanMove(plain));
    }

    [Fact]
    public void BadData_NeverThrows()
    {
        var model = Model();
        var s = new GameState();
        var p = Add(s, "a", "garbage\tmore", "");
        p.TraitStates.Clear();
        Assert.NotNull(PieceActions.For(p, model, s, Ids()));
        Assert.Empty(PieceActions.ReadSheet(p, 7));
        Assert.IsType<SetTraitState>(PieceActions.WriteSheet(p, 7, new[] { "x" }));
    }
}
