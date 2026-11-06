using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class ModuleSessionSmokeTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ms-" + Guid.NewGuid().ToString("N"));

    public ModuleSessionSmokeTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static string Add(string id, string type, string state) =>
        "+/" + SequenceEncoder.Encode(new[] { id, type, state }, '/');

    private static FixtureModule Fixture(string extra = "")
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/>" + extra +
            "</VASSAL.build.GameModule>");
        return new FixtureModule().WithBuildFile(xml)
            .AddSave("s.vsav", new[] { "begin_save", "", Add("p1", "piece;;;unit.svg;A", "Main Map;100;50;1;0"), "end_save" });
    }

    [Fact]
    public void OpenLoadSetupSaveLoad_RoundTrips()
    {
        using var fx = Fixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"));
        Assert.Equal("Fixture", s.Model.Name);
        Assert.Contains(s.Archive.Sha256, s.CacheDir);
        Assert.Null(s.Game);

        s.LoadSetup(s.Model.Setups[0]);
        Assert.NotNull(s.Game);
        Assert.True(s.Game!.State.Pieces.Count > 0);

        var save = Path.Combine(_tmp, "a.qsave");
        s.SaveNative(save);
        s.StartNewGame();
        Assert.Empty(s.Game!.State.Pieces);
        s.LoadNative(save);
        Assert.Single(s.Game!.State.Pieces);
    }

    [Fact]
    public void LoadNative_OtherModule_Throws()
    {
        using var fx = Fixture();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"));
        s.LoadSetup(s.Model.Setups[0]);
        var save = Path.Combine(_tmp, "a.qsave");
        s.SaveNative(save);
        File.WriteAllText(save, File.ReadAllText(save).Replace(s.Archive.Sha256, new string('0', 64)));
        var ex = Assert.Throws<InvalidOperationException>(() => s.LoadNative(save));
        Assert.Contains("different module", ex.Message);
    }

    [Fact]
    public void Open_NotAZip_Throws()
    {
        var p = Path.Combine(_tmp, "bad.vmod");
        File.WriteAllText(p, "nope");
        Assert.Throws<InvalidDataException>(() => { using var _ = ModuleSession.Open(p, _tmp); });
    }
}
