using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quartermaster.Vassal;

namespace Quartermaster.Rules.Tests;

/// <summary>B6: the keyed-by property field, the output key field, KeyedFacts and MapRefs.</summary>
public class PlacesModelTests
{
    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Written by the pre-B6 code (commit 05076f3) from an invented pack; a regression in how any existing field is written changes these bytes.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc123",
          "properties": [
            {
              "id": "p-fuel",
              "name": "Fuel",
              "scope": "pieces",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private",
              "resetAt": [
                "ph1"
              ]
            },
            {
              "id": "p-level",
              "name": "Water level",
              "scope": "holders",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "public",
              "labels": "t-levels",
              "holders": [
                "global",
                "h1"
              ]
            }
          ],
          "holders": [
            {
              "id": "h1",
              "name": "Boat yard",
              "notes": null,
              "sideId": null
            }
          ],
          "ruleRefs": [],
          "sides": [],
          "phases": [
            {
              "id": "ph1",
              "name": "Dawn",
              "sideId": null,
              "instructions": "",
              "ruleRefId": null,
              "checklist": [],
              "onStart": [],
              "onMove": [],
              "warnings": [],
              "notes": null
            }
          ],
          "blocks": [],
          "alerts": [],
          "mapOwners": []
        }
        """;

    private const string OldTableJson = """
        {
          "id": "t-levels",
          "name": "Water levels",
          "kind": "oneD",
          "rowKeys": [
            "1",
            "2"
          ],
          "columnKeys": [],
          "cells": [
            [
              "Low"
            ],
            [
              "High"
            ]
          ],
          "ruleRefId": null,
          "notes": null
        }
        """;

    private const string OldFormulaJson = """
        {
          "id": "f1",
          "name": "Sink the barge",
          "text": "1",
          "bindings": [],
          "outputs": [
            {
              "target": "unit",
              "holderId": null,
              "property": "p-fuel",
              "op": "subtract"
            },
            {
              "target": "unit",
              "holderId": null,
              "property": "",
              "op": "subtract",
              "action": "remove",
              "toMapId": "m-river"
            }
          ],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": false
        }
        """;

    private const string OldPackSha256 = "4aaa58a82acfce174cdce955283e8ffcad8a5aab59480f1eb57e8e532ad3d1ee";

    /// <summary>The literals follow the checkout's line endings (CRLF on Windows with autocrlf); the pack is written with LF.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static byte[] OldZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[] { ("pack.json", Lf(OldPackJson)), ("tables/t-levels.json", Lf(OldTableJson)), ("formulas/f1.json", Lf(OldFormulaJson)) })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
        return ms.ToArray();
    }

    [Fact]
    public void B6_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldTableJson) + "\n", Entry(again, "tables/t-levels.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f1.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
    }

    private static RulesPack Base() => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Format = RulesPackIo.CurrentFormat,
        Properties = new()
        {
            new PropertyDef { Id = "p-ford", Name = "Ford depth", Scope = PropertyScope.Holders, KeyedBy = "hex", Holders = new() { "global" } },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Flood the ford", Text = "3",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-ford", Op = OutputOp.Set, Expr = "3", Key = "hex()" } },
            },
        },
    };

    [Fact]
    public void New_fields_round_trip_camelCase()
    {
        var pack = Base();
        var json = JsonSerializer.Serialize(pack.Properties[0], RulesPackIo.JsonOptions);
        Assert.Contains("\"keyedBy\": \"hex\"", json);
        var fjson = JsonSerializer.Serialize(pack.Formulas[0].Outputs[0], RulesPackIo.JsonOptions);
        Assert.Contains("\"key\": \"hex()\"", fjson);
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack));
        Assert.Equal("hex", back.Properties[0].KeyedBy);
        Assert.Equal("hex()", back.Formulas[0].Outputs[0].Key);
        Assert.Equal(RulesPackIo.ComputeSha256(pack), RulesPackIo.ComputeSha256(back));
    }

    [Fact]
    public void Null_fields_are_omitted()
    {
        Assert.DoesNotContain("keyedBy", JsonSerializer.Serialize(new PropertyDef { Id = "p" }, RulesPackIo.JsonOptions));
        Assert.DoesNotContain("\"key\"", JsonSerializer.Serialize(new FormulaOutput(), RulesPackIo.JsonOptions));
    }

    [Fact]
    public void Blank_keyedBy_and_key_become_null()
    {
        var pack = Base();
        pack = pack with
        {
            Properties = new() { pack.Properties[0] with { KeyedBy = "   " } },
            Formulas = new() { pack.Formulas[0] with { Outputs = new() { pack.Formulas[0].Outputs[0] with { Key = " " } } } },
        };
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack));
        Assert.Null(back.Properties[0].KeyedBy);
        Assert.Null(back.Formulas[0].Outputs[0].Key);
    }

    [Fact]
    public void KeyedBy_trimmed_case_kept()
    {
        var pack = Base();
        pack = pack with { Properties = new() { pack.Properties[0] with { KeyedBy = "  Table:T-Hulls " } } };
        Assert.Equal("Table:T-Hulls", RulesPackIo.FromBytes(RulesPackIo.ToBytes(pack)).Properties[0].KeyedBy);
    }

    // ---- KeyedFacts ----

    [Theory]
    [InlineData("table:t1", KeyKind.Table, "t1", false)]
    [InlineData(" TABLE: t1 ", KeyKind.Table, "t1", false)]
    [InlineData(" TURN ", KeyKind.Turn, null, false)]
    [InlineData("Hex", KeyKind.Hex, null, false)]
    [InlineData("table:", KeyKind.None, null, true)]
    [InlineData("side", KeyKind.None, null, true)]
    [InlineData("", KeyKind.None, null, false)]
    [InlineData(null, KeyKind.None, null, false)]
    public void KeyedFacts_parse_forms(string? text, KeyKind kind, string? tableId, bool malformed)
    {
        Assert.Equal((kind, tableId), KeyedFacts.Parse(text));
        Assert.Equal(malformed, KeyedFacts.IsMalformed(text));
    }

    [Fact]
    public void KeyedFacts_is_keyed_and_place()
    {
        Assert.False(KeyedFacts.IsKeyed(null));
        Assert.False(KeyedFacts.IsKeyed(new PropertyDef()));
        Assert.False(KeyedFacts.IsKeyed(new PropertyDef { KeyedBy = "side" }));
        Assert.True(KeyedFacts.IsKeyed(new PropertyDef { KeyedBy = "turn" }));
        Assert.True(KeyedFacts.IsKeyed(new PropertyDef { KeyedBy = "table:t" }));
        Assert.True(KeyedFacts.IsPlace(new PropertyDef { KeyedBy = " HEX" }));
        Assert.False(KeyedFacts.IsPlace(new PropertyDef { KeyedBy = "turn" }));
        Assert.False(KeyedFacts.IsPlace(null));
    }

    [Fact]
    public void KeyedFacts_display_round_trip()
    {
        var pack = new RulesPack { Tables = new() { new TableDef { Id = "t-hulls", Name = "Hull types" } } };
        Assert.Equal("", KeyedFacts.Display(pack, null));
        Assert.Equal("", KeyedFacts.Display(pack, "  "));
        Assert.Equal("Turn", KeyedFacts.Display(pack, " TURN "));
        Assert.Equal("Hex (place)", KeyedFacts.Display(pack, "hex"));
        Assert.Equal("Table: Hull types", KeyedFacts.Display(pack, "table:t-hulls"));
        foreach (var kb in new[] { "turn", "hex", "table:t-hulls" })
            Assert.Equal(kb, KeyedFacts.FromDisplay(pack, KeyedFacts.Display(pack, kb)));
        Assert.Null(KeyedFacts.FromDisplay(pack, ""));
        Assert.Null(KeyedFacts.FromDisplay(pack, "   "));
        Assert.Equal("turn", KeyedFacts.FromDisplay(pack, "turn"));
    }

    // ---- MapRefs ----

    private static MapDef Map(string id, string name) => new(id, name, false, Array.Empty<string>(), Array.Empty<BoardDef>(), 0, 0,
        new RgbColor(255, 255, 255), new StackMetricsDef(0, 0, 0, 0, true), Array.Empty<SetupStackDef>(), "Always");

    [Fact]
    public void MapRefs_id_then_name_trimmed()
    {
        var maps = new[] { Map("m1", "North"), Map("m2", "m1"), Map("North", "South") };
        Assert.Same(maps[0], MapRefs.Find(maps, "m1"));          // an id beats a name
        Assert.Same(maps[0], MapRefs.Find(maps, "  m1  "));      // trimmed
        Assert.Same(maps[2], MapRefs.Find(maps, "North"));       // an id beats a name here too
        Assert.Same(maps[2], MapRefs.Find(maps, "South"));       // by name
        Assert.Null(MapRefs.Find(maps, "south"));                // ordinal
        Assert.Null(MapRefs.Find(maps, "Nowhere"));
    }

    [Fact]
    public void MapRefs_blank_null()
    {
        var maps = new[] { Map("m1", "North") };
        Assert.Null(MapRefs.Find(maps, null));
        Assert.Null(MapRefs.Find(maps, ""));
        Assert.Null(MapRefs.Find(maps, "   "));
        Assert.Null(MapRefs.Find(Array.Empty<MapDef>(), "m1"));
    }
}
