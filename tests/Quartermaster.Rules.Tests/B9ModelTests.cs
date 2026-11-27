using System.IO.Compression;
using System.Text;

namespace Quartermaster.Rules.Tests;

/// <summary>B9: owner expression, holder choices, draw bindings and holder variables: bytes of old packs, round trip, Normalise.</summary>
public class B9ModelTests
{
    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Format-4 literal written by the pre-B9 code (the B8 merge head): a holder with a side, a fixed Holder binding, a Piece binding with a filter and same hex, a Piece output, a Holder output.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc123",
          "properties": [
            {
              "id": "stores",
              "name": "Stores",
              "scope": "holders",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private",
              "holders": [
                "h-ford"
              ]
            },
            {
              "id": "strength",
              "name": "Strength",
              "scope": "pieces",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "public"
            }
          ],
          "holders": [
            {
              "id": "h-ford",
              "name": "Ford Dump",
              "notes": null,
              "sideId": "north"
            }
          ],
          "ruleRefs": [],
          "sides": [
            {
              "id": "north",
              "name": "Northern League",
              "unitFilter": null
            },
            {
              "id": "south",
              "name": "Southern Compact",
              "unitFilter": null
            }
          ],
          "phases": [],
          "blocks": [],
          "alerts": [],
          "mapOwners": []
        }
        """;

    private const string OldFormulaJson = """
        {
          "id": "f-raid",
          "name": "Raid",
          "text": "Cargo \u002B Foe",
          "bindings": [
            {
              "variable": "Cargo",
              "kind": "holder",
              "property": "stores",
              "holderId": "h-ford",
              "tableId": null,
              "rowExpr": null,
              "colExpr": null,
              "min": null,
              "max": null,
              "choices": null,
              "diceCount": null,
              "diceSides": null,
              "caption": null,
              "autoValue": null
            },
            {
              "variable": "Foe",
              "kind": "piece",
              "property": "strength",
              "holderId": null,
              "tableId": null,
              "rowExpr": null,
              "colExpr": null,
              "min": null,
              "max": null,
              "choices": null,
              "diceCount": null,
              "diceSides": null,
              "caption": null,
              "autoValue": null,
              "pieceFilter": "side() == \u0022Southern Compact\u0022",
              "sameHex": true
            }
          ],
          "outputs": [
            {
              "target": "piece",
              "holderId": null,
              "property": "strength",
              "op": "subtract",
              "expr": "1",
              "pieceVariable": "Foe"
            },
            {
              "target": "holder",
              "holderId": "h-ford",
              "property": "stores",
              "op": "subtract",
              "expr": "Cargo"
            }
          ],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": false
        }
        """;

    private const string OldPackSha256 = "87a7db1a08b26247a1b79afe8ba40c5f928425677aff5c9d34c7c18a371e3894";

    /// <summary>The literals follow the checkout's line endings (CRLF on Windows with autocrlf); the pack is written with LF.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static byte[] OldZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[] { ("pack.json", Lf(OldPackJson)), ("formulas/f-raid.json", Lf(OldFormulaJson)) })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
        return ms.ToArray();
    }

    [Fact]
    public void B9_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f-raid.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
        foreach (var key in new[] { "ownerExpr", "holderChoices", "drawCount", "holderVariable" })
        {
            Assert.DoesNotContain(key, Entry(again, "pack.json"));
            Assert.DoesNotContain(key, Entry(again, "formulas/f-raid.json"));
        }
    }

    private static RulesPack Pack(HolderDef[]? holders = null, params FormulaDef[] formulas) => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Format = RulesPackIo.CurrentFormat,
        Holders = (holders ?? Array.Empty<HolderDef>()).ToList(),
        Formulas = formulas.ToList(),
    };

    private static RulesPack RoundTrip(RulesPack p) => RulesPackIo.FromBytes(RulesPackIo.ToBytes(p));

    private static FormulaDef Formula(VariableBinding b, FormulaOutput? o = null) =>
        new() { Id = "f-1", Name = "One", Text = "1", Bindings = new() { b }, Outputs = o is null ? new() : new() { o } };

    [Fact]
    public void Owner_expr_round_trips_camelCase()
    {
        var p = Pack(new[] { new HolderDef { Id = "h-ford", Name = "Ford Dump", SideId = "north", OwnerExpr = "keyed(DumpOwner, \"Ford Dump\")" }, new HolderDef { Id = "h-mill", Name = "Mill Dump" } });
        var bytes = RulesPackIo.ToBytes(p);
        Assert.Contains("\"ownerExpr\"", Entry(bytes, "pack.json"));
        var back = RulesPackIo.FromBytes(bytes);
        Assert.Equal("keyed(DumpOwner, \"Ford Dump\")", back.Holders[0].OwnerExpr);
        Assert.Null(back.Holders[1].OwnerExpr);
    }

    [Fact]
    public void Draw_binding_round_trips()
    {
        var p = Pack(null, Formula(new VariableBinding { Variable = "Hits", Kind = BindingKind.Draw, Property = "p-boats", SameHex = true, PieceFilter = "side() == \"Southern Compact\"", DrawCount = "Shots" }));
        var bytes = RulesPackIo.ToBytes(p);
        var json = Entry(bytes, "formulas/f-1.json");
        Assert.Contains("\"kind\": \"draw\"", json);
        Assert.Contains("\"drawCount\": \"Shots\"", json);
        Assert.Contains("\"sameHex\": true", json);
        Assert.Contains("\"pieceFilter\"", json);
        Assert.Contains("\"property\": \"p-boats\"", json);
        var b = RulesPackIo.FromBytes(bytes).Formulas[0].Bindings[0];
        Assert.Equal(BindingKind.Draw, b.Kind);
        Assert.Equal("Shots", b.DrawCount);
        Assert.True(b.SameHex);
        Assert.Equal("p-boats", b.Property);
        Assert.Equal("side() == \"Southern Compact\"", b.PieceFilter);
    }

    [Fact]
    public void Holder_choices_and_holder_variable_round_trip()
    {
        var p = Pack(null, Formula(
            new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderChoices = new() { "h-ford", "h-mill" } },
            new FormulaOutput { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores", Op = OutputOp.Subtract, Expr = "Need" }));
        var bytes = RulesPackIo.ToBytes(p);
        var json = Entry(bytes, "formulas/f-1.json");
        Assert.Contains("\"holderChoices\"", json);
        Assert.Contains("\"holderVariable\": \"Dump\"", json);
        var f = RulesPackIo.FromBytes(bytes).Formulas[0];
        Assert.Equal(new[] { "h-ford", "h-mill" }, f.Bindings[0].HolderChoices);
        Assert.Equal("Dump", f.Outputs[0].HolderVariable);
    }

    [Fact]
    public void Blank_new_fields_become_null()
    {
        var p = Pack(new[] { new HolderDef { Id = "h-ford", Name = "Ford Dump", OwnerExpr = "  " } }, Formula(
            new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, HolderChoices = new() { " ", "" }, DrawCount = "  " },
            new FormulaOutput { Target = OutputTarget.Holder, HolderVariable = "  ", Property = "p-stores" }));
        var back = RoundTrip(p);
        Assert.Null(back.Holders[0].OwnerExpr);
        Assert.Null(back.Formulas[0].Bindings[0].HolderChoices);
        Assert.Null(back.Formulas[0].Bindings[0].DrawCount);
        Assert.Null(back.Formulas[0].Outputs[0].HolderVariable);
    }

    [Fact]
    public void Holder_choices_trimmed_and_deduplicated()
    {
        var back = RoundTrip(Pack(null, Formula(new VariableBinding { Variable = "Dump", Kind = BindingKind.Holder, HolderChoices = new() { " h-ford ", "h-ford", "", "h-mill", "H-mill" } })));
        Assert.Equal(new[] { "h-ford", "h-mill", "H-mill" }, back.Formulas[0].Bindings[0].HolderChoices);
    }
}
