using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B8 alliances: invented Riverland pack on the PhaseFixture module. The Northern League (<c>north</c>, "Red 1") and the Hill Clans
/// (<c>hills</c>, "Red 2") are in alliance <c>Upriver Pact</c>; the Southern Compact (<c>south</c>, "Blue 1") is alone. Holders NorthDepot,
/// HillCamp and SouthDepot keep a Private Supply. Phases: Weather (no side; River toll when sealed), Supply (north), Movement (no side), Combat
/// (hills), End (south). Temp roots only.
/// </summary>
public abstract class AllianceTestBase : IDisposable
{
    protected readonly string Tmp = Path.Combine(Path.GetTempPath(), "qm-b8ally-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    protected const string Pact = "Upriver Pact";
    protected const string HillMap = "Hill Lookout";
    protected const double CampSupply = 47474;
    protected const double SouthSupply = 52525;
    protected const string OtherSideUnit = "That unit belongs to a side you don't hold.";
    protected const string AlreadyIn = "Your sealed answer is already in.";

    protected static readonly PlayerDef Alice = new("p1", "Alice", new[] { "north" });
    protected static readonly PlayerDef Cara = new("p3", "Cara", new[] { "hills" });
    protected static readonly PlayerDef Bob = new("p2", "Bob", new[] { "south" });
    protected static IReadOnlySet<string> North => new HashSet<string> { "north" };
    protected static IReadOnlySet<string> Hills => new HashSet<string> { "hills" };
    protected static IReadOnlySet<string> South => new HashSet<string> { "south" };

    protected AllianceTestBase() => Directory.CreateDirectory(Tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(Tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    protected static RulesPack Pack(string sha, bool alliance = true, bool sealedToll = false)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"Red 1\")", AllianceId = alliance ? Pact : null },
                new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"Blue\")" },
                new SideDef { Id = "hills", Name = "Hill Clans", UnitFilter = "startsWith(name(), \"Red 2\")", AllianceId = alliance ? " upriver pact " : null },
            },
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "p-supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
                new PropertyDef { Id = "p-gate", Name = "Gate", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Public },
                new PropertyDef { Id = "p-bid", Name = "TollBid", Scope = PropertyScope.Holders, KeyedBy = "table:t-sides", Holders = new() { "global" }, Visibility = Visibility.Public },
            }).ToList(),
            Holders = new()
            {
                new HolderDef { Id = "northdepot", Name = "NorthDepot", SideId = "north" },
                new HolderDef { Id = "hillcamp", Name = "HillCamp", SideId = "hills" },
                new HolderDef { Id = "southdepot", Name = "SouthDepot", SideId = "south" },
            },
            MapOwners = new() { new MapOwner { MapId = HillMap, SideId = "hills" } },
            Alerts = new()
            {
                Alert("north", "Red 1"), Alert("hills", "Red 2"), Alert("south", "Blue"),
            },
            Tables = p.Tables.Append(new TableDef
            {
                Id = "t-sides", Name = "Toll sides", Kind = TableKind.OneD, RowKeys = new() { "Northern League", "Southern Compact", "Hill Clans" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } },
            }).ToList(),
            Formulas = p.Formulas.Select(f => f.Id switch
            {
                "f-weather" => f with { PrivateRolls = true },
                "f-fuel" => f with { Bindings = f.Bindings.Select(b => b.Variable == "Terrain" ? b with { AutoValue = null } : b).ToList() },
                _ => f,
            }).Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-camp", Name = "Camp count", Text = "S",
                    Bindings = new() { new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "hillcamp", Property = "p-supply" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-gate", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-gate", Name = "Open gate", Text = "1",
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-gate", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-toll", Name = "Toll bid", Text = "bid",
                    Bindings = new() { new VariableBinding { Variable = "bid", Kind = BindingKind.Input, Min = 0, Max = 10 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-bid", Op = OutputOp.Set, Key = "askside()" } },
                },
            }).ToList(),
            Phases = new()
            {
                new PhaseDef
                {
                    Id = "ph-weather", Name = sealedToll ? "River toll" : "Weather",
                    OnStart = sealedToll ? new() { new StartEntry { FormulaId = "f-toll", ForEach = ForEachMode.Once, Sealed = true } } : new(),
                },
                new PhaseDef { Id = "ph-supply", Name = "Supply", SideId = "north" },
                new PhaseDef
                {
                    Id = "ph-move", Name = "Movement",   // no side: every moved unit's run is queued, whoever may answer it
                    OnMove = new() { new MoveEntry { FormulaId = "f-fuel" } },
                },
                new PhaseDef { Id = "ph-combat", Name = "Combat", SideId = "hills", Checklist = new() { "Resolve attacks" } },
                new PhaseDef { Id = "ph-end", Name = "End", SideId = "south" },
            },
            Blocks = new(),
        };
    }

    private static AlertDef Alert(string side, string prefix) =>
        new() { Id = "a-" + side, Name = side + " alert", Condition = $"startsWith(name(), \"{prefix}\")", Message = "here", Severity = AlertSeverity.Warning, SideId = side };

    /// <summary>A session with the pack applied, three players (or <paramref name="players"/>), dice always 4, and HillCamp/SouthDepot supply set.</summary>
    protected ModuleSession Session(bool alliance = true, bool sealedToll = false, PlayerDef[]? players = null, Difficulty d = Difficulty.FullyAuto)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(Tmp, "cache" + _sessions.Count), Path.Combine(Tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d, players ?? new[] { Alice, Bob, Cara });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, alliance, sealedToll));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("hillcamp"), "p-supply", 0, CampSupply, "manual", null, null, null, null));
        s.Game.Execute(new LedgerDelta(LedgerKey.Holder("southdepot"), "p-supply", 0, SouthSupply, "manual", null, null, null, null));
        return s;
    }

    /// <summary>A guest-mode session on the same module (no game until a snapshot arrives).</summary>
    protected ModuleSession GuestSession()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(Tmp, "gcache" + _sessions.Count), Path.Combine(Tmp, "groot" + _sessions.Count));
        _sessions.Add(s);
        s.EnterGuestMode();
        return s;
    }

    protected static PhaseController Controller(ModuleSession s) => new(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));

    protected static double? Supply(GameState st, string holder) => Ledger.Get(st, LedgerKey.Holder(holder), "p-supply");

    protected static double? Gate(GameState st) => Ledger.Get(st, LedgerKey.Holder(HolderDef.GlobalId), "p-gate");

    /// <summary>Start, then Next Phase until <paramref name="phaseId"/> is current.</summary>
    protected static void To(ModuleSession s, PhaseController c, string phaseId)
    {
        c.Start();
        for (var i = 0; i < 10 && s.Game!.State.Position?.PhaseId != phaseId; i++) c.Advance();
        Assert.Equal(phaseId, s.Game!.State.Position?.PhaseId);
    }
}
