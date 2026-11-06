using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented module, temp cache/settings roots only: roll, place via the palette path, move, notes, turn, then save and load.</summary>
public class Piece2bEndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-p2b-" + Guid.NewGuid().ToString("N"));
    private readonly FixtureModule _fx = new();
    private readonly ModuleSession _s;

    public Piece2bEndToEndTests()
    {
        Directory.CreateDirectory(_tmp);
        var armor = """<VASSAL.build.widget.PieceSlot entryName="Armor Division" gpid="2" height="32" width="32">+/null/piece;;;unit.svg;Armor Division/null;0;0;2</VASSAL.build.widget.PieceSlot>""";
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.widget.ListWidget>", armor + "</VASSAL.build.widget.ListWidget>");
        _s = ModuleSession.Open(_fx.WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg").Build(),
            Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "rules"));
    }

    public void Dispose()
    {
        _s.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void EndToEnd_RollPlaceMoveNotesTurn_SaveLoad()
    {
        var settings = new SettingsStore(Path.Combine(_tmp, "settings"));
        Assert.NotNull(settings.Load(_s.Log)); // defaults load from an empty temp root
        _s.SetRoller((n, sides) => Enumerable.Range(0, n).Select(i => 3 + i).ToList());
        _s.StartNewGame();
        var game = _s.Game!;
        var map = _s.Model.Maps[0];
        var who = "local";
        DateTimeOffset Now() => DateTimeOffset.UtcNow;

        var results = _s.Roll(2, 6);
        game.Execute(new RollDice("2d6", 2, 6, results, "2d6", null, 0, Now(), who));

        var slot = Flatten(PaletteModel.Build(_s.Model)).First(x => x.EntryName == "Armor Division");
        var target = PaletteModel.ResolveDrop(game.State, map, new PointD(110, 60), _s.Images, snap: true);
        Assert.NotNull(target);
        var (type, state) = PieceFactory.FromSlot(_s.Model, slot, target!.Value.MapId, target.Value.Pos);
        game.Execute(new AddPiece("p:e2e", type, state, target.Value.MapId, target.Value.Pos, target.Value.StackOnto, null, Now(), who));
        Assert.True(game.State.Pieces.ContainsKey("p:e2e"));

        game.Execute(new MovePieces(new[] { "p:e2e" }, target.Value.MapId, new PointD(target.Value.Pos.X + 30, target.Value.Pos.Y), null, null, Now(), who));
        game.Execute(new SetNotes("remember the bridge", Now(), who));
        game.Execute(new SetTurn(2, "Movement", Now(), who));

        var path = Path.Combine(_tmp, "e2e.qsave");
        _s.SaveNative(path);
        _s.StartNewGame();
        _s.LoadNative(path);
        var loaded = _s.Game!.State;

        // StartNewGame itself logs one system entry (Seq 1), then the five commands.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, loaded.Log.Select(e => e.Seq));
        Assert.Equal(new[] { LogKind.System, LogKind.Dice, LogKind.Place, LogKind.Move, LogKind.Note, LogKind.Turn }, loaded.Log.Select(e => e.Kind));
        Assert.Equal("remember the bridge", loaded.Notes);
        Assert.Equal(new TurnState(2, "Movement"), loaded.Turn);
        Assert.True(loaded.Pieces.ContainsKey("p:e2e"));
        Assert.Contains("3 + 4", loaded.Log[1].Text);
    }

    [Fact]
    public void EndToEnd_Format2SaveStillLoads()
    {
        var path = Path.Combine(_tmp, "old.qsave");
        File.WriteAllText(path, "{\"format\":2,\"moduleSha256\":\"" + _s.Archive.Sha256 + "\",\"snapshot\":{\"pieces\":{},\"stacks\":{},\"mapOrder\":[]},\"log\":[]}");
        _s.LoadNative(path);
        var st = _s.Game!.State;
        Assert.Empty(st.Log);
        Assert.Equal("", st.Notes);
        Assert.Equal(new TurnState(1, null), st.Turn);
    }

    private static IEnumerable<PieceSlotDef> Flatten(PaletteNode n) => n.Slots.Concat(n.Children.SelectMany(Flatten));
}
