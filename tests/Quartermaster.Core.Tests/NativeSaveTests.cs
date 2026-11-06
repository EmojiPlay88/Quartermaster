using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class NativeSaveTests
{
    [Fact]
    public void NativeSave_RoundTrip_PreservesStateAndLog()
    {
        var st = new GameState(); st.MapOrder.Add("m1");
        var sess = new GameSession(st, "abc123");
        int fired = 0; sess.Changed += _ => fired++;
        sess.Execute(new AddPiece("z", "t", "", "m1", new PointD(1.5, 2), null));
        sess.Execute(new AddPiece("a", "t", "", "m1", new PointD(3, 4), null));
        sess.Execute(new RemovePiece("z"));
        sess.Execute(new AddPiece("m", "t", "", "m1", new PointD(3, 4), null));
        sess.Execute(new MovePieces(["a", "m"], "m1", new PointD(7, 8), null));
        sess.Execute(new SetTraitState("a", 1, "s"));
        sess.Execute(new SetStackExpanded(sess.State.Pieces["a"].StackId!, true));
        sess.Execute(new ToggleTrail("a"));
        sess.Execute(new ClearMoved("m1"));
        Assert.Equal(9, fired);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            var back = NativeSave.Read(path);
            Assert.Equal(JsonSerializer.Serialize(sess.State, QuartermasterJson.Options), JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
            Assert.Equal(["a", "m"], back.State.Pieces.Keys);
            Assert.Equal(sess.Log.Count, back.Log.Count);
            Assert.Equal(sess.Log[4].GetType(), back.Log[4].GetType());
            Assert.Equal(["a", "m"], ((MovePieces)back.Log[4]).PieceIds);
            Assert.Equal("abc123", back.ModuleSha256);
            Assert.Contains("\"$t\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_Read_WrongFormatVersion_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            File.WriteAllText(path, "{\"format\":2,\"moduleSha256\":\"x\",\"snapshot\":{},\"log\":[]}");
            var ex = Assert.Throws<InvalidDataException>(() => NativeSave.Read(path));
            Assert.Contains("2", ex.Message);
            File.WriteAllText(path, "{not json");
            Assert.IsType<JsonException>(Assert.Throws<InvalidDataException>(() => NativeSave.Read(path)).InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeSave_RoundTrip_NestedTraitStates()
    {
        var (type, state, expected) = GameStateTests.NestedPiece();
        var st = new GameState(); st.MapOrder.Add("m1");
        var sess = new GameSession(st, "h");
        sess.Execute(new AddPiece("a", type, state, "m1", new PointD(0, 0), null));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".qsave");
        try
        {
            NativeSave.Write(path, sess);
            Assert.Equal(expected, NativeSave.Read(path).State.Pieces["a"].TraitStates);
        }
        finally { File.Delete(path); }
    }
}
