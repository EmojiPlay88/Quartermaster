using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public sealed class PieceEnv : IDisposable
{
    public FixtureModule Fx = new();
    public TempCache Cache = new();
    public VmodArchive Arch = null!;
    public DiagnosticLog Log = new();
    public ImageSource Images = null!;
    public ModuleModel Model = null!;

    public static readonly SKColor Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 255, 0);

    public PieceEnv Open()
    {
        Fx.WithBuildFile(FixtureModule.MinimalBuildFile());
        Arch = VmodArchive.Open(Fx.Build());
        Images = new ImageSource(Arch, Cache.Dir, Log);
        Model = ModuleModelBuilder.Build(Arch, Log);
        return this;
    }

    public void Dispose() { Arch?.Dispose(); Fx.Dispose(); Cache.Dispose(); }

    public static string Enc(params string[] f) => SequenceEncoder.Encode(f, ';');
    public static string Chain(params string[] levels)
    {
        var cur = levels[^1];
        for (var i = levels.Length - 2; i >= 0; i--) cur = SequenceEncoder.Encode(new[] { levels[i], cur }, '\t');
        return cur;
    }
    public static string Basic(string image) => "piece;" + Enc("", "", image, "P");
    public static string Layer(string images, int x = 0, int y = 0, bool under = false) =>
        "emb2;" + Enc("Toggle", "130", "A", "Up", "130", "", "Down", "130", "", "Reset", "", "1", under ? "true" : "false",
            x.ToString(), y.ToString(), images, "A,B", "true", "Lyr", "", "", "true", "", "1", "2", "true");
    public static string Immobile(bool selLocked) => "immob;" + Enc(selLocked ? "n" : "", "");

    public static PieceInstance Piece(string id, string type, params string[] states) =>
        new() { Id = id, TypeString = type, TraitStates = states.ToList() };

    public static SKColor Center(Action<SKCanvas> draw, int size = 64)
    {
        using var bmp = new SKBitmap(size, size);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        draw(c);
        return bmp.GetPixel(size / 2, size / 2);
    }
}

public class PieceRendererTests
{
    private static PieceEnv Env()
    {
        var e = new PieceEnv();
        e.Fx.AddImage("red.png", 32, 32, PieceEnv.Red).AddImage("blue.png", 32, 32, PieceEnv.Blue).AddImage("base.png", 48, 48, PieceEnv.Green);
        return e.Open();
    }

    private static PieceType Parse(PieceEnv e, string type) => PieceTypeParser.Parse(type, e.Log, null);

    [Theory]
    [InlineData("2", 0, 0, 255)]
    [InlineData("1", 255, 0, 0)]
    public void PieceRenderer_LayerAtLevel_DrawsMatchingImage(string state, int r, int g, int b)
    {
        using var e = Env();
        var type = Parse(e, PieceEnv.Chain(PieceEnv.Layer("red.png,blue.png"), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("p", "", state, "");
        var px = PieceEnv.Center(c => new PieceRenderer(e.Images).Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None));
        Assert.Equal(new SKColor((byte)r, (byte)g, (byte)b), px);
    }

    [Fact]
    public void PieceRenderer_InactiveLayerNotDrawn()
    {
        using var e = Env();
        var type = Parse(e, PieceEnv.Chain(PieceEnv.Layer("red.png,blue.png"), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("p", "", "-2", "");
        var px = PieceEnv.Center(c => new PieceRenderer(e.Images).Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None));
        Assert.Equal(PieceEnv.Green, px);
    }

    [Fact]
    public void PieceRenderer_DrawUnderneath_OnlyWhenSelected()
    {
        using var e = Env();
        var type = Parse(e, PieceEnv.Chain(PieceEnv.Layer("red.png", under: true), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("p", "", "1", "");
        var r = new PieceRenderer(e.Images);
        Assert.Equal(PieceEnv.Red, PieceEnv.Center(c => r.Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None)));
        Assert.Equal(PieceEnv.Green, PieceEnv.Center(c => r.Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.Selected)));
    }

    [Fact]
    public void PieceRenderer_Rotation_RotatesWholePiece()
    {
        using var e = Env();
        // layer offset (20,0) puts red to the right of centre; rotating 90 degrees clockwise moves it below centre.
        var type = Parse(e, PieceEnv.Chain("rotate;" + PieceEnv.Enc("4", "", "", "", "", "", "", ""),
            PieceEnv.Layer("red.png", x: 20), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("p", "", "1", "1", "");
        using var bmp = new SKBitmap(64, 64);
        using var cv = new SKCanvas(bmp);
        cv.Clear(SKColors.White);
        new PieceRenderer(e.Images).Draw(cv, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None);
        Assert.Equal(PieceEnv.Red, bmp.GetPixel(32, 52));
        Assert.NotEqual(PieceEnv.Red, bmp.GetPixel(52, 32));
    }

    [Fact]
    public void PieceRenderer_HiddenIsTranslucent_ObscuredShowsMask()
    {
        using var e = Env();
        var r = new PieceRenderer(e.Images);
        var hide = Parse(e, PieceEnv.Chain("hide;" + PieceEnv.Enc("I", "Invisible", "", "", "0.3"), PieceEnv.Basic("base.png")));
        var hp = PieceEnv.Piece("h", "", "Me", "");
        var px = PieceEnv.Center(c => r.Draw(c, hp, hide, new PointD(32, 32), 1f, PieceDrawFlags.None));
        Assert.True(px.Red > 150 && px.Green > 200, px.ToString()); // mostly white, tinted green
        Assert.NotEqual(PieceEnv.Green, px);
        var obs = Parse(e, PieceEnv.Chain("obs;" + PieceEnv.Enc("", "", "Mask", "I", "red.png"), PieceEnv.Basic("base.png")));
        var op = PieceEnv.Piece("o", "", "Side;", "");
        Assert.Equal(PieceEnv.Red, PieceEnv.Center(c => r.Draw(c, op, obs, new PointD(32, 32), 1f, PieceDrawFlags.None)));
        var open = PieceEnv.Piece("o2", "", "null;", "");
        Assert.Equal(PieceEnv.Green, PieceEnv.Center(c => r.Draw(c, open, obs, new PointD(32, 32), 1f, PieceDrawFlags.None)));
    }

    [Fact]
    public void PieceRenderer_MovedMarkerDrawnOnlyWhenMoved()
    {
        using var e = Env();
        var r = new PieceRenderer(e.Images);
        var type = Parse(e, PieceEnv.Chain("markmoved;" + PieceEnv.Enc("red.png", "-16", "-16"), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("m", "", "true", "");
        p.Moved = true;
        Assert.Equal(PieceEnv.Red, PieceEnv.Center(c => r.Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None)));
        p.Moved = false;
        Assert.Equal(PieceEnv.Green, PieceEnv.Center(c => r.Draw(c, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None)));
    }

    [Fact]
    public void PieceRenderer_LabelDrawsTextFromState()
    {
        using var e = Env();
        var r = new PieceRenderer(e.Images);
        var type = Parse(e, PieceEnv.Chain("label;" + PieceEnv.Enc("", "Change", "24", "", "0,0,0", "c", "0", "c", "0", "c", "c", "$label$", "Dialog", "1", "0", "TextLabel"), PieceEnv.Basic("base.png")));
        var p = PieceEnv.Piece("l", "", "WWWW", "");
        using var bmp = new SKBitmap(64, 64);
        using var cv = new SKCanvas(bmp);
        cv.Clear(SKColors.White);
        r.Draw(cv, p, type, new PointD(32, 32), 1f, PieceDrawFlags.None);
        Assert.Contains(Enumerable.Range(0, 64 * 64).Select(i => bmp.GetPixel(i % 64, i / 64)), px => px.Red < 100 && px.Blue < 100 && px.Green < 100);
        // empty label state draws no text
        using var bmp2 = new SKBitmap(64, 64);
        using var cv2 = new SKCanvas(bmp2);
        cv2.Clear(SKColors.White);
        r.Draw(cv2, PieceEnv.Piece("l2", "", "", ""), type, new PointD(32, 32), 1f, PieceDrawFlags.None);
        Assert.DoesNotContain(Enumerable.Range(0, 64 * 64).Select(i => bmp2.GetPixel(i % 64, i / 64)), px => px.Red < 100 && px.Blue < 100 && px.Green < 100);
    }

    [Fact]
    public void PieceRenderer_MissingBaseImage_DoesNotThrow()
    {
        using var e = Env();
        var type = Parse(e, PieceEnv.Basic("nope.png"));
        PieceEnv.Center(c => new PieceRenderer(e.Images).Draw(c, PieceEnv.Piece("x", ""), type, new PointD(32, 32), 1f, PieceDrawFlags.Ghost));
        Assert.Single(e.Log.Items, d => d.Category == "image");
    }
}

public class HitTesterTests
{
    private static string Type(string img, bool locked = false) =>
        locked ? PieceEnv.Chain(PieceEnv.Immobile(true), PieceEnv.Basic(img)) : PieceEnv.Basic(img);

    private static GameState State(MapDef map, params (string Id, string Type, double X, double Y)[] stacks)
    {
        var s = new GameState();
        foreach (var (id, type, x, y) in stacks)
        {
            var pc = PieceEnv.Piece("pc-" + id, type);
            pc.MapId = map.Id; pc.StackId = id; pc.Pos = new PointD(x, y);
            s.Pieces[pc.Id] = pc;
            s.Stacks[id] = new StackInstance { Id = id, MapId = map.Id, Pos = new PointD(x, y), PieceIds = { pc.Id } };
        }
        return s;
    }

    [Fact]
    public void HitTester_TopmostWins()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("a.png", 32, 32); e.Open();
        var map = e.Model.Maps[0];
        var s = State(map, ("s1", Type("a.png"), 100, 100), ("s2", Type("a.png"), 110, 100));
        Assert.Equal("pc-s2", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(105, 100)));
        Assert.Equal("pc-s1", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(88, 100)));
        Assert.Null(HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(300, 300)));
    }

    [Fact]
    public void HitTester_StackedPiecesHitTopFirst()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("a.png", 32, 32); e.Open();
        var map = e.Model.Maps[0];
        var s = State(map, ("s1", Type("a.png"), 100, 100));
        var top = PieceEnv.Piece("top", Type("a.png")); top.MapId = map.Id; top.StackId = "s1"; top.Pos = new PointD(100, 100);
        s.Pieces["top"] = top; s.Stacks["s1"].PieceIds.Add("top");
        Assert.Equal("top", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(100, 100)));
    }

    [Fact]
    public void HitTester_SelectionLockedSkipped()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("a.png", 32, 32); e.Open();
        var map = e.Model.Maps[0];
        var s = State(map, ("s1", Type("a.png"), 100, 100), ("s2", Type("a.png", locked: true), 100, 100));
        Assert.Equal("pc-s1", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(100, 100)));
    }

    [Fact]
    public void HitTester_NonRectangularShapeUsed()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("a.png", 32, 32); e.Open();
        var map = e.Model.Maps[0];
        // triangle in centre-relative coordinates: (-16,-16) (16,-16) (-16,16)
        var shape = PieceEnv.Chain("nonRect2;" + PieceEnv.Enc("1.0", "d M -16 -16 L 16 -16 L -16 16 Z"), PieceEnv.Basic("a.png"));
        var s = State(map, ("s1", shape, 100, 100));
        Assert.Equal("pc-s1", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(90, 90)));
        Assert.Null(HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(112, 112)));
    }

    [Fact]
    public void HitTester_BadShapeFallsBackToImageRectWithOneDiagnostic()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("a.png", 32, 32); e.Open();
        var map = e.Model.Maps[0];
        var shape = PieceEnv.Chain("nonRect2;" + PieceEnv.Enc("1.0", "d M x y"), PieceEnv.Basic("a.png"));
        var s = State(map, ("s1", shape, 100, 100));
        var cache = new PieceTypeCache(e.Log);
        Assert.Equal("pc-s1", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(112, 112), cache));
        HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(112, 112), cache);
        Assert.Single(e.Log.Items, d => d.Category == "shape");
    }
}

public class ShapeParserTests
{
    [Fact]
    public void Parses_NewAndLegacyFormats_AndRejectsGarbage()
    {
        var p = ShapeParser.TryParse("d M 0 0 L 10 0 L 10 10 Z N img.png");
        Assert.NotNull(p);
        Assert.True(p!.Contains(8, 2));
        var legacy = ShapeParser.TryParse("m,0,0,l,10,0,l,10,10,c");
        Assert.NotNull(legacy);
        Assert.True(legacy!.Contains(8, 2));
        Assert.Null(ShapeParser.TryParse("d M x y"));
        Assert.Null(ShapeParser.TryParse(""));
    }
}

public class MapRendererTests
{
    [Fact]
    public async Task MapRenderer_DrawsTilesAndStackedPiece()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("unit.png", 20, 20, PieceEnv.Red); e.Open();
        var map = e.Model.Maps[0];
        var tiler = new BoardTiler(e.Arch, e.Cache.Dir, e.Log);
        var ts = await tiler.EnsureTilesAsync(map.Boards[0], null, CancellationToken.None);
        var tiles = new Dictionary<string, TileSet> { [map.Boards[0].Name] = ts };
        var s = new GameState();
        var pc = PieceEnv.Piece("p1", PieceEnv.Basic("unit.png")); pc.MapId = map.Id; pc.StackId = "s1"; pc.Pos = new PointD(100, 50);
        s.Pieces["p1"] = pc;
        s.Stacks["s1"] = new StackInstance { Id = "s1", MapId = map.Id, Pos = new PointD(100, 50), PieceIds = { "p1" } };
        var vp = new Viewport();
        using var bmp = new SKBitmap(200, 100);
        using var cv = new SKCanvas(bmp);
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        mr.Draw(cv, map, tiles, s, e.Model, vp, new SKSize(200, 100), new MapDrawOptions(true, new HashSet<string> { "p1" }, null));
        Assert.Equal(PieceEnv.Red, bmp.GetPixel(100, 50));
        Assert.Equal(new SKColor(0x40, 0x80, 0xC0), bmp.GetPixel(10, 90));
        Assert.Equal("p1", HitTester.PieceAt(s, map, e.Model, e.Images, new PointD(105, 55)));
    }
}
