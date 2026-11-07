using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class ModuleSessionRulesTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-msr-" + Guid.NewGuid().ToString("N"));

    public ModuleSessionRulesTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static string Add(string id, string type, string state) =>
        "+/" + SequenceEncoder.Encode(new[] { id, type, state }, '/');

    private static FixtureModule Fixture(string tag = "")
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/>" + tag +
            "</VASSAL.build.GameModule>");
        var type = "propertysheet;0Fuel;Sheet;\tpiece;;;unit.svg;A";
        var state = "40\tMain Map;100;50;1;0";
        return new FixtureModule().WithBuildFile(xml)
            .AddSave("s.vsav", new[] { "begin_save", "", Add("p1", type, state), "end_save" });
    }

    private ModuleSession Open(FixtureModule fx, string? rulesRoot = null) =>
        ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), rulesRoot);

    private static RulesPack FuelPack(string module)
    {
        var p = RulesPackIo.CreateEmpty("T", module);
        p.Properties.Add(new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" });
        return p;
    }

    [Fact]
    public void NewRules_BoundToModuleHash()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        var changed = 0;
        s.RulesChanged += () => changed++;
        s.NewRules("Mine");
        Assert.Equal("Mine", s.Rules!.Name);
        Assert.Equal(s.Archive.Sha256, s.Rules.ModuleSha256);
        Assert.NotNull(s.Runner);
        Assert.Null(s.RulesPath);
        Assert.True(s.RulesDirty);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void SaveOpen_RoundTrip_SetsGameRulesSha()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("Mine");
        var path = Path.Combine(_tmp, "a.qrules");
        s.SaveRules(path);
        Assert.False(s.RulesDirty);
        Assert.Equal(path, s.RulesPath);
        var sha = RulesPackIo.ComputeSha256(s.Rules!);
        Assert.Equal(sha, s.Game!.RulesSha256);

        using var t = Open(fx);
        t.OpenRules(path);
        Assert.Equal("Mine", t.Rules!.Name);
        Assert.False(t.RulesDirty);
        Assert.Equal(path, t.RulesPath);
        Assert.DoesNotContain(t.Log.Items, d => d.Category == "rules");
    }

    [Fact]
    public void LoadRules_ModuleHashMismatch_LoadsWithWarning()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        var path = Path.Combine(_tmp, "other.qrules");
        RulesPackIo.Save(RulesPackIo.CreateEmpty("Other", new string('0', 64)), path);
        s.OpenRules(path);
        Assert.Equal("Other", s.Rules!.Name);
        Assert.Contains(s.Log.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Category == "rules"
            && d.Message == "Rules pack was made for a different module");
    }

    [Fact]
    public void OpenRules_SeedsLedgerFromSheetFields()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        var path = Path.Combine(_tmp, "f.qrules");
        RulesPackIo.Save(FuelPack(s.Archive.Sha256), path);
        s.OpenRules(path);
        Assert.Equal(40, Ledger.Get(s.Game!.State, "p:p1", "fuel"));
        // a new game replaced later is seeded as well
        s.LoadSetup(s.Model.Setups[0]);
        Assert.Equal(40, Ledger.Get(s.Game!.State, "p:p1", "fuel"));
    }

    [Fact]
    public void RulesMemory_RemembersAndAutoOpens()
    {
        using var fx = Fixture();
        var root = Path.Combine(_tmp, "root");
        string sha;
        var path = Path.Combine(_tmp, "m.qrules");
        using (var s = Open(fx, root))
        {
            sha = s.Archive.Sha256;
            s.NewRules("Remembered");
            s.SaveRules(path);
        }
        Assert.Equal(path, RulesMemory.LastFor(root, sha));
        using (var t = Open(fx, root))
        {
            Assert.Equal("Remembered", t.Rules!.Name);
            Assert.Equal(path, t.RulesPath);
        }
        // broken remembered pack: session still opens, with a warning
        File.WriteAllText(path, "junk");
        using var u = Open(fx, root);
        Assert.Null(u.Rules);
        Assert.Contains(u.Log.Items, d => d.Category == "rules" && d.Severity == DiagnosticSeverity.Warning);
        // corrupt index is treated as empty
        File.WriteAllText(Path.Combine(root, "rules-index.json"), "{{{");
        Assert.Null(RulesMemory.LastFor(root, sha));
    }

    [Fact]
    public void OpenRules_Corrupt_ThrowsInvalidData_SessionUnchanged()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.NewRules("Keep");
        var before = s.Rules;
        var bad = Path.Combine(_tmp, "bad.qrules");
        File.WriteAllText(bad, "nope");
        Assert.Throws<InvalidDataException>(() => s.OpenRules(bad));
        Assert.Same(before, s.Rules);
        Assert.Null(s.RulesPath);
    }

    [Fact]
    public void UpdateRules_ThrowingEdit_ChangesNothing_AndEditMarksDirty()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.NewRules("A");
        s.SaveRules(Path.Combine(_tmp, "u.qrules"));
        var changed = 0;
        s.RulesChanged += () => changed++;
        Assert.Throws<InvalidOperationException>(() => s.UpdateRules(_ => throw new InvalidOperationException()));
        Assert.False(s.RulesDirty);
        Assert.Equal(0, changed);
        var id = s.Rules!.Id;
        s.UpdateRules(p => p with { Name = "B" });
        Assert.True(s.RulesDirty);
        Assert.Equal("B", s.Rules!.Name);
        Assert.Equal(id, s.Rules.Id);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void LoadNative_RulesHashDiffers_Warns()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("A");
        s.SaveRules(Path.Combine(_tmp, "n.qrules"));
        var save = Path.Combine(_tmp, "n.qsave");
        s.SaveNative(save);
        Assert.DoesNotContain(s.Log.Items, d => d.Message.StartsWith("Save was made"));
        s.UpdateRules(p => p with { Name = "B" });
        s.SaveRules();
        s.LoadNative(save);
        Assert.Contains(s.Log.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Category == "rules"
            && d.Message == "Save was made with a different version of the rules pack");
    }

    [Fact]
    public void UpdateRules_AddedSheetProperty_SeedsWithoutReopen()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("A");
        Assert.Null(Ledger.Get(s.Game!.State, "p:p1", "fuel"));
        s.UpdateRules(p =>
        {
            p.Properties.Add(new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" });
            return p;
        });
        Assert.Equal(40, Ledger.Get(s.Game!.State, "p:p1", "fuel"));
    }

    [Fact]
    public void SaveNative_AfterEdit_StampsCurrentRulesHash()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("A");
        s.SaveRules(Path.Combine(_tmp, "h.qrules"));
        s.UpdateRules(p => p with { Name = "Edited" });
        var save = Path.Combine(_tmp, "h.qsave");
        s.SaveNative(save);
        Assert.Equal(RulesPackIo.ComputeSha256(s.Rules!), NativeSave.Read(save).RulesSha256);
    }

    [Fact]
    public void NewGames_GetDistinctGameIds_OnStateAndGenesis()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.StartNewGame();
        var a = s.Game!.State.GameId;
        Assert.Equal(32, a.Length);
        Assert.True(Guid.TryParseExact(a, "N", out _));
        Assert.Equal(a, s.Game.Genesis.GameId);
        s.LoadSetup(s.Model.Setups[0]);
        var b = s.Game!.State.GameId;
        Assert.Equal(32, b.Length);
        Assert.Equal(b, s.Game.Genesis.GameId);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LoadNative_SeedsMissingValues_WhenPackOpen()
    {
        using var fx = Fixture();
        using var s = Open(fx);
        s.LoadSetup(s.Model.Setups[0]);
        var save = Path.Combine(_tmp, "l.qsave");
        s.SaveNative(save); // no ledger values yet
        var path = Path.Combine(_tmp, "l.qrules");
        RulesPackIo.Save(FuelPack(s.Archive.Sha256), path);
        s.StartNewGame();
        s.OpenRules(path);
        s.LoadNative(save);
        Assert.Equal(40, Ledger.Get(s.Game!.State, "p:p1", "fuel"));
    }
}
