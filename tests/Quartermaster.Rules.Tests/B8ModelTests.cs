using System.IO.Compression;
using System.Text;

namespace Quartermaster.Rules.Tests;

/// <summary>B8: former ids and alliance on a side: bytes of old packs, round trip, Normalise and reference mapping.</summary>
public class B8ModelTests
{
    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Format-4 literal written by the pre-B8 code (the B7 merge head): sides without formerIds or allianceId, an askSideId entry, an alert with a side.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc123",
          "properties": [
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
            },
            {
              "id": "tollbid",
              "name": "TollBid",
              "scope": "holders",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private",
              "keyedBy": "turn"
            }
          ],
          "holders": [],
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
          "phases": [
            {
              "id": "ph1",
              "name": "Toll",
              "sideId": null,
              "instructions": "",
              "ruleRefId": null,
              "checklist": [],
              "onStart": [
                {
                  "formulaId": "f-bid",
                  "forEach": "once",
                  "unitFilter": null,
                  "askSideId": "north"
                }
              ],
              "onMove": [],
              "warnings": [],
              "notes": null
            }
          ],
          "blocks": [],
          "alerts": [
            {
              "id": "a1",
              "name": "Low",
              "condition": "1 \u003E 0",
              "message": "Low",
              "severity": "warning",
              "sideId": "south",
              "notes": null
            }
          ],
          "mapOwners": []
        }
        """;

    private const string OldFormulaJson = """
        {
          "id": "f-bid",
          "name": "Bid",
          "text": "A",
          "bindings": [
            {
              "variable": "A",
              "kind": "input",
              "property": null,
              "holderId": null,
              "tableId": null,
              "rowExpr": null,
              "colExpr": null,
              "min": 0,
              "max": 9,
              "choices": null,
              "diceCount": null,
              "diceSides": null,
              "caption": null,
              "autoValue": null
            }
          ],
          "outputs": [
            {
              "target": "global",
              "holderId": null,
              "property": "tollbid",
              "op": "set",
              "key": "turn()"
            },
            {
              "target": "unit",
              "holderId": null,
              "property": "strength",
              "op": "subtract",
              "action": "setMask"
            }
          ],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": true
        }
        """;

    private const string OldPackSha256 = "180b642af44bc460de028242002ef20cd7d117c34236db225bdd683a0cd545ae";

    /// <summary>The literals follow the checkout's line endings (CRLF on Windows with autocrlf); the pack is written with LF.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static byte[] OldZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[] { ("pack.json", Lf(OldPackJson)), ("formulas/f-bid.json", Lf(OldFormulaJson)) })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
        return ms.ToArray();
    }

    [Fact]
    public void B8_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f-bid.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
        Assert.DoesNotContain("formerIds", Entry(again, "pack.json"));
        Assert.DoesNotContain("allianceId", Entry(again, "pack.json"));
    }

    private static RulesPack Pack(params SideDef[] sides) => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Format = RulesPackIo.CurrentFormat,
        Sides = sides.ToList(),
    };

    private static RulesPack RoundTrip(RulesPack p) => RulesPackIo.FromBytes(RulesPackIo.ToBytes(p));

    [Fact]
    public void FormerIds_and_alliance_round_trip_camelCase()
    {
        var p = Pack(new SideDef { Id = "north", Name = "Northern League", FormerIds = new() { "league" }, AllianceId = "Upriver Pact" }, new SideDef { Id = "south", Name = "Southern Compact" });
        var bytes = RulesPackIo.ToBytes(p);
        var json = Entry(bytes, "pack.json");
        Assert.Contains("\"formerIds\"", json);
        Assert.Contains("\"allianceId\": \"Upriver Pact\"", json);
        var back = RulesPackIo.FromBytes(bytes);
        Assert.Equal(new[] { "league" }, back.Sides[0].FormerIds);
        Assert.Equal("Upriver Pact", back.Sides[0].AllianceId);
        Assert.Null(back.Sides[1].FormerIds);
        Assert.Null(back.Sides[1].AllianceId);
    }

    [Fact]
    public void Blank_alliance_and_empty_former_ids_become_null()
    {
        var back = RoundTrip(Pack(
            new SideDef { Id = "north", Name = "Northern League", FormerIds = new() { " ", "" }, AllianceId = "  " },
            new SideDef { Id = "south", Name = "Southern Compact", FormerIds = new() }));
        Assert.All(back.Sides, s => { Assert.Null(s.FormerIds); Assert.Null(s.AllianceId); });
    }

    [Fact]
    public void Former_ids_trimmed_and_deduplicated()
    {
        var back = RoundTrip(Pack(new SideDef { Id = "north", Name = "Northern League", FormerIds = new() { " league ", "league", "", "Union", "union" }, AllianceId = " Upriver Pact " }));
        Assert.Equal(new[] { "league", "Union", "union" }, back.Sides[0].FormerIds);
        Assert.Equal("Upriver Pact", back.Sides[0].AllianceId);
    }

    private static RulesPack Mapped() => Pack(
        new SideDef { Id = "east", Name = "East Shore", FormerIds = new() { "league" } },
        new SideDef { Id = "south", Name = "Southern Compact" }) with
    {
        Phases = new()
        {
            new PhaseDef
            {
                Id = "ph1", Name = "Toll", SideId = "league", Reactors = new() { "league", "south", "league", "east" },
                OnStart = new() { new StartEntry { FormulaId = "f", AskSideId = "league" } },
                OnEnd = new() { new StartEntry { FormulaId = "f", AskSideId = "league" } },
            },
            new PhaseDef { Id = "ph2", Name = "March", SideId = "south", Reactors = new() { "south" } },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot", SideId = "league" } },
        MapOwners = new() { new MapOwner { MapId = "m1", SideId = "league" } },
        Alerts = new() { new AlertDef { Id = "a1", Name = "Low", Condition = "1 > 0", Message = "Low", SideId = "league" } },
        Victory = new() { new VictoryCondition { Id = "v1", Condition = "1 > 0", SideId = "league" } },
    };

    [Fact]
    public void Normalise_maps_pack_references()
    {
        var p = RoundTrip(Mapped());
        var ph = p.Phases[0];
        Assert.Equal("east", ph.SideId);
        Assert.Equal(new[] { "south" }, ph.Reactors);          // league -> east is the phase's own side: dropped; duplicates merged
        Assert.Equal("east", ph.OnStart[0].AskSideId);
        Assert.Equal("east", ph.OnEnd![0].AskSideId);
        Assert.Equal(new[] { "south" }, p.Phases[1].Reactors);  // a phase naming no former id is left as written
        Assert.Equal("east", p.Holders[0].SideId);
        Assert.Equal("east", p.MapOwners[0].SideId);
        Assert.Equal("east", p.Alerts[0].SideId);
        Assert.Equal("east", p.Victory![0].SideId);
    }

    [Fact]
    public void No_former_ids_same_pack_instance_from_MapReferences()
    {
        var p = Mapped() with { Sides = new() { new SideDef { Id = "east", Name = "East Shore" }, new SideDef { Id = "south", Name = "Southern Compact" } } };
        Assert.Same(p, SideFacts.MapReferences(p));
        var q = Mapped() with { Phases = new(), Holders = new(), MapOwners = new(), Alerts = new(), Victory = null };
        Assert.Same(q, SideFacts.MapReferences(q));
    }
}
