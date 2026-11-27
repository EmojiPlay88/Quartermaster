using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>B9 #55: a draw is made once, when the run is made: replay and undo/redo use the run's commands and never draw again.</summary>
public class DrawReplayTests
{
    [Fact]
    public void Replay_does_not_redraw()
    {
        var pack = DrawFx.Pack();
        var (runner, calls) = DrawFx.Runner(pack);
        var g = new GameSession(DrawFx.State(), "sha");
        var t = runner.Run(g.State, DrawFx.Request());
        using (g.BeginAction("Shore battery")) foreach (var c in runner.Commands(t, null, sideId: "north")) g.Execute(c);
        Assert.Equal(3, calls.Count);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, g);
            var back = NativeSave.Read(path);
            Assert.Equal(3, calls.Count);   // loading replays the commands: the roller is not called
            foreach (var id in new[] { "b1", "b2", "b3", "b4" })
                Assert.Equal(Ledger.Get(g.State, LedgerKey.Piece(id), "p-boats"), Ledger.Get(back.State, LedgerKey.Piece(id), "p-boats"));
            Assert.Equal(g.State.Log.Select(l => (l.Kind, l.Text, l.PieceId, l.SideId)), back.State.Log.Select(l => (l.Kind, l.Text, l.PieceId, l.SideId)));
            Assert.Contains(back.State.Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Undo_redo_draw_run_one_step()
    {
        var pack = DrawFx.Pack();
        var (runner, calls) = DrawFx.Runner(pack);
        var g = new GameSession(DrawFx.State(), "sha");
        var t = runner.Run(g.State, DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        using (g.BeginAction("Shore battery")) foreach (var c in runner.Commands(t, null, sideId: "north")) g.Execute(c);
        Assert.Single(g.Actions);
        Assert.Equal(0, Ledger.Get(g.State, LedgerKey.Piece("b4"), "p-boats"));
        g.Undo();
        Assert.Equal(3, Ledger.Get(g.State, LedgerKey.Piece("b1"), "p-boats"));
        Assert.Equal(2, Ledger.Get(g.State, LedgerKey.Piece("b4"), "p-boats"));
        Assert.DoesNotContain(g.State.Log, l => l.Text.Contains("Hits:"));
        g.Redo();
        Assert.Equal(2, Ledger.Get(g.State, LedgerKey.Piece("b1"), "p-boats"));
        Assert.Equal(0, Ledger.Get(g.State, LedgerKey.Piece("b4"), "p-boats"));
        Assert.Contains(g.State.Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
        Assert.Empty(calls);
    }

    [Fact]
    public void Physical_dice_mode_still_draws_with_roller()
    {
        // physical dice: the Roll binding is typed in (an override); a value typed for the draw is not used, the roller draws
        var b = DrawFx.Battery(text: "Hits + Gun");
        var f = b with { Bindings = b.Bindings.Append(new VariableBinding { Variable = "Gun", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }).ToList() };
        var pack = DrawFx.Pack(f);
        var (runner, calls) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(overrides: new Dictionary<string, string> { ["Gun"] = "4", ["Hits"] = "1" }));
        Assert.Null(t.Error);
        Assert.Equal(new[] { (1, 6), (1, 5), (1, 4) }, calls);
        Assert.Equal(3.0, Assert.Single(t.Variables, v => v.Variable == "Hits").Value!.Value.Number);
        Assert.Equal(7.0, t.Result!.Value.Number);
        Assert.Equal(new[] { ("b2", 1), ("b4", 2) }, DrawFx.Split(t));
    }
}
