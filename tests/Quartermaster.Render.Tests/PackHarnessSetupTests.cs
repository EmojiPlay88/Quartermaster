using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>PackHarness.NewGame with a named setup (the Riverland demo has none). Invented PhaseFixture data.</summary>
public class PackHarnessSetupTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-harness-setup-" + Guid.NewGuid().ToString("N"));

    public PackHarnessSetupTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    [Fact]
    public void NewGame_with_setup_name_loads_it_ignoring_case()
    {
        using var fx = PhaseFixture.Module(true);
        var vmod = fx.Build();
        string sha;
        using (var a = Quartermaster.Vassal.VmodArchive.Open(vmod)) sha = a.Sha256;
        var rules = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(PhaseFixture.Pack(sha), rules);
        using var h = PackHarness.Open(vmod, rules, Path.Combine(_tmp, "cache"));
        var name = h.Session.Model.Setups[0].Name;
        Assert.Same(h, h.NewGame(name.ToUpperInvariant(), Difficulty.SemiAuto));
        Assert.Equal(Difficulty.SemiAuto, h.Session.Game!.State.Difficulty);
        Assert.Contains(h.Log, l => l.Text.StartsWith($"Setup \"{name}\" loaded"));
    }
}
