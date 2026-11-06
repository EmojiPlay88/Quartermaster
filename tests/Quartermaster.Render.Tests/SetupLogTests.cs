using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class SetupLogTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-sl-" + Guid.NewGuid().ToString("N"));

    public SetupLogTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static string Add(string id, string type, string state) =>
        "+/" + SequenceEncoder.Encode(new[] { id, type, state }, '/');

    private static FixtureModule Fixture()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        return new FixtureModule().WithBuildFile(xml)
            .AddSave("s.vsav", new[] { "begin_save", "", Add("p1", "piece;;;unit.svg;A", "Main Map;100;50;1;0"), Add("p2", "piece;;;unit.svg;B", "Main Map;120;50;1;0"), "end_save" });
    }

    [Fact]
    public void LoadSetup_LogsOneSystemEntry()
    {
        using var fx = Fixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"));
        s.LoadSetup(s.Model.Setups[0]);
        var e = Assert.Single(s.Game!.State.Log);
        Assert.Equal(1, e.Seq);
        Assert.Equal(LogKind.System, e.Kind);
        Assert.Equal("local", e.Who);
        Assert.Equal("Setup \"Start\" loaded: 2 pieces", e.Text);
        Assert.True(e.At > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void StartNewGame_LogsOneSystemEntry()
    {
        using var fx = Fixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"));
        s.StartNewGame();
        var e = Assert.Single(s.Game!.State.Log);
        Assert.Equal(LogKind.System, e.Kind);
        Assert.Equal("New game: 0 pieces", e.Text);
    }

    [Fact]
    public void LoadNative_PreservesSavedLog()
    {
        using var fx = Fixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"));
        s.LoadSetup(s.Model.Setups[0]);
        s.Game!.Execute(new SetNotes("hi", DateTimeOffset.UtcNow, "local"));
        var path = Path.Combine(_tmp, "a.qsave");
        s.SaveNative(path);
        s.StartNewGame();
        s.LoadNative(path);
        Assert.Equal(new[] { "system", "note" }, s.Game!.State.Log.Select(e => e.Kind));
    }
}
