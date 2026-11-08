using Quartermaster.Core;
using Quartermaster.DemoModule;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Docs.Tests;

/// <summary>The invented Riverland demo module and rules pack used by every wiki example.</summary>
public class DemoModuleTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-demo-" + Guid.NewGuid().ToString("N"));

    public DemoModuleTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (string Vmod, string Rules) Build(string name = "a") => DemoContent.Build(Path.Combine(_tmp, name));

    [Fact]
    public void Demo_vmod_loads()
    {
        var (vmod, _) = Build();
        using var archive = VmodArchive.Open(vmod);
        var log = new DiagnosticLog();
        var model = ModuleModelBuilder.Build(archive, log);
        Assert.Equal("Riverland", model.Name);
        var pieces = model.Slots.Count + model.Maps.SelectMany(m => m.SetupStacks).Sum(s => s.Pieces.Count);
        Assert.True(model.Slots.Count >= 12, $"palette has {model.Slots.Count} pieces");
        Assert.True(pieces >= 24, $"module has {pieces} pieces");
        Assert.Single(model.Maps);
        Assert.Equal(new[] { DemoContent.North, DemoContent.South }, model.Sides);
        Assert.IsType<ZonedGrid>(model.Maps[0].Boards.Single().Grid);
        Assert.DoesNotContain(log.Items, d => d.Severity == DiagnosticSeverity.Warning || d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Demo_pack_matches_module()
    {
        var (vmod, rules) = Build();
        var pack = RulesPackIo.Load(rules);
        Assert.Equal(VmodArchive.ComputeSha256(vmod), pack.ModuleSha256);
        using var archive = VmodArchive.Open(vmod);
        var model = ModuleModelBuilder.Build(archive, new DiagnosticLog());
        var issues = RulesValidator.Validate(pack, model.Maps.Select(m => m.Id).ToList());
        Assert.Empty(issues);
    }

    [Fact]
    public void Demo_formulas_evaluate()
    {
        var (vmod, rules) = Build();
        using var s = ModuleSession.Open(vmod, Path.Combine(_tmp, "cache"), null);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.StartNewGame(Difficulty.FullyAuto);
        s.OpenRules(rules);
        var state = s.Game!.State;
        var pack = s.Rules!;
        var units = state.Pieces.Values.Where(p => PieceInfo.Markers(p).TryGetValue("Kind", out var k) && k == "Unit").ToList();
        Assert.True(units.Count >= 12, $"{units.Count} units on the map");
        foreach (var f in pack.Formulas)
            foreach (var u in units)
            {
                var t = s.Runner!.Run(state, new RunRequest(f.Id, u.Id, new Dictionary<string, string>(), new Dictionary<string, string>(),
                    new Dictionary<string, IReadOnlyList<int>>(), Moved: 3, UseAutoValues: true));
                var who = $"{f.Name} on {PieceInfo.Name(u)}";
                Assert.True(t.Error == null, $"{who}: {t.Error}");
                Assert.All(t.Variables, v => Assert.True(v.MissingReason == null, $"{who}: {v.Variable} {v.MissingReason}"));
                Assert.NotNull(t.Result);
            }
        // Every unit is seeded from its property sheet, and one starts low on fuel so the alert shows at once.
        Assert.All(units, u => Assert.NotNull(Ledger.Get(state, LedgerKey.Piece(u.Id), "fuel")));
        var hits = AlertEngine.Evaluate(state, pack, s.Model, null, null);
        Assert.Contains(hits, h => h.Id == "a-low-fuel" && h.Matches.Count >= 1);
    }

    [Fact]
    public void Demo_is_deterministic()
    {
        var a = Build("a");
        var b = Build("b");
        Assert.Equal(File.ReadAllBytes(a.Vmod), File.ReadAllBytes(b.Vmod));
        Assert.Equal(File.ReadAllBytes(a.Rules), File.ReadAllBytes(b.Rules));
    }

    [Fact]
    public void Committed_demo_is_current()
    {
        var (vmod, rules) = Build();
        var demo = Path.Combine(RepoRoot(), "docs", "wiki", "demo");
        Assert.True(File.ReadAllBytes(vmod).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(demo, DemoContent.VmodName))),
            "docs/wiki/demo is stale: run dotnet run --project tools/Quartermaster.DemoModule -- docs/wiki/demo");
        Assert.True(File.ReadAllBytes(rules).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(demo, DemoContent.RulesName))),
            "docs/wiki/demo is stale: run dotnet run --project tools/Quartermaster.DemoModule -- docs/wiki/demo");
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Quartermaster.slnx"))) return d.FullName;
        throw new InvalidOperationException("Repository root (Quartermaster.slnx) not found.");
    }
}
