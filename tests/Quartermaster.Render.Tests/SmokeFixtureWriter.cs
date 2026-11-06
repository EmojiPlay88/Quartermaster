using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Not a real test: when QM_WRITE_SMOKE_FIXTURE names a folder it writes phases.vmod and phases.qrules there for the app smokes.
/// Does nothing when the variable is unset. The written files are never committed.
/// </summary>
public class SmokeFixtureWriter
{
    [Fact]
    public void WriteSmokeFixture_WhenEnvSet()
    {
        var dir = Environment.GetEnvironmentVariable("QM_WRITE_SMOKE_FIXTURE");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var fx = PhaseFixture.Module();
        var built = fx.Build();
        var vmod = Path.Combine(dir, "phases.vmod");
        File.Copy(built, vmod, true);
        string sha;
        using (var a = VmodArchive.Open(vmod)) sha = a.Sha256;
        RulesPackIo.Save(PhaseFixture.Pack(sha), Path.Combine(dir, "phases.qrules"));
        Assert.True(File.Exists(Path.Combine(dir, "phases.qrules")));
    }
}
