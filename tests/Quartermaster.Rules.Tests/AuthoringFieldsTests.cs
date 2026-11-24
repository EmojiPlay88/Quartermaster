using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class AuthoringFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qrules-auth-" + Guid.NewGuid().ToString("N"));

    public AuthoringFieldsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // An invented format-4 pack that uses only fields older packs had; checklist items are plain strings.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc",
          "properties": [
            {
              "id": "p1",
              "name": "Fuel",
              "scope": "both",
              "default": 3,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "public"
            }
          ],
          "holders": [
            {
              "id": "h1",
              "name": "Depot",
              "notes": null,
              "sideId": null
            }
          ],
          "ruleRefs": [],
          "sides": [
            {
              "id": "s1",
              "name": "North",
              "unitFilter": null
            }
          ],
          "phases": [
            {
              "id": "ph1",
              "name": "Supply",
              "sideId": "s1",
              "instructions": "Count fuel.",
              "ruleRefId": null,
              "checklist": [
                "Check depots",
                "Pay fuel"
              ],
              "onStart": [],
              "onMove": [],
              "warnings": [
                {
                  "condition": "Fuel \u003C 1",
                  "message": "Out of fuel",
                  "isDecision": false
                }
              ],
              "notes": null
            }
          ],
          "blocks": [],
          "alerts": [
            {
              "id": "a1",
              "name": "Low",
              "condition": "Fuel \u003C 2",
              "message": "Low fuel",
              "severity": "warning",
              "sideId": null,
              "notes": null
            }
          ],
          "mapOwners": []
        }
        """;

    private static RulesPack Pack(Func<RulesPack, RulesPack>? edit = null)
    {
        var p = new RulesPack
        {
            Format = 4,
            Id = "t",
            Name = "TEST",
            Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both, Visibility = Visibility.Public } },
            Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
            RuleRefs = new() { new RuleRef { Id = "r1", Label = "Section 1", DocumentEntry = "rules.pdf", Page = 2 } },
            Sides = new() { new SideDef { Id = "s1", Name = "North" } },
            Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply" } },
        };
        return edit?.Invoke(p) ?? p;
    }

    private static List<RulesIssue> Issues(RulesPack p) => RulesValidator.Validate(p).ToList();

    private static RulesIssue? Find(RulesPack p, string message) => Issues(p).FirstOrDefault(i => i.Message == message);

    private string WriteFolder(string packJson)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pack.json"), packJson, new UTF8Encoding(false));
        return dir;
    }

    private static string PackJsonOf(RulesPack p)
    {
        using var zip = new ZipArchive(new MemoryStream(RulesPackIo.ToBytes(p)), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("pack.json")!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void Text_only_items_and_absent_fields_keep_bytes()
    {
        var pack = RulesPackIo.Load(WriteFolder(OldPackJson.Replace("\r\n", "\n")));
        Assert.Equal("Check depots", pack.Phases[0].Checklist[0].Text);
        Assert.True(pack.Phases[0].Checklist[0].IsTextOnly);
        Assert.Null(pack.Properties[0].Holders);
        Assert.Equal(CloneValue.Copy, pack.Properties[0].CloneAs);

        var written = PackJsonOf(pack);
        Assert.Equal(OldPackJson.Replace("\r\n", "\n") + "\n", written);

        var first = RulesPackIo.ComputeSha256(pack);
        var again = RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack)));
        Assert.Equal(first, again);
        Assert.DoesNotContain("labels", written);
        Assert.DoesNotContain("cloneAs", written);
    }

    [Fact]
    public void Checklist_item_with_fields_round_trips_as_object()
    {
        var pack = Pack(p => p with
        {
            Properties = new() { p.Properties[0] with { RuleRefId = "r1", Labels = "t1", Holders = new() { "h1" }, CloneAs = CloneValue.Default } },
            Holders = new() { p.Holders[0] with { RuleRefId = "r1" } },
            Alerts = new() { new AlertDef { Id = "a1", Name = "Low", Condition = "Fuel < 1", Message = "m", RuleRefId = "r1" } },
            Tables = new() { new TableDef { Id = "t1", Name = "Fog", Kind = TableKind.OneD, RowKeys = new() { "1" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "Mist" } } } },
            Phases = new() { new PhaseDef
            {
                Id = "ph1", Name = "Supply",
                Checklist = new() { "plain", new ChecklistItem { Id = "fuel", Text = "Pay fuel", RuleRefId = "r1", Notes = "n" }, new ChecklistItem { Id = "x", Text = "" } },
                Warnings = new() { new PhaseWarning { Condition = "Fuel < 1", Message = "m", RuleRefId = "r1" } },
            } },
        });
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack));
        var json = PackJsonOf(pack);
        Assert.Contains("\"plain\"", json);
        Assert.Contains("\"ruleRefId\": \"r1\"", json);
        Assert.Contains("\"cloneAs\": \"default\"", json);
        var c = back.Phases[0].Checklist;
        Assert.True(c[0].IsTextOnly);
        Assert.Equal(new ChecklistItem { Id = "fuel", Text = "Pay fuel", RuleRefId = "r1", Notes = "n" }, c[1]);
        Assert.Equal(new ChecklistItem { Id = "x", Text = "" }, c[2]);
        Assert.Equal("r1", back.Phases[0].Warnings[0].RuleRefId);
        Assert.Equal("r1", back.Properties[0].RuleRefId);
        Assert.Equal("t1", back.Properties[0].Labels);
        Assert.Equal(new[] { "h1" }, back.Properties[0].Holders);
        Assert.Equal(CloneValue.Default, back.Properties[0].CloneAs);
        Assert.Equal("r1", back.Holders[0].RuleRefId);
        Assert.Equal("r1", back.Alerts[0].RuleRefId);
        Assert.Equal(RulesPackIo.ComputeSha256(pack), RulesPackIo.ComputeSha256(back));
    }

    [Fact]
    public void Blank_fields_load_as_null()
    {
        const string json = """
            {"format":4,"id":"b","name":"B","moduleSha256":"",
             "properties":[{"id":"p1","name":"Fuel","scope":"both","ruleRefId":" ","labels":"","holders":[" ","", "h1", "h1", " h2 "]},
                           {"id":"p2","name":"Zero","scope":"both","holders":["", " "]}],
             "holders":[{"id":"h1","name":"Depot","ruleRefId":""}],
             "alerts":[{"id":"a1","name":"A","condition":"1","message":"m","ruleRefId":"  "}],
             "phases":[{"id":"ph1","name":"P","checklist":[{"id":" ","text":null,"ruleRefId":"","notes":" "}],
                        "warnings":[{"condition":"1","message":"m","ruleRefId":""}]}]}
            """;
        var pack = RulesPackIo.Load(WriteFolder(json));
        Assert.Null(pack.Properties[0].RuleRefId);
        Assert.Null(pack.Properties[0].Labels);
        Assert.Equal(new[] { "h1", "h2" }, pack.Properties[0].Holders);
        Assert.Null(pack.Properties[1].Holders);
        Assert.Null(pack.Holders[0].RuleRefId);
        Assert.Null(pack.Alerts[0].RuleRefId);
        var item = pack.Phases[0].Checklist[0];
        Assert.Equal("", item.Text);
        Assert.Null(item.Id);
        Assert.Null(item.RuleRefId);
        Assert.Null(item.Notes);
        Assert.True(item.IsTextOnly);
        Assert.Null(pack.Phases[0].Warnings[0].RuleRefId);
    }

    [Fact]
    public void Implicit_string_item()
    {
        ChecklistItem item = "Do it";
        Assert.Equal("Do it", item.Text);
        Assert.True(item.IsTextOnly);
        Assert.Null(item.Id);
        var phase = new PhaseDef { Checklist = new() { "a", "b" } };
        Assert.Equal(new[] { "a", "b" }, phase.Checklist.Select(c => c.Text));
        Assert.False(new ChecklistItem { Text = "a", Notes = "n" }.IsTextOnly);
    }

    [Fact]
    public void Validator_dangling_rule_refs_warn()
    {
        var pack = Pack(p => p with
        {
            Properties = new() { p.Properties[0] with { RuleRefId = "gone" } },
            Holders = new() { p.Holders[0] with { RuleRefId = "gone" } },
            Alerts = new() { new AlertDef { Id = "a1", Name = "Low", Condition = "Fuel < 1", Message = "m", RuleRefId = "gone" } },
            Phases = new() { new PhaseDef
            {
                Id = "ph1", Name = "Supply",
                Checklist = new() { "ok", new ChecklistItem { Text = "x", RuleRefId = "gone" } },
                Warnings = new() { new PhaseWarning { Condition = "Fuel < 1", Message = "m" }, new PhaseWarning { Condition = "Fuel < 1", Message = "m", RuleRefId = "gone" } },
            } },
        });
        foreach (var who in new[] { "Property \"Fuel\"", "Holder \"Depot\"", "Alert \"Low\"", "Phase \"Supply\": warning 2", "Phase \"Supply\": checklist item 2" })
        {
            var issue = Find(pack, $"{who}: rule reference \"gone\" does not exist");
            Assert.NotNull(issue);
            Assert.Equal(RulesIssueSeverity.Warning, issue!.Severity);
        }
        Assert.DoesNotContain(Issues(pack), i => i.Message.Contains("warning 1: rule") || i.Message.Contains("checklist item 1: rule"));

        var fine = pack with
        {
            Properties = new() { pack.Properties[0] with { RuleRefId = "r1" } },
            Holders = new() { pack.Holders[0] with { RuleRefId = "r1" } },
            Alerts = new() { pack.Alerts[0] with { RuleRefId = "r1" } },
            Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Checklist = new() { new ChecklistItem { Text = "x", RuleRefId = "r1" } }, Warnings = new() { new PhaseWarning { Condition = "Fuel < 1", Message = "m", RuleRefId = "r1" } } } },
        };
        Assert.DoesNotContain(Issues(fine), i => i.Message.Contains("rule reference"));
    }

    [Fact]
    public void Validator_checklist_unusable_and_duplicate_ids()
    {
        var pack = Pack(p => p with
        {
            Phases = new()
            {
                new PhaseDef { Id = "ph1", Name = "Supply", Checklist = new() { new ChecklistItem { Id = "a b", Text = "x" }, new ChecklistItem { Id = "dup", Text = "y" }, new ChecklistItem { Id = "ok-1.x_y", Text = "z" } } },
                new PhaseDef { Id = "ph2", Name = "Combat", Checklist = new() { new ChecklistItem { Id = "dup", Text = "w" } } },
            },
        });
        var bad = Find(pack, "Phase \"Supply\": checklist item 1 has an unusable id \"a b\"; use letters, digits, '-', '_' or '.'");
        Assert.NotNull(bad);
        Assert.Equal(RulesIssueSeverity.Error, bad!.Severity);
        var dup = Find(pack, "Phase \"Combat\": checklist item id \"dup\" is used more than once");
        Assert.NotNull(dup);
        Assert.Equal(RulesIssueSeverity.Error, dup!.Severity);
        Assert.Equal(1, Issues(pack).Count(i => i.Message.Contains("is used more than once")));
        Assert.DoesNotContain(Issues(pack), i => i.Message.Contains("\"ok-1.x_y\""));
    }

    [Fact]
    public void Validator_checklist_links_dangling()
    {
        var pack = Pack(p => p with
        {
            Formulas = new() { new FormulaDef { Id = "f1", Name = "Supply roll", Text = "1" } },
            Phases = new() { new PhaseDef
            {
                Id = "ph1", Name = "Supply",
                Checklist = new()
                {
                    new ChecklistItem { Id = "fuel", Text = "Pay fuel" },
                    "See {item:fuel} and {formula:f1}",
                    "See {item:nope} and {formula:nada}",
                },
            } },
        });
        var item = Find(pack, "Phase \"Supply\": checklist item 3 names item \"nope\", which does not exist");
        var formula = Find(pack, "Phase \"Supply\": checklist item 3 names formula \"nada\", which does not exist");
        Assert.Equal(RulesIssueSeverity.Warning, item!.Severity);
        Assert.Equal(RulesIssueSeverity.Warning, formula!.Severity);
        Assert.DoesNotContain(Issues(pack), i => i.Message.Contains("checklist item 2 names"));
    }

    [Fact]
    public void ChecklistText_renders_links_cuts_and_keeps_missing()
    {
        var longText = new string('x', 45);
        var pack = Pack(p => p with
        {
            Formulas = new() { new FormulaDef { Id = "f1", Name = "Supply roll", Text = "1" } },
            Phases = new() { new PhaseDef
            {
                Id = "ph1", Name = "Supply",
                Checklist = new() { new ChecklistItem { Id = "short", Text = "Pay fuel" }, new ChecklistItem { Id = "long", Text = longText }, new ChecklistItem { Id = "exact", Text = new string('y', 40) } },
            } },
        });
        Assert.Equal(40, ChecklistText.CutAt);
        Assert.Equal("Do \"Pay fuel\" then Supply roll", ChecklistText.Render(pack, "Do {item:short} then {formula:f1}"));
        Assert.Equal($"\"{new string('x', 40)}…\"", ChecklistText.Render(pack, "{item:long}"));
        Assert.Equal($"\"{new string('y', 40)}\"", ChecklistText.Render(pack, "{item:exact}"));
        Assert.Equal("{item:nope} {formula:nope} {other:x}", ChecklistText.Render(pack, "{item:nope} {formula:nope} {other:x}"));
        Assert.Equal(new[] { ("item", "short", "{item:short}"), ("formula", "f1", "{formula:f1}") }, ChecklistText.Tokens("{item:short} and {formula:f1} {x:y}"));
        Assert.Equal("Pay fuel", ChecklistText.ItemById(pack, "short")!.Text);
        Assert.Null(ChecklistText.ItemById(pack, "zzz"));
        Assert.True(ChecklistText.IsUsableId("a-b_c.1"));
        Assert.False(ChecklistText.IsUsableId("a b"));
        Assert.False(ChecklistText.IsUsableId(""));
    }

    private static RulesPack LabelPack(Func<TableDef, TableDef>? table = null)
    {
        var t = new TableDef
        {
            Id = "t1", Name = "Fog", Kind = TableKind.OneD, ColumnKeys = new() { "Value" },
            RowKeys = new() { "0", "1-2", "3+", "-9" },
            Cells = new() { new() { "Clear" }, new() { " Mist " }, new() { "Thick" }, new() { "  " } },
        };
        return Pack(p => p with
        {
            Properties = new() { new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both, Labels = "t1" } },
            Tables = new() { table?.Invoke(t) ?? t },
        });
    }

    [Fact]
    public void PropertyLabels_text_range_key_and_no_match()
    {
        var pack = LabelPack();
        var p = pack.Properties[0];
        Assert.Equal("Clear", PropertyLabels.Label(pack, p, 0));
        Assert.Equal("Mist", PropertyLabels.Label(pack, p, 2));
        Assert.Equal("Thick", PropertyLabels.Label(pack, p, 7));
        Assert.Null(PropertyLabels.Label(pack, p, -4));   // no row
        Assert.Null(PropertyLabels.Label(pack, p, -9));   // blank cell
        Assert.Equal("0 (Clear)", PropertyLabels.Text(pack, p, 0));
        Assert.Equal("1.5 (Mist)", PropertyLabels.Text(pack, p, 1.5));
        Assert.Equal("-4", PropertyLabels.Text(pack, p, -4));
        Assert.Equal("-9", PropertyLabels.Text(pack, p, -9));
        Assert.Null(PropertyLabels.Label(pack, p with { Labels = null }, 0));
        Assert.Null(PropertyLabels.Label(pack, p with { Labels = "missing" }, 0));
        Assert.Equal("5", PropertyLabels.Text(pack, p with { Labels = null }, 5));

        var dice = LabelPack(t => t with { Kind = TableKind.Dice });
        Assert.Equal("Clear", PropertyLabels.Label(dice, dice.Properties[0], 0));
        var two = LabelPack(t => t with { Kind = TableKind.TwoD, ColumnKeys = new() { "A", "B" } });
        Assert.Null(PropertyLabels.Label(two, two.Properties[0], 0));
    }

    [Fact]
    public void Validator_labels_table_missing_and_two_d()
    {
        var gone = LabelPack() with { Tables = new() };
        var a = Find(gone, "Property \"Fuel\" takes its labels from a table that no longer exists");
        Assert.NotNull(a);
        Assert.Equal(RulesIssueSeverity.Error, a!.Severity);

        var two = LabelPack(t => t with { Kind = TableKind.TwoD, ColumnKeys = new() { "A", "B" }, Cells = new() { new() { "1", "2" }, new() { "1", "2" }, new() { "1", "2" }, new() { "1", "2" } } });
        var b = Find(two, "Property \"Fuel\" takes its labels from table \"Fog\", which has more than one column");
        Assert.NotNull(b);
        Assert.Equal(RulesIssueSeverity.Error, b!.Severity);

        Assert.DoesNotContain(Issues(LabelPack()), i => i.Message.Contains("labels"));
    }

    [Fact]
    public void Validator_holders_unknown_and_pieces_scope()
    {
        var pack = Pack(p => p with
        {
            Properties = new()
            {
                p.Properties[0] with { Holders = new() { "h1", "nope", HolderDef.GlobalId } },
                new PropertyDef { Id = "p2", Name = "Steps", Scope = PropertyScope.Pieces, Holders = new() { "h1" } },
            },
        });
        var unknown = Find(pack, "Property \"Fuel\" names holder \"nope\", which does not exist");
        Assert.NotNull(unknown);
        Assert.Equal(RulesIssueSeverity.Error, unknown!.Severity);
        Assert.Equal(1, Issues(pack).Count(i => i.Message.Contains("names holder")));
        var pieces = Find(pack, "Property \"Steps\" lists holders but is not a holder property");
        Assert.NotNull(pieces);
        Assert.Equal(RulesIssueSeverity.Error, pieces!.Severity);
    }

    private static RulesPack KeptPack(VariableBinding? binding, FormulaOutput? output, string? expr = null) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Holders, Holders = new() { "h1" } },
            new PropertyDef { Id = "p2", Name = "Ore", Scope = PropertyScope.Holders },
            new PropertyDef { Id = "p3", Name = "Calc", Scope = PropertyScope.Holders, Expr = expr },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" }, new HolderDef { Id = "h2", Name = "Yard" } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "F", Text = "x",
                Bindings = binding is null ? new() : new() { binding },
                Outputs = output is null ? new() : new() { output },
            },
        },
    };

    [Fact]
    public void Validator_holder_binding_output_and_call_not_kept()
    {
        // binding on a named holder
        var named = KeptPack(new VariableBinding { Variable = "x", Kind = BindingKind.Holder, HolderId = "h2", Property = "p1" }, null);
        var m = Find(named, "Formula \"F\": variable \"x\" reads \"Fuel\", which holder \"Yard\" does not keep");
        Assert.NotNull(m);
        Assert.Equal(RulesIssueSeverity.Error, m!.Severity);
        Assert.DoesNotContain(Issues(KeptPack(new VariableBinding { Variable = "x", Kind = BindingKind.Holder, HolderId = "h1", Property = "p1" }, null)), i => i.Message.Contains("does not keep"));
        Assert.DoesNotContain(Issues(KeptPack(new VariableBinding { Variable = "x", Kind = BindingKind.Holder, HolderId = "h2", Property = "p2" }, null)), i => i.Message.Contains("does not keep"));

        // binding on the global holder
        var global = KeptPack(new VariableBinding { Variable = "x", Kind = BindingKind.Global, Property = "p1" }, null);
        Assert.NotNull(Find(global, "Formula \"F\": variable \"x\" reads \"Fuel\", which holder \"Global\" does not keep"));
        var globalHolder = KeptPack(new VariableBinding { Variable = "x", Kind = BindingKind.Holder, HolderId = HolderDef.GlobalId, Property = "p1" }, null);
        Assert.NotNull(Find(globalHolder, "Formula \"F\": variable \"x\" reads \"Fuel\", which holder \"Global\" does not keep"));

        // output on a named holder and on the global one
        var outNamed = KeptPack(null, new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h2", Property = "p1" });
        Assert.NotNull(Find(outNamed, "Formula \"F\": output 1 writes \"Fuel\", which holder \"Yard\" does not keep"));
        var outGlobal = KeptPack(null, new FormulaOutput { Target = OutputTarget.Global, Property = "p1" });
        Assert.NotNull(Find(outGlobal, "Formula \"F\": output 1 writes \"Fuel\", which holder \"Global\" does not keep"));
        var outGlobalHolder = KeptPack(null, new FormulaOutput { Target = OutputTarget.Holder, HolderId = HolderDef.GlobalId, Property = "p1" });
        Assert.NotNull(Find(outGlobalHolder, "Formula \"F\": output 1 writes \"Fuel\", which holder \"Global\" does not keep"));
        var outKept = KeptPack(null, new FormulaOutput { Target = OutputTarget.Holder, HolderId = "h1", Property = "p1" });
        Assert.DoesNotContain(Issues(outKept), i => i.Message.Contains("does not keep"));

        // holder() call in a formula and in a property expression
        var call = KeptPack(null, null) with { Formulas = new() { new FormulaDef { Id = "f1", Name = "F", Text = "holder(Yard, Fuel)" } } };
        Assert.NotNull(Find(call, "Formula \"F\": refers to \"Fuel\", which holder \"Yard\" does not keep"));
        var callKept = KeptPack(null, null) with { Formulas = new() { new FormulaDef { Id = "f1", Name = "F", Text = "holder(Depot, Fuel)" } } };
        Assert.DoesNotContain(Issues(callKept), i => i.Message.Contains("does not keep"));
        var propCall = KeptPack(null, null, "holder(Yard, Fuel)");
        Assert.NotNull(Find(propCall, "Property \"Calc\": refers to \"Fuel\", which holder \"Yard\" does not keep"));
    }

    [Fact]
    public void KeptBy_null_means_all()
    {
        var all = new PropertyDef { Id = "p", Name = "P" };
        Assert.True(PropertyFacts.KeptBy(all, "anything"));
        Assert.True(PropertyFacts.KeptBy(all, HolderDef.GlobalId));
        var some = all with { Holders = new() { "h1" } };
        Assert.True(PropertyFacts.KeptBy(some, "h1"));
        Assert.False(PropertyFacts.KeptBy(some, "H1"));
        Assert.False(PropertyFacts.KeptBy(some, "h2"));
        Assert.False(PropertyFacts.KeptBy(some, HolderDef.GlobalId));
    }
}
