using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

public class MapImageExporterTests
{
    static GameState OnePiece(string mapId)
    {
        var s = new GameState();
        var pc = PieceEnv.Piece("p1", PieceEnv.Basic("unit.png")); pc.MapId = mapId; pc.StackId = "s1"; pc.Pos = new PointD(100, 50);
        s.Pieces["p1"] = pc;
        s.Stacks["s1"] = new StackInstance { Id = "s1", MapId = mapId, Pos = new PointD(100, 50), PieceIds = { "p1" } };
        return s;
    }

    [Fact]
    public void Export_ClampsLongSide()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("board.png", 3000, 2000); e.Fx.AddImage("unit.png", 20, 20, PieceEnv.Red); e.Open();
        var map = e.Model.Maps[0];
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        using var bmp = MapImageExporter.Render(map, new Dictionary<string, TileSet>(), new GameState(), e.Model, mr, 4, 1000);
        Assert.Equal(1000, Math.Max(bmp.Width, bmp.Height));
        Assert.Equal(1000, bmp.Width);
        Assert.InRange(bmp.Height, 666, 667);
    }

    [Fact]
    public void Export_DrawsPieces()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("unit.png", 20, 20, PieceEnv.Red); e.Open();
        var map = e.Model.Maps[0];
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        using var bmp = MapImageExporter.Render(map, new Dictionary<string, TileSet>(), OnePiece(map.Id), e.Model, mr, 1);
        Assert.Equal(200, bmp.Width);
        Assert.Equal(100, bmp.Height);
        Assert.Equal(PieceEnv.Red, bmp.GetPixel(100, 50));
    }

    [Fact]
    public void Export_Strips_MatchSinglePass()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("unit.png", 20, 20, PieceEnv.Red); e.Open();
        var map = e.Model.Maps[0];
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        var s = OnePiece(map.Id);
        var tiles = new Dictionary<string, TileSet>();
        using var one = MapImageExporter.Render(map, tiles, s, e.Model, mr, 1, stripHeight: 4096);
        using var many = MapImageExporter.Render(map, tiles, s, e.Model, mr, 1, stripHeight: 16);
        Assert.Equal(one.Bytes, many.Bytes);
    }

    [Fact]
    public void Export_Cancelled_Throws()
    {
        using var e = new PieceEnv(); e.Open();
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MapImageExporter.Render(e.Model.Maps[0], new Dictionary<string, TileSet>(), new GameState(), e.Model, mr, 1, ct: cts.Token));
    }

    [Fact]
    public void Highlight_LastMovedIds_FromLog()
    {
        var log = new GameCommand[]
        {
            new MovePieces(new[] { "a" }, "m1", new PointD(1, 1), null),
            new MovePieces(new[] { "b", "c" }, "m1", new PointD(2, 2), null),
            new MovePieces(new[] { "d" }, "m2", new PointD(3, 3), null),
            new ToggleTrail("a"),
        };
        Assert.Equal(new[] { "b", "c" }, MapViewTools.LastMovedIds(log, "m1").OrderBy(x => x));
        Assert.Equal(new[] { "d" }, MapViewTools.LastMovedIds(log, "m2"));
        Assert.Empty(MapViewTools.LastMovedIds(log, "none"));
        Assert.Empty(MapViewTools.LastMovedIds(Array.Empty<GameCommand>(), "m1"));
    }

    [Fact]
    public void Highlight_OutlinesMovedPiece_AndHideAllDropsCounters()
    {
        using var e = new PieceEnv(); e.Fx.AddImage("unit.png", 20, 20, PieceEnv.Red); e.Open();
        var map = e.Model.Maps[0];
        var mr = new MapRenderer(e.Images, new PieceRenderer(e.Images));
        var s = OnePiece(map.Id);
        var yellow = new SKColor(255, 255, 0);
        SKBitmap Draw(MapDrawOptions o)
        {
            var b = new SKBitmap(200, 100);
            using var c = new SKCanvas(b);
            mr.Draw(c, map, new Dictionary<string, TileSet>(), s, e.Model, new Viewport(), new SKSize(200, 100), o);
            return b;
        }
        using var hl = Draw(new MapDrawOptions(false, new HashSet<string>(), null, HighlightPieceIds: new HashSet<string> { "p1" }, HighlightColor: yellow));
        var found = false;
        for (var y = 0; y < 100 && !found; y++) for (var x = 0; x < 200; x++) if (hl.GetPixel(x, y) == yellow) { found = true; break; }
        Assert.True(found);
        using var hidden = Draw(new MapDrawOptions(false, new HashSet<string>(), null, HideAllPieces: true));
        Assert.NotEqual(PieceEnv.Red, hidden.GetPixel(100, 50));
    }

    [Fact]
    public void SelectionCentre_AveragesSelectedOnMap()
    {
        var s = new GameState();
        s.Pieces["a"] = new PieceInstance { Id = "a", MapId = "m", Pos = new PointD(0, 0) };
        s.Pieces["b"] = new PieceInstance { Id = "b", MapId = "m", Pos = new PointD(100, 40) };
        s.Pieces["c"] = new PieceInstance { Id = "c", MapId = "other", Pos = new PointD(900, 900) };
        Assert.Equal(new PointD(50, 20), MapViewTools.SelectionCentre(s, "m", new HashSet<string> { "a", "b", "c" }));
        Assert.Null(MapViewTools.SelectionCentre(s, "m", new HashSet<string>()));
    }

    [Fact]
    public void SavePng_TempNameIsUnique()
    {
        var a = MapImageExporter.TempPathFor("x.png");
        var b = MapImageExporter.TempPathFor("x.png");
        Assert.NotEqual(a, b);
        Assert.StartsWith("x.png.", a);
        Assert.EndsWith(".tmp", a);
    }

    [Fact]
    public void SavePng_FailureLeavesNoTempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-png-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "out.png");
            Directory.CreateDirectory(target); // moving a file over a directory fails
            using var bmp = new SKBitmap(4, 4);
            Assert.ThrowsAny<Exception>(() => MapImageExporter.SavePng(bmp, target));
            Assert.Empty(Directory.GetFiles(dir));
            var ok = Path.Combine(dir, "ok.png");
            MapImageExporter.SavePng(bmp, ok);
            Assert.Equal(new[] { ok }, Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
