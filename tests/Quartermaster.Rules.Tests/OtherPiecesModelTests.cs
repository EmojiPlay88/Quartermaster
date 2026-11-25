using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class OtherPiecesModelTests
{
    private static RulesPack OldPack() => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Format = RulesPackIo.CurrentFormat,
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Barge transfer", Text = "A",
                Bindings = new() { new VariableBinding { Variable = "A", Kind = BindingKind.Input } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
            },
        },
    };

    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Written by the pre-B3 code (commit b11b79e) from an invented pack; a regression in how any existing field is written changes these bytes.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc123",
          "properties": [
            {
              "id": "fuel",
              "name": "Fuel",
              "scope": "both",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private"
            }
          ],
          "holders": [
            {
              "id": "depot",
              "name": "Depot",
              "notes": null,
              "sideId": null
            }
          ],
          "ruleRefs": [],
          "sides": [],
          "phases": [],
          "blocks": [],
          "alerts": [],
          "mapOwners": []
        }
        """;

    private const string OldFormulaJson = """
        {
          "id": "f1",
          "name": "Barge transfer",
          "text": "A \u002B R \u002B H",
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
            },
            {
              "variable": "R",
              "kind": "piece",
              "property": "fuel",
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
              "pieceFilter": "Fuel \u003E 0"
            },
            {
              "variable": "H",
              "kind": "holder",
              "property": "fuel",
              "holderId": "depot",
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
            }
          ],
          "outputs": [
            {
              "target": "unit",
              "holderId": null,
              "property": "fuel",
              "op": "subtract"
            },
            {
              "target": "global",
              "holderId": null,
              "property": "fuel",
              "op": "add",
              "expr": "A"
            },
            {
              "target": "holder",
              "holderId": "depot",
              "property": "fuel",
              "op": "set",
              "expr": "H"
            }
          ],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": false
        }
        """;

    private const string OldPackSha256 = "3c098df952ff15bc1ac6a01060c7b36aa89471e40ebee26f4749b75d95c26da4";

    /// <summary>The literals follow the checkout's line endings (CRLF on Windows with autocrlf); the pack is written with LF.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static byte[] OldZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[] { ("pack.json", Lf(OldPackJson)), ("formulas/f1.json", Lf(OldFormulaJson)) })
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
        return ms.ToArray();
    }

    [Fact]
    public void B3_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f1.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
    }

    [Fact]
    public void New_fields_round_trip_camelCase()
    {
        var f = OldPack().Formulas[0] with
        {
            Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Piece, Property = "fuel", SameHex = true } },
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Piece, Action = OutputAction.SetLayer, PieceVariable = "R", Layer = "Levels" },
                new FormulaOutput { Target = OutputTarget.Piece, Action = OutputAction.Remove, PieceVariable = "R", ToMapId = "dead" },
                new FormulaOutput { Target = OutputTarget.Unit, Action = OutputAction.Place, Slot = "wreck" },
                new FormulaOutput { Target = OutputTarget.Placed, Property = "fuel", Op = OutputOp.Set },
            },
        };
        var json = JsonSerializer.Serialize(f, RulesPackIo.JsonOptions);
        Assert.Contains("\"action\": \"setLayer\"", json);
        Assert.Contains("\"target\": \"placed\"", json);
        Assert.Contains("\"sameHex\": true", json);
        Assert.Contains("\"toMapId\": \"dead\"", json);
        Assert.Contains("\"pieceVariable\": \"R\"", json);
        Assert.Contains("\"slot\": \"wreck\"", json);
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(OldPack() with { Formulas = new() { f } })).Formulas[0];
        Assert.Equal(JsonSerializer.Serialize(f, RulesPackIo.JsonOptions), JsonSerializer.Serialize(back, RulesPackIo.JsonOptions));
        Assert.True(back.Bindings[0].SameHex);
        Assert.Equal(OutputAction.Remove, back.Outputs[1].Action);
        Assert.Equal("dead", back.Outputs[1].ToMapId);
        Assert.Equal(OutputTarget.Placed, back.Outputs[3].Target);
    }

    [Fact]
    public void Write_action_and_false_SameHex_omitted()
    {
        var o = new FormulaOutput { Action = OutputAction.Write, Property = "fuel" };
        Assert.DoesNotContain("action", JsonSerializer.Serialize(o, RulesPackIo.JsonOptions));
        var b = new VariableBinding { Variable = "R", Kind = BindingKind.Piece, SameHex = false };
        Assert.DoesNotContain("sameHex", JsonSerializer.Serialize(b, RulesPackIo.JsonOptions));
    }

    [Fact]
    public void Blank_output_texts_become_null()
    {
        var f = OldPack().Formulas[0] with
        {
            Outputs = new() { new FormulaOutput { Property = "fuel", PieceVariable = "  ", Slot = "", Layer = " ", ToMapId = "\t" } },
        };
        var o = RulesPackIo.FromBytes(RulesPackIo.ToBytes(OldPack() with { Formulas = new() { f } })).Formulas[0].Outputs[0];
        Assert.Null(o.PieceVariable);
        Assert.Null(o.Slot);
        Assert.Null(o.Layer);
        Assert.Null(o.ToMapId);
    }

    private sealed class LayerCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
        public double Layer(string name) => name == "Levels" ? 2 : 0;
    }

    private sealed class PlainCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    [Fact]
    public void Layer_parses_one_argument()
    {
        Assert.Equal(2, Evaluator.Evaluate(Parser.Parse("layer(\"Levels\")"), new LayerCtx()).Number);
        Assert.Equal(3, Evaluator.Evaluate(Parser.Parse("layer(\"Levels\") + 1"), new LayerCtx()).Number);
        Assert.Throws<ParseError>(() => Parser.Parse("layer()"));
        Assert.Throws<ParseError>(() => Parser.Parse("layer(\"a\", \"b\")"));
        Assert.Contains("layer", Evaluator.FunctionNames);
    }

    [Fact]
    public void Layer_needs_quoted_name()
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("layer(Levels)"), new LayerCtx()));
        Assert.Equal("layer() needs a layer name in quotes", e.Message);
    }

    [Fact]
    public void Layer_default_context_not_available()
    {
        var e = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("layer(\"Levels\")"), new PlainCtx()));
        Assert.Equal("layer() is not available here", e.Message);
    }
}
