using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

/// <summary>B7: the Reveal output, Margin, sealed start entries and askside() in the pack and the language.</summary>
public class B7ModelTests
{
    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Written by the pre-B7 code (commit 4370355) from an invented pack with an askSideId start entry, a setMask output, a keyed output,
    // an alert with a side and private rolls; a regression in how any existing field is written changes these bytes.
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
    public void B7_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f-bid.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
    }

    private static RulesPack Pack(FormulaOutput? output = null, StartEntry? entry = null) => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Format = RulesPackIo.CurrentFormat,
        Properties = new() { new PropertyDef { Id = "strength", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public } },
        Sides = new() { new SideDef { Id = "north", Name = "Northern League" }, new SideDef { Id = "south", Name = "Southern Compact" } },
        Phases = new() { new PhaseDef { Id = "ph1", Name = "Toll", OnStart = entry is null ? new() : new() { entry } } },
        Formulas = new()
        {
            new FormulaDef { Id = "f-bid", Name = "Bid", Text = "1", Outputs = output is null ? new() : new() { output } },
        },
    };

    [Fact]
    public void Reveal_and_margin_round_trip_camelCase()
    {
        var o = new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Action = OutputAction.Reveal, Margin = "2" };
        var bytes = RulesPackIo.ToBytes(Pack(o));
        var json = Entry(bytes, "formulas/f-bid.json");
        Assert.Contains("\"action\": \"reveal\"", json);
        Assert.Contains("\"margin\": \"2\"", json);
        var back = RulesPackIo.FromBytes(bytes).Formulas[0].Outputs[0];
        Assert.Equal(OutputAction.Reveal, back.Action);
        Assert.Equal("2", back.Margin);
    }

    [Fact]
    public void Sealed_round_trip_and_false_omitted()
    {
        var sealedBytes = RulesPackIo.ToBytes(Pack(entry: new StartEntry { FormulaId = "f-bid", Sealed = true }));
        Assert.Contains("\"sealed\": true", Entry(sealedBytes, "pack.json"));
        Assert.True(RulesPackIo.FromBytes(sealedBytes).Phases[0].OnStart[0].Sealed);
        var plain = RulesPackIo.ToBytes(Pack(entry: new StartEntry { FormulaId = "f-bid" }));
        Assert.DoesNotContain("sealed", Entry(plain, "pack.json"));
        Assert.False(RulesPackIo.FromBytes(plain).Phases[0].OnStart[0].Sealed);
    }

    [Fact]
    public void Blank_margin_becomes_null()
    {
        var o = new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Action = OutputAction.Reveal, Margin = "  " };
        var loaded = RulesPackIo.FromBytes(RulesPackIo.ToBytes(Pack(o)));
        Assert.Null(loaded.Formulas[0].Outputs[0].Margin);
        Assert.DoesNotContain("margin", Entry(RulesPackIo.ToBytes(loaded), "formulas/f-bid.json"));
    }

    // ---- askside() ----

    [Fact]
    public void Askside_parses_and_arity()
    {
        Assert.IsType<Call>(Parser.Parse("askside()"));
        var e = Assert.Throws<ParseError>(() => Parser.Parse("askside(1)"));
        Assert.Contains("askside", e.Message);
    }

    private sealed class EmptyCtx : IEvalContext
    {
        public Value GetVariable(string name) => throw new EvalError("none");
        public Value Lookup(string tableName, Value row, Value? col) => throw new EvalError("none");
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("none");
        public bool Has(string property) => false;
        public Value PieceName() => Value.Of("");
        public Value Marker(string key) => Value.Of("");
    }

    [Fact]
    public void Askside_default_context_empty()
    {
        var v = Evaluator.Evaluate(Parser.Parse("askside()"), new EmptyCtx());
        Assert.Equal(ValueKind.Text, v.Kind);
        Assert.Equal("", v.Text);
    }

    [Fact]
    public void Askside_in_function_names() => Assert.Contains("askside", Evaluator.FunctionNames);
}
