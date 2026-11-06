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
}
