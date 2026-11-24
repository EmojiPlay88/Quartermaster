using System.Reflection;
using Quartermaster.Core;
using Quartermaster.DemoModule;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Docs.Tests;

/// <summary>PackHarness against the invented Riverland demo module and pack.</summary>
public class PackHarnessTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-harness-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<PackHarness> _open = new();

    public PackHarnessTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var h in _open) h.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PackHarness Harness(Func<RulesPack, RulesPack>? tweak = null, Difficulty d = Difficulty.FullyAuto)
    {
        var (vmod, rules) = DemoContent.Build(Path.Combine(_tmp, "demo" + _open.Count));
        if (tweak != null) RulesPackIo.Save(tweak(RulesPackIo.Load(rules)), rules);
        var h = PackHarness.Open(vmod, rules, Path.Combine(_tmp, "cache"));
        _open.Add(h);
        return h.NewGame(null, d);
    }

    private static InvalidOperationException Thrown(Action a) => Assert.Throws<InvalidOperationException>(a);

    [Fact]
    public void Harness_runs_demo_formula_and_reads_value()
    {
        var h = Harness();
        Assert.Equal(3, h.Value("NL 1 Rifles", "Fuel"));
        var run = h.Run("f-fuel-supply", "NL 1 Rifles");   // Hexes auto 2: lookup 1-2 -> +2
        Assert.True(run.Applied, run.Error);
        Assert.False(run.Refused);
        Assert.Null(run.Error);
        Assert.Equal("5", run.Result);
        Assert.Empty(run.Missing);
        Assert.Equal(5, h.Value("NL 1 Rifles", "Fuel"));
        var delivery = h.Run("f-north-delivery", dice: new[] { 4 });   // the depot starts at its default, 20
        Assert.True(delivery.Applied, delivery.Error);
        Assert.Equal(24, h.Value("North Depot", "Stores"));
        Assert.Contains(h.Log, l => l.Text.Contains("→ Fuel = 5"));
    }

    [Fact]
    public void Harness_inputs_and_missing()
    {
        var h = Harness();
        var run = h.Run("f-fuel-supply", "NL 1 Rifles", new Dictionary<string, string> { ["Hexes"] = "0" });   // lookup 0 -> +3, capped at 6
        Assert.True(run.Applied, run.Error);
        Assert.Equal(6, h.Value("NL 1 Rifles", "Fuel"));
        // No unit: the unit variable cannot be filled; nothing changes.
        var none = h.Run("f-ammo-supply");
        Assert.False(none.Applied);
        Assert.Contains("Ammo", none.Missing);
        Assert.Equal(3, h.Value("NL 1 Rifles", "Ammo"));
    }

    [Fact]
    public void Harness_dice_pinned_and_run_out_throws()
    {
        var h = Harness();
        var run = h.Run("f-attack", "NL 1 Rifles", dice: new[] { 5 });   // 5 + floor(3 / 2)
        Assert.True(run.Applied, run.Error);
        Assert.Equal("6", run.Result);
        Assert.Equal(1, run.DiceUsed);
        var w = h.Run("f-weather", dice: new[] { 6 });
        Assert.True(w.Applied, w.Error);
        Assert.Equal(2, h.Value("Global", "Weather"));
        var ex = Thrown(() => h.Run("f-attack", "NL 1 Rifles", dice: Array.Empty<int>()));
        Assert.Equal("The harness ran out of dice (formula \"Attack roll\")", ex.Message);
    }

    [Fact]
    public void Harness_advance_queues_and_flags_errors()
    {
        var semi = Harness(d: Difficulty.SemiAuto);
        Assert.Null(semi.Phase);
        semi.Advance();
        Assert.Equal("North Supply", semi.Phase);
        Assert.NotEmpty(semi.Pending);

        // A pack whose Ammo supply always fails: every unit's auto-run line carries the flag.
        var bad = Harness(p => p with { Formulas = p.Formulas.Select(f => f.Id == "f-ammo-supply" ? f with { Text = "Ammo / 0" } : f).ToList() });
        bad.Advance();
        Assert.Contains(bad.Log, l => l.Error && l.Text.StartsWith("Ammo supply: NL "));
        Assert.DoesNotContain(bad.Log, l => l.Error && l.Text.StartsWith("Fuel supply"));
    }

    [Fact]
    public void Harness_unknown_and_ambiguous_names_throw()
    {
        var h = Harness();
        Assert.Equal("No setup named \"Nope\"", Thrown(() => h.NewGame("Nope")).Message);
        Assert.Equal("No piece named \"Ghost\"", Thrown(() => h.Run("f-position", "Ghost")).Message);
        Assert.Equal("No holder or piece named \"Ghost\"", Thrown(() => h.Value("Ghost", "Fuel")).Message);
        Assert.Equal("No property named \"Nothing\"", Thrown(() => h.Value("NL 1 Rifles", "Nothing")).Message);

        var state = h.Session.Game!.State;
        var src = state.Pieces.Values.First(p => PieceInfo.Name(p) == "NL 2 Rifles");
        state.Pieces["dup"] = new PieceInstance { Id = "dup", TypeString = src.TypeString, TraitStates = src.TraitStates.ToList(), MapId = src.MapId, Pos = src.Pos };
        Assert.Equal("2 pieces are named \"NL 2 Rifles\"; use a unique name", Thrown(() => h.Run("f-position", "NL 2 Rifles")).Message);
        Assert.Equal("2 pieces are named \"NL 2 Rifles\"; use a unique name", Thrown(() => h.Value("NL 2 Rifles", "Fuel")).Message);
    }

    [Fact]
    public void Harness_ViewFor_hides_other_side()
    {
        var h = Harness();
        Assert.Equal(3, h.Value("SC 1 Rifles", "Readiness"));   // the harness is full-state
        var south = h.Session.Game!.State.Pieces.Values.First(p => PieceInfo.Name(p) == "SC 1 Rifles").Id;
        var northView = h.ViewFor("north");
        Assert.True(northView.IsPlayerView);
        Assert.False(northView.Ledgers.TryGetValue(LedgerKey.Piece(south), out var inner) && inner.ContainsKey("readiness"));
        var southView = h.ViewFor("south");
        Assert.True(southView.Ledgers[LedgerKey.Piece(south)].ContainsKey("readiness"));
        // The full state is untouched.
        Assert.True(h.Session.Game.State.Ledgers[LedgerKey.Piece(south)].ContainsKey("readiness"));
    }

    [Fact]
    public void Harness_public_surface_is_pinned()
    {
        static IEnumerable<string> Surface(Type t) => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is not MethodInfo { IsSpecialName: true })
            .Select(m => m switch
            {
                MethodInfo mi => $"{(mi.IsStatic ? "static " : "")}{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                PropertyInfo pi => $"{pi.PropertyType.Name} {pi.Name}",
                ConstructorInfo ci => $".ctor({string.Join(", ", ci.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                _ => m.MemberType + " " + m.Name,
            })
            .OrderBy(x => x, StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "static PackHarness Open(String vmodPath, String rulesPath, String cacheDir)",
            "HarnessRun Run(String formulaId, String pieceName, IReadOnlyDictionary`2 inputs, IReadOnlyList`1 dice)",
            "GameState ViewFor(String[] sideIds)",
            "IReadOnlyList`1 Log",
            "IReadOnlyList`1 Pending",
            "ModuleSession Session",
            "PackHarness NewGame(String setup, Difficulty difficulty)",
            "String Advance()",
            "String Phase",
            "Double? Value(String holderOrPieceName, String propertyName)",
            "Void Dispose()",
        }.Select(x => x.Replace("Double?", "Nullable`1")).OrderBy(x => x, StringComparer.Ordinal), Surface(typeof(PackHarness)));

        Assert.Equal(new[]
        {
            "Boolean Applied", "Boolean Refused", "Int32 DiceUsed", "IReadOnlyList`1 Missing", "String Error", "String Result", "String Working",
        }.OrderBy(x => x, StringComparer.Ordinal), Surface(typeof(HarnessRun))
            .Where(x => !x.Contains("Equals") && !x.Contains("GetHashCode") && !x.Contains("ToString") && !x.Contains("Deconstruct") && !x.Contains("PrintMembers") && !x.StartsWith(".ctor") && !x.Contains("Clone") && !x.Contains("EqualityContract")));
    }
}
