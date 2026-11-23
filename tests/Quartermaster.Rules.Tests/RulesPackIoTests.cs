using System.IO.Compression;
using System.Text.Json;
using Quartermaster.Rules;

namespace Quartermaster.Rules.Tests;

public class RulesPackIoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qrules-" + Guid.NewGuid().ToString("N"));

    public RulesPackIoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RulesPack Sample()
    {
        var p = RulesPackIo.CreateEmpty("Sample", "abc123");
        return p with
        {
            Properties = new List<PropertyDef> { new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both, Default = 3, SheetField = "Fuel", AppliesTo = "x > 1", Notes = "n" } },
            Holders = new List<HolderDef> { new HolderDef { Id = "h1", Name = "Depot", Notes = "d" } },
            RuleRefs = new List<RuleRef> { new RuleRef { Id = "r1", Label = "Section 1", DocumentEntry = "rules.pdf", Page = 4 } },
            Tables = new List<TableDef>
            {
                new TableDef { Id = "t2", Name = "Rates", Kind = TableKind.TwoD, RowKeys = new() { "a", "b" }, ColumnKeys = new() { "x", "y" },
                    Cells = new() { new() { "1", "2" }, new() { "3", "4" } }, RuleRefId = "r1", Notes = "tn" },
                new TableDef { Id = "t1", Name = "Dice", Kind = TableKind.Dice, RowKeys = new() { "2" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "5" } } },
            },
            Formulas = new List<FormulaDef>
            {
                new FormulaDef { Id = "f1", Name = "Cost", Text = "a * 2", AppliesTo = "true", RuleRefId = "r1", Notes = "fn",
                    Bindings = new List<VariableBinding> { new VariableBinding { Variable = "a", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6, Min = 0, Max = 9, Choices = new() { "q" }, Caption = "c", Property = "Fuel", HolderId = "h1", TableId = "t1", RowExpr = "1", ColExpr = "2" } },
                    Outputs = new List<FormulaOutput> { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "Fuel", Op = OutputOp.Subtract } } },
            },
        };
    }

    private static string J(RulesPack p) => JsonSerializer.Serialize(p, RulesPackIo.JsonOptions);

    [Fact]
    public void SaveLoad_RoundTrip_PreservesEverything()
    {
        var zip = Path.Combine(_dir, "a.qrules");
        var p = Sample();
        RulesPackIo.Save(p, zip);
        var l = RulesPackIo.Load(zip);
        Assert.Equal(J(p with { Tables = p.Tables.OrderBy(t => t.Id).ToList() }), J(l));
        Assert.False(File.Exists(zip + ".tmp"));
    }

    [Fact]
    public void AlwaysAsk_round_trips_and_default_is_not_written()
    {
        var p = Sample();
        var f = p.Formulas[0];
        var asked = p with { Formulas = new() { f with { Bindings = new() { f.Bindings[0] with { Kind = BindingKind.Input, AlwaysAsk = true } } } } };
        var zip = Path.Combine(_dir, "ask.qrules");
        RulesPackIo.Save(asked, zip);
        Assert.True(RulesPackIo.Load(zip).Formulas[0].Bindings[0].AlwaysAsk);

        var plain = JsonSerializer.Serialize(f, RulesPackIo.JsonOptions);
        Assert.DoesNotContain("alwaysAsk", plain, StringComparison.OrdinalIgnoreCase);
        var loaded = JsonSerializer.Deserialize<FormulaDef>(plain, RulesPackIo.JsonOptions)!;
        Assert.False(loaded.Bindings[0].AlwaysAsk);
        Assert.Contains("alwaysAsk", JsonSerializer.Serialize(asked.Formulas[0], RulesPackIo.JsonOptions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlwaysAsk_unset_keeps_the_pack_hash()
    {
        var a = Path.Combine(_dir, "h1.qrules");
        var b = Path.Combine(_dir, "h2.qrules");
        RulesPackIo.Save(Sample(), a);
        RulesPackIo.Save(RulesPackIo.Load(a), b);
        Assert.Equal(RulesPackIo.ComputeSha256(RulesPackIo.Load(a)), RulesPackIo.ComputeSha256(RulesPackIo.Load(b)));
        Assert.DoesNotContain("alwaysAsk", J(RulesPackIo.Load(a)), StringComparison.OrdinalIgnoreCase);
    }

    private static RulesPack PhasedPack() => new()
    {
        Format = RulesPackIo.CurrentFormat, Id = "fixed-pack", Name = "Phased", ModuleSha256 = "abc123",
        Phases = new()
        {
            new PhaseDef { Id = "a", Name = "Alpha" },
            new PhaseDef { Id = "b", Name = "Beta", Warnings = new() { new PhaseWarning { Condition = "true", Message = "m" } } },
        },
    };

    private static RulesPack HookedPack() => PhasedPack() with
    {
        Formulas = new() { new FormulaDef { Id = "f1", Name = "Supply roll", Text = "1" } },
        Phases = new()
        {
            new PhaseDef { Id = "a", Name = "Alpha", OnStart = new() { new StartEntry { FormulaId = "f1" } }, OnMove = new() { new MoveEntry { FormulaId = "f1" } } },
        },
    };

    [Fact]
    public void Pack_without_new_fields_keeps_hash()
    {
        var p = HookedPack();
        // Pinned with the code before OnEnd / FiresFor / Phases existed.
        Assert.Equal("0b02149a4e0b3372417b625978000d7d2521b0483d4bbc55feb36d4dfbc27dc7", RulesPackIo.ComputeSha256(p));
        var json = JsonSerializer.Serialize(p.Phases[0], RulesPackIo.JsonOptions) + JsonSerializer.Serialize(p.Formulas[0], RulesPackIo.JsonOptions)
                   + JsonSerializer.Serialize(p.Phases[0].OnMove[0], RulesPackIo.JsonOptions);
        Assert.DoesNotContain("onEnd", json);
        Assert.DoesNotContain("firesFor", json);
        Assert.DoesNotContain("\"phases\"", JsonSerializer.Serialize(p.Formulas[0], RulesPackIo.JsonOptions));
    }

    [Fact]
    public void Pack_without_guards_keeps_hash()
    {
        var p = HookedPack();
        // Pinned with the code before Precondition / RefusalMessage / When existed.
        Assert.Equal("0b02149a4e0b3372417b625978000d7d2521b0483d4bbc55feb36d4dfbc27dc7", RulesPackIo.ComputeSha256(p));
        var json = JsonSerializer.Serialize(p.Formulas[0] with { Bindings = new() { new VariableBinding { Variable = "x", Kind = BindingKind.Input } } }, RulesPackIo.JsonOptions);
        Assert.DoesNotContain("precondition", json);
        Assert.DoesNotContain("refusalMessage", json);
        Assert.DoesNotContain("when", json);
    }

    [Fact]
    public void Guards_round_trip()
    {
        var p = HookedPack();
        p = p with { Formulas = new() { p.Formulas[0] with
        {
            Precondition = "Fuel > 0",
            RefusalMessage = "No fuel",
            Bindings = new() { new VariableBinding { Variable = "Fuel", Kind = BindingKind.Input, When = "1 = 1" } },
        } } };
        var path = Path.Combine(_dir, "guards.qrules");
        RulesPackIo.Save(p, path);
        var l = RulesPackIo.Load(path);
        Assert.Equal("Fuel > 0", l.Formulas[0].Precondition);
        Assert.Equal("No fuel", l.Formulas[0].RefusalMessage);
        Assert.Equal("1 = 1", l.Formulas[0].Bindings[0].When);
        Assert.Equal(RulesPackIo.ComputeSha256(p), RulesPackIo.ComputeSha256(l));
        var json = JsonSerializer.Serialize(p.Formulas[0], RulesPackIo.JsonOptions);
        Assert.Contains("\"precondition\"", json);
        Assert.Contains("\"refusalMessage\"", json);
        Assert.Contains("\"when\"", json);
    }

    [Fact]
    public void New_fields_round_trip()
    {
        var p = HookedPack();
        p = p with
        {
            Formulas = new() { p.Formulas[0] with { Phases = new() { "a" } } },
            Phases = new() { p.Phases[0] with
            {
                OnEnd = new() { new StartEntry { FormulaId = "f1", ForEach = ForEachMode.Units, UnitFilter = "Fuel > 1" } },
                OnMove = new() { new MoveEntry { FormulaId = "f1", FiresFor = MoveSide.Other } },
            } },
        };
        var path = Path.Combine(_dir, "hooks.qrules");
        RulesPackIo.Save(p, path);
        var l = RulesPackIo.Load(path);
        Assert.Equal(new[] { "a" }, l.Formulas[0].Phases);
        Assert.Equal("f1", l.Phases[0].OnEnd![0].FormulaId);
        Assert.Equal(ForEachMode.Units, l.Phases[0].OnEnd![0].ForEach);
        Assert.Equal(MoveSide.Other, l.Phases[0].OnMove[0].FiresFor);
        Assert.Equal(RulesPackIo.ComputeSha256(p), RulesPackIo.ComputeSha256(l));
        var any = JsonSerializer.Serialize(p.Phases[0].OnMove[0] with { FiresFor = MoveSide.Any }, RulesPackIo.JsonOptions);
        Assert.Contains("firesFor", any);
        Assert.Contains("\"any\"", any);
    }

    [Fact]
    public void Condition_unset_keeps_pack_hash()
    {
        var p = PhasedPack();
        // Pinned before PhaseDef.Condition existed: a pack without conditions hashes exactly as it did.
        Assert.Equal("a8c3ea882b4b8db72be9a6b462c31fc9f8afd697561b592ce773dfac7b72a4d6", RulesPackIo.ComputeSha256(p));
        var a = Path.Combine(_dir, "c1.qrules");
        RulesPackIo.Save(p, a);
        var l = RulesPackIo.Load(a);
        Assert.Equal(RulesPackIo.ComputeSha256(p), RulesPackIo.ComputeSha256(l));
        Assert.DoesNotContain("condition", JsonSerializer.Serialize(l.Phases[0], RulesPackIo.JsonOptions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Condition_round_trips()
    {
        var p = PhasedPack();
        p = p with { Phases = new() { p.Phases[0] with { Condition = "mod(turn(), 2) == 1" }, p.Phases[1] } };
        var a = Path.Combine(_dir, "c2.qrules");
        RulesPackIo.Save(p, a);
        var l = RulesPackIo.Load(a);
        Assert.Equal("mod(turn(), 2) == 1", l.Phases[0].Condition);
        Assert.Null(l.Phases[1].Condition);
        Assert.Equal(RulesPackIo.ComputeSha256(p), RulesPackIo.ComputeSha256(l));
        Assert.NotEqual(RulesPackIo.ComputeSha256(PhasedPack()), RulesPackIo.ComputeSha256(l));
    }

    [Fact]
    public void Load_Folder_SameAsZip()
    {
        var zip = Path.Combine(_dir, "a.qrules");
        RulesPackIo.Save(Sample(), zip);
        var folder = Path.Combine(_dir, "f");
        ZipFile.ExtractToDirectory(zip, folder);
        File.WriteAllText(Path.Combine(folder, "extra.txt"), "ignored");
        Assert.Equal(J(RulesPackIo.Load(zip)), J(RulesPackIo.Load(folder)));
    }

    [Fact]
    public void Hash_StableAcrossSaveLoad_AndChangesWhenTableEdited()
    {
        var p = Sample();
        var h = RulesPackIo.ComputeSha256(p);
        var zip = Path.Combine(_dir, "a.qrules");
        RulesPackIo.Save(p, zip);
        Assert.Equal(h, RulesPackIo.ComputeSha256(RulesPackIo.Load(zip)));
        var zip2 = Path.Combine(_dir, "b.qrules");
        RulesPackIo.Save(p, zip2);
        Assert.Equal(File.ReadAllBytes(zip), File.ReadAllBytes(zip2));
        var edited = p with { Tables = p.Tables.Select(t => t.Id == "t2" ? t with { Cells = new() { new() { "1", "2" }, new() { "3", "9" } } } : t).ToList() };
        Assert.NotEqual(h, RulesPackIo.ComputeSha256(edited));
    }

    [Fact]
    public void Load_CorruptJson_ThrowsInvalidData()
    {
        var zip = Path.Combine(_dir, "a.qrules");
        RulesPackIo.Save(Sample(), zip);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var e = z.Entries.First(x => x.FullName.StartsWith("tables/")).FullName;
            z.GetEntry(e)!.Delete();
            using var w = new StreamWriter(z.CreateEntry("tables/bad.json").Open());
            w.Write("{ not json");
        }
        var ex = Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(zip));
        Assert.Contains("tables/bad.json", ex.Message);
    }

    [Fact]
    public void Load_FutureFormat_ThrowsInvalidData()
    {
        var folder = Path.Combine(_dir, "f");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), "{\"format\":99,\"id\":\"x\",\"name\":\"n\"}");
        var ex = Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(folder));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void Load_MissingPackJson_AndDuplicateIds_Throw()
    {
        var folder = Path.Combine(_dir, "f");
        Directory.CreateDirectory(folder);
        Assert.Contains("pack.json missing", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(folder)).Message);
        File.WriteAllText(Path.Combine(folder, "pack.json"),
            "{\"format\":1,\"id\":\"x\",\"name\":\"n\",\"holders\":[{\"id\":\"dup\",\"name\":\"a\"},{\"id\":\"dup\",\"name\":\"b\"}]}");
        Assert.Contains("dup", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(folder)).Message);
    }

    [Fact]
    public void Load_NullCollections_NormalisedToEmpty()
    {
        var folder = Path.Combine(_dir, "f");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), "{\"format\":1,\"id\":\"x\",\"name\":\"n\",\"properties\":null,\"tables\":null}");
        var p = RulesPackIo.Load(folder);
        Assert.NotNull(p.Properties);
        Assert.Empty(p.Tables);
    }

    [Fact]
    public void Lookups_AreCaseInsensitive()
    {
        var p = Sample();
        Assert.Equal("t2", p.TableByName("rATES")!.Id);
        Assert.Equal("p1", p.PropertyByName("FUEL")!.Id);
        Assert.Equal("h1", p.HolderByName("depot")!.Id);
        Assert.Equal("f1", p.ById<FormulaDef>("f1")!.Id);
        Assert.Null(p.TableByName("nope"));
    }

    private string FolderWith(string packJson, params (string Path, string Json)[] extra)
    {
        var folder = Path.Combine(_dir, "g" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), packJson);
        foreach (var (p, j) in extra)
        {
            var full = Path.Combine(folder, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, j);
        }
        return folder;
    }

    [Fact]
    public void Load_FutureFormatWithChangedSchema_SaysNewer()
    {
        var f = FolderWith("{\"format\":99,\"properties\":\"now a string\"}");
        Assert.Contains("newer", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
    }

    [Theory]
    [InlineData("{\"format\":0,\"id\":\"x\"}")]
    [InlineData("{\"id\":\"x\"}")]
    public void Load_BadFormat_Unsupported(string json)
    {
        var f = FolderWith(json);
        Assert.Contains("unsupported format", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
    }

    [Fact]
    public void Load_NullId_Throws()
    {
        var f = FolderWith("{\"format\":1}", ("tables/x.json", "{\"id\":null,\"name\":\"n\"}"));
        Assert.Contains("tables/x.json", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
        var g = FolderWith("{\"format\":1,\"holders\":[{\"name\":\"n\"}]}");
        Assert.Contains("holder", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(g)).Message);
    }

    [Fact]
    public void Load_NumericEnum_Throws()
    {
        var f = FolderWith("{\"format\":1}", ("tables/x.json", "{\"id\":\"x\",\"kind\":1}"));
        Assert.Contains("tables/x.json", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
    }

    [Fact]
    public void Save_DuplicateTableIds_Throws_NoTmpLeft()
    {
        var p = Sample();
        p = p with { Tables = new List<TableDef> { new TableDef { Id = "a" }, new TableDef { Id = "a" } } };
        var zip = Path.Combine(_dir, "d.qrules");
        Assert.Throws<InvalidOperationException>(() => RulesPackIo.Save(p, zip));
        Assert.False(File.Exists(zip + ".tmp"));
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void Save_IdsDifferingByCase_Throws()
    {
        var p = Sample() with { Tables = new List<TableDef> { new TableDef { Id = "A" }, new TableDef { Id = "a" } } };
        Assert.Throws<InvalidOperationException>(() => RulesPackIo.Save(p, Path.Combine(_dir, "c.qrules")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("con")]
    [InlineData("x.")]
    [InlineData("sp ace")]
    public void Save_UnsafeId_Throws(string id)
    {
        var p = Sample() with { Tables = new List<TableDef> { new TableDef { Id = id } } };
        Assert.Throws<InvalidOperationException>(() => RulesPackIo.Save(p, Path.Combine(_dir, "u.qrules")));
    }

    private static PhaseDef Ph(string id) => new PhaseDef
    {
        Id = id, Name = "Phase " + id, SideId = "s1", Instructions = "do " + id, RuleRefId = "r1",
        Checklist = new() { "one", "two" },
        OnStart = new() { new StartEntry { FormulaId = "f1", ForEach = ForEachMode.Units, UnitFilter = "x > 1" } },
        OnMove = new() { new MoveEntry { FormulaId = "f1", UnitFilter = "y" } },
        Warnings = new() { new PhaseWarning { Condition = "a > b", Message = "{unit} bad", IsDecision = true } },
    };

    [Fact]
    public void Format2_RoundTrip_PhasesSidesBlocksInOrder()
    {
        var p = Sample() with
        {
            Sides = new() { new SideDef { Id = "z", Name = "Zed", UnitFilter = "s == 1" }, new SideDef { Id = "a", Name = "Ay" } },
            Phases = new() { Ph("C"), Ph("A"), Ph("B") },
            Blocks = new() { new PhaseBlock { FirstPhaseId = "C", LastPhaseId = "B", Count = 3, Label = "Round" } },
        };
        var zip = Path.Combine(_dir, "p.qrules");
        RulesPackIo.Save(p, zip);
        var l = RulesPackIo.Load(zip);
        Assert.Equal(4, l.Format);
        Assert.Equal(new[] { "C", "A", "B" }, l.Phases.Select(x => x.Id));
        Assert.Equal(new[] { "z", "a" }, l.Sides.Select(x => x.Id));
        Assert.Equal(J(p with { Tables = p.Tables.OrderBy(t => t.Id).ToList() }), J(l));
        Assert.Equal(ForEachMode.Units, l.Phases[0].OnStart[0].ForEach);
        Assert.True(l.Phases[0].Warnings[0].IsDecision);
        Assert.Same(l.Phases[1], l.ById<PhaseDef>("A"));
        Assert.Equal("Ay", l.ById<SideDef>("a")!.Name);
    }

    [Fact]
    public void Format1_Loads_NoPhases()
    {
        var f = FolderWith("{\"format\":1,\"id\":\"x\",\"name\":\"n\"}");
        var p = RulesPackIo.Load(f);
        Assert.Empty(p.Sides);
        Assert.Empty(p.Phases);
        Assert.Empty(p.Blocks);
    }

    [Fact]
    public void Format3_RoundTrip_AlertsOutlinerNotes()
    {
        var p = Sample() with
        {
            Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel", Outliner = OutlinerAggregate.Sum } },
            Phases = new() { Ph("A") with { Notes = "phase note" } },
            Alerts = new()
            {
                new AlertDef { Id = "a1", Name = "Low fuel", Condition = "Fuel < 3", Message = "Fuel low", Severity = AlertSeverity.Critical, SideId = "s1", Notes = "n" },
                new AlertDef { Id = "a2", Name = "Plain", Condition = "Fuel > 0", Message = "m" },
            },
        };
        var zip = Path.Combine(_dir, "f3.qrules");
        RulesPackIo.Save(p, zip);
        var l = RulesPackIo.Load(zip);
        Assert.Equal(4, l.Format);
        Assert.Equal(new[] { "a1", "a2" }, l.Alerts.Select(a => a.Id));
        Assert.Equal(AlertSeverity.Critical, l.Alerts[0].Severity);
        Assert.Equal(AlertSeverity.Warning, l.Alerts[1].Severity);
        Assert.Equal("s1", l.ById<AlertDef>("a1")!.SideId);
        Assert.Equal(OutlinerAggregate.Sum, l.Properties[0].Outliner);
        Assert.Equal("phase note", l.Phases[0].Notes);
        Assert.Equal(J(p with { Tables = p.Tables.OrderBy(t => t.Id).ToList() }), J(l));
    }

    [Fact]
    public void Format2Pack_LoadsWithDefaults()
    {
        var f = FolderWith("{\"format\":2,\"id\":\"x\",\"properties\":[{\"id\":\"p\",\"name\":\"Fuel\"}],\"phases\":[{\"id\":\"h\",\"name\":\"H\"}]}");
        var p = RulesPackIo.Load(f);
        Assert.Empty(p.Alerts);
        Assert.Equal(OutlinerAggregate.None, p.Properties[0].Outliner);
        Assert.Null(p.Phases[0].Notes);
    }

    [Fact]
    public void Alerts_NullFieldsAndDuplicates()
    {
        var f = FolderWith("{\"format\":3,\"id\":\"x\",\"alerts\":[{\"id\":\"a\"}]}");
        var a = RulesPackIo.Load(f).Alerts[0];
        Assert.Equal("", a.Name); Assert.Equal("", a.Condition); Assert.Equal("", a.Message);
        var d = FolderWith("{\"format\":3,\"id\":\"x\",\"alerts\":[{\"id\":\"a\"},{\"id\":\"a\"}]}");
        Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(d));
    }

    [Fact]
    public void Format5Pack_Rejected()
    {
        var f = FolderWith("{\"format\":5,\"id\":\"x\"}");
        Assert.Equal("pack.json: pack format 5 is newer than supported format 4",
            Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
    }

    [Fact]
    public void Format4_RoundTrip_Visibility()
    {
        var p = Sample() with
        {
            Properties = new()
            {
                new PropertyDef { Id = "p1", Name = "Fuel", Visibility = Visibility.Public },
                new PropertyDef { Id = "p2", Name = "Secret" },
            },
            Holders = new() { new HolderDef { Id = "h1", Name = "Depot", SideId = "s1" }, new HolderDef { Id = "h2", Name = "Pool" } },
            MapOwners = new() { new MapOwner { MapId = "m1", SideId = "s1" } },
            Formulas = new() { new FormulaDef { Id = "f1", Name = "Roll", Text = "1", PrivateRolls = true } },
        };
        var zip = Path.Combine(_dir, "f4.qrules");
        RulesPackIo.Save(p, zip);
        var l = RulesPackIo.Load(zip);
        Assert.Equal(4, l.Format);
        Assert.Equal(Visibility.Public, l.Properties[0].Visibility);
        Assert.Equal(Visibility.Private, l.Properties[1].Visibility);
        Assert.Equal("s1", l.Holders[0].SideId);
        Assert.Null(l.Holders[1].SideId);
        Assert.Equal(new MapOwner { MapId = "m1", SideId = "s1" }, Assert.Single(l.MapOwners));
        Assert.True(l.Formulas[0].PrivateRolls);
        Assert.Equal(J(p with { Tables = p.Tables.OrderBy(t => t.Id).ToList() }), J(l));
    }

    [Fact]
    public void Format3Pack_LoadsAllPublic()
    {
        var f = FolderWith("{\"format\":3,\"id\":\"x\",\"properties\":[{\"id\":\"p\",\"name\":\"Fuel\"},{\"id\":\"q\",\"name\":\"Q\",\"visibility\":\"private\"}],\"holders\":[{\"id\":\"h\",\"name\":\"H\"}]}");
        var p = RulesPackIo.Load(f);
        Assert.All(p.Properties, x => Assert.Equal(Visibility.Public, x.Visibility));
        Assert.Null(p.Holders[0].SideId);
        Assert.Empty(p.MapOwners);
    }

    [Fact]
    public void Format4Pack_OmittedVisibility_IsPrivate_AndNullMapOwnersNormalised()
    {
        var f = FolderWith("{\"format\":4,\"id\":\"x\",\"properties\":[{\"id\":\"p\",\"name\":\"Fuel\"}],\"mapOwners\":null}");
        var p = RulesPackIo.Load(f);
        Assert.Equal(Visibility.Private, p.Properties[0].Visibility);
        Assert.Empty(p.MapOwners);
    }

    [Fact]
    public void NullPhaseLists_Normalised()
    {
        var f = FolderWith("{\"format\":2,\"id\":\"x\",\"sides\":null,\"blocks\":[null],\"phases\":[null,{\"id\":\"p\",\"name\":null,\"instructions\":null,\"checklist\":null,\"onStart\":[null],\"onMove\":null,\"warnings\":[null,{\"condition\":null,\"message\":null}]}]}");
        var p = RulesPackIo.Load(f);
        Assert.Empty(p.Sides);
        Assert.Empty(p.Blocks);
        var ph = Assert.Single(p.Phases);
        Assert.Equal("", ph.Name);
        Assert.Equal("", ph.Instructions);
        Assert.Empty(ph.Checklist);
        Assert.Empty(ph.OnStart);
        Assert.Empty(ph.OnMove);
        var w = Assert.Single(ph.Warnings);
        Assert.Equal("", w.Condition);
        Assert.Equal("", w.Message);
    }

    [Fact]
    public void DuplicatePhaseId_LoadAndSaveRejected()
    {
        var f = FolderWith("{\"format\":2,\"id\":\"x\",\"phases\":[{\"id\":\"d\"},{\"id\":\"d\"}]}");
        Assert.Equal("Duplicate phase id 'd'", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(f)).Message);
        var g = FolderWith("{\"format\":2,\"id\":\"x\",\"sides\":[{\"id\":\"d\"},{\"id\":\"d\"}]}");
        Assert.Equal("Duplicate side id 'd'", Assert.Throws<InvalidDataException>(() => RulesPackIo.Load(g)).Message);
        var zip = Path.Combine(_dir, "d.qrules");
        var ex = Assert.Throws<InvalidOperationException>(() => RulesPackIo.Save(Sample() with { Phases = new() { Ph("d"), Ph("d") } }, zip));
        Assert.Equal("Duplicate phase id 'd'", ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => RulesPackIo.Save(Sample() with { Sides = new() { new SideDef { Id = "d" }, new SideDef { Id = "d" } } }, zip));
        Assert.Equal("Duplicate side id 'd'", ex.Message);
        Assert.False(File.Exists(zip));
    }

    [Fact]
    public void Binding_MovedAndAutoValue_RoundTrip()
    {
        var p = Sample();
        var f = p.Formulas[0] with
        {
            Bindings = new() { new VariableBinding { Variable = "m", Kind = BindingKind.Moved, AutoValue = "Dist * 2" } },
        };
        p = p with { Formulas = new() { f } };
        var zip = Path.Combine(_dir, "m.qrules");
        RulesPackIo.Save(p, zip);
        var b = RulesPackIo.Load(zip).Formulas[0].Bindings[0];
        Assert.Equal(BindingKind.Moved, b.Kind);
        Assert.Equal("Dist * 2", b.AutoValue);
        Assert.Equal(7, (int)BindingKind.Moved);
    }

    [Fact]
    public void FromBytes_Received_ThousandEntryPack_Loads_TooManyRefused()
    {
        // Every formula and table is its own entry: a big real pack has hundreds or thousands.
        var p = RulesPackIo.CreateEmpty("Big", "abc123") with
        {
            Formulas = Enumerable.Range(0, 1000).Select(i => new FormulaDef { Id = "f" + i, Name = "Invented " + i, Text = "1 + " + i }).ToList(),
        };
        var bytes = RulesPackIo.ToBytes(p);
        var back = RulesPackIo.FromBytes(bytes, 32L * 1024 * 1024);
        Assert.Equal(1000, back.Formulas.Count);
        Assert.Equal(RulesPackIo.ComputeSha256(p), RulesPackIo.ComputeSha256(back));

        var many = RulesPackIo.CreateEmpty("Many", "abc123") with
        {
            Formulas = Enumerable.Range(0, RulesPackIo.MaxReceivedEntries).Select(i => new FormulaDef { Id = "f" + i, Name = "F", Text = "1" }).ToList(),
        };
        var ex = Assert.Throws<InvalidDataException>(() => RulesPackIo.FromBytes(RulesPackIo.ToBytes(many), 32L * 1024 * 1024));
        Assert.Contains("too many entries", ex.Message);
    }
}
