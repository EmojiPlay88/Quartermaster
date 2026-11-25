using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

/// <summary>B4: group roll, choices table and filter in the pack, and the row() function.</summary>
public class GroupRunsModelTests
{
    private static RulesPack OldPack() => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Format = RulesPackIo.CurrentFormat,
        Properties = new()
        {
            new PropertyDef { Id = "cargo", Name = "Cargo", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f1", Name = "Barge transfer", Text = "A + D",
                Bindings = new()
                {
                    new VariableBinding { Variable = "A", Kind = BindingKind.Input, Min = 0, Max = 9, Choices = new() { "Grain", "Salt" }, Caption = "Cargo kind" },
                    new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
                },
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

    // Written by the pre-B4 code (commit 5e29879) from an invented pack; a regression in how any existing field is written changes these bytes.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "riverland",
          "name": "Riverland",
          "moduleSha256": "abc123",
          "properties": [
            {
              "id": "cargo",
              "name": "Cargo",
              "scope": "pieces",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private"
            },
            {
              "id": "fuel",
              "name": "Fuel",
              "scope": "pieces",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "private"
            }
          ],
          "holders": [],
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
          "text": "A \u002B D",
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
              "choices": [
                "Grain",
                "Salt"
              ],
              "diceCount": null,
              "diceSides": null,
              "caption": "Cargo kind",
              "autoValue": null
            },
            {
              "variable": "D",
              "kind": "roll",
              "property": null,
              "holderId": null,
              "tableId": null,
              "rowExpr": null,
              "colExpr": null,
              "min": null,
              "max": null,
              "choices": null,
              "diceCount": 2,
              "diceSides": 6,
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
            }
          ],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": false
        }
        """;

    private const string OldPackSha256 = "7aa34cd7ced95b50638c7657f5dc8f507b12fca4acab43eae4657520207447be";

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
    public void B4_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(OldZip());
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f1.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
    }

    [Fact]
    public void New_fields_round_trip_camelCase()
    {
        var f = OldPack().Formulas[0] with
        {
            GroupRoll = true,
            Bindings = new()
            {
                new VariableBinding { Variable = "A", Kind = BindingKind.Input, ChoicesTable = "t1", ChoicesFilter = "lookup(Cargo, row(), \"Min size\") <= Fuel" },
                new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
            },
        };
        var json = JsonSerializer.Serialize(f, RulesPackIo.JsonOptions);
        Assert.Contains("\"groupRoll\": true", json);
        Assert.Contains("\"choicesTable\": \"t1\"", json);
        Assert.Contains("\"choicesFilter\":", json);
        var back = RulesPackIo.FromBytes(RulesPackIo.ToBytes(OldPack() with { Formulas = new() { f } })).Formulas[0];
        Assert.Equal(JsonSerializer.Serialize(f, RulesPackIo.JsonOptions), JsonSerializer.Serialize(back, RulesPackIo.JsonOptions));
        Assert.True(back.GroupRoll);
        Assert.Equal("t1", back.Bindings[0].ChoicesTable);
        Assert.Equal("lookup(Cargo, row(), \"Min size\") <= Fuel", back.Bindings[0].ChoicesFilter);
    }

    [Fact]
    public void False_GroupRoll_omitted()
    {
        Assert.DoesNotContain("groupRoll", JsonSerializer.Serialize(new FormulaDef { Id = "f", GroupRoll = false }, RulesPackIo.JsonOptions));
        var b = new VariableBinding { Variable = "A", Kind = BindingKind.Input };
        var json = JsonSerializer.Serialize(b, RulesPackIo.JsonOptions);
        Assert.DoesNotContain("choicesTable", json);
        Assert.DoesNotContain("choicesFilter", json);
    }

    [Fact]
    public void Blank_choices_texts_become_null()
    {
        var f = OldPack().Formulas[0] with
        {
            Bindings = new() { new VariableBinding { Variable = "A", Kind = BindingKind.Input, ChoicesTable = "  ", ChoicesFilter = "" } },
        };
        var b = RulesPackIo.FromBytes(RulesPackIo.ToBytes(OldPack() with { Formulas = new() { f } })).Formulas[0].Bindings[0];
        Assert.Null(b.ChoicesTable);
        Assert.Null(b.ChoicesFilter);
    }

    // ---- language ----

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
    public void Row_parses_no_arguments()
    {
        Assert.IsType<Call>(Parser.Parse("row()"));
        Assert.IsType<Binary>(Parser.Parse("lookup(Cargo, row(), \"Min size\") <= 3"));
        Assert.Throws<ParseError>(() => Parser.Parse("row(1)"));
        Assert.Contains("row", Evaluator.FunctionNames);
    }

    [Fact]
    public void Row_default_context_throws()
    {
        var ex = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("row()"), new PlainCtx()));
        Assert.Equal("row() can only be used in a choices filter", ex.Message);
    }
}
