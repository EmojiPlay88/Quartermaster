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
        // Every documented feature appears once: side-owned depot holders, a global property, side-tied phases, a location variable.
        Assert.Equal(new[] { "north", "south" }, pack.Holders.Select(h => h.SideId));
        Assert.Contains(pack.Properties, p => p.Scope == PropertyScope.Holders);
        Assert.Contains(pack.Formulas, f => f.Outputs.Any(o => o.Target == OutputTarget.Holder));
        Assert.Contains(pack.Formulas, f => f.Outputs.Any(o => o.Target == OutputTarget.Global));
        Assert.Contains(pack.Formulas, f => f.Bindings.Any(b => b.Kind == BindingKind.Global));
        Assert.Contains(pack.Formulas, f => f.Bindings.Any(b => b.Kind == BindingKind.Location));
        Assert.All(pack.Phases, ph => Assert.NotNull(ph.SideId));
        Assert.Equal(new[] { "north", "south" }, pack.Phases.Select(ph => ph.SideId).Distinct());
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
                if (f.Outputs.Any(o => o.Target == OutputTarget.Holder))
                    Assert.Contains(t.Outputs, o => o.Key.StartsWith("h:", StringComparison.Ordinal));
            }
        Assert.Equal("0202", s.Runner!.Run(state, new RunRequest("f-position", units.First(u => PieceInfo.Name(u) == "NL 1 Rifles").Id,
            new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>())).Result!.Value.Display());
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

    /// <summary>
    /// .NET writes the OS into each central-directory entry (host 0 and attributes 0 on Windows; host 3 and mode 0644 elsewhere). The rules
    /// pack records the module's SHA-256, so the generator clears both: a Linux or macOS build must give the module a Windows build gives.
    /// </summary>
    [Fact]
    public void Demo_module_zip_headers_do_not_depend_on_the_OS()
    {
        var bytes = File.ReadAllBytes(Build().Vmod);
        var entries = CentralDirectory(bytes);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal((0, 0u), (bytes[e + 5], BitConverter.ToUInt32(bytes, e + 38))));

        // What a Unix build wrote before normalising: host 3 (Unix), external attributes S_IFREG | 0644.
        var unix = (byte[])bytes.Clone();
        foreach (var e in entries)
        {
            unix[e + 5] = 3;
            BitConverter.GetBytes(0x81A40000u).CopyTo(unix, e + 38);
        }
        Assert.NotEqual(bytes, unix);
        Assert.Equal(bytes, DemoContent.NormalizeZipHost(unix));
    }

    /// <summary>Offsets of the central-directory file headers (the zip is small: no zip64, no comment).</summary>
    private static List<int> CentralDirectory(byte[] z)
    {
        var eocd = z.Length - 22;
        Assert.Equal(0x06054b50u, BitConverter.ToUInt32(z, eocd));
        var list = new List<int>();
        var at = (int)BitConverter.ToUInt32(z, eocd + 16);
        for (var n = 0; n < BitConverter.ToUInt16(z, eocd + 10); n++)
        {
            Assert.Equal(0x02014b50u, BitConverter.ToUInt32(z, at));
            list.Add(at);
            at += 46 + BitConverter.ToUInt16(z, at + 28) + BitConverter.ToUInt16(z, at + 30) + BitConverter.ToUInt16(z, at + 32);
        }
        return list;
    }

    /// <summary>Compares entry names and contents, not raw zip bytes, so another Deflate implementation cannot make it fail.</summary>
    [Fact]
    public void Committed_demo_is_current()
    {
        var (vmod, rules) = Build();
        var demo = Path.Combine(RepoRoot(), "docs", "wiki", "demo");
        const string stale = "docs/wiki/demo is stale: run dotnet run --project tools/Quartermaster.DemoModule -- docs/wiki/demo";
        Assert.True(SameEntries(vmod, Path.Combine(demo, DemoContent.VmodName)), stale);
        Assert.True(SameEntries(rules, Path.Combine(demo, DemoContent.RulesName)), stale);
    }

    private static bool SameEntries(string a, string b)
    {
        var ea = Entries(a);
        var eb = Entries(b);
        return ea.Keys.SequenceEqual(eb.Keys) && ea.All(kv => kv.Value.AsSpan().SequenceEqual(eb[kv.Key]));
    }

    private static SortedDictionary<string, byte[]> Entries(string zipPath)
    {
        var d = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using var z = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var e in z.Entries)
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            d[e.FullName] = ms.ToArray();
        }
        return d;
    }

    internal static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Quartermaster.slnx"))) return d.FullName;
        throw new InvalidOperationException("Repository root (Quartermaster.slnx) not found.");
    }
}
