using System.IO.Compression;
using System.Text;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

/// <summary>B2: starting value, reset-at and ask-side fields, runs() in the expression language and their validator checks.</summary>
public class PhasePipelineFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qrules-b2-" + Guid.NewGuid().ToString("N"));
    public PhasePipelineFieldsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static RulesPack Base() => new()
    {
        Format = 4,
        Id = "t",
        Name = "TEST",
        Properties = new()
        {
            new PropertyDef { Id = "p1", Name = "Fuel", Scope = PropertyScope.Both, Visibility = Visibility.Public },
            new PropertyDef { Id = "p2", Name = "Steps", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p3", Name = "Kind", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
        },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Sides = new() { new SideDef { Id = "s1", Name = "North" } },
        Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply" }, new PhaseDef { Id = "ph2", Name = "Move", SideId = "s1" } },
        Formulas = new() { new FormulaDef { Id = "f1", Name = "Fuel Use", Text = "1" } },
    };

    private static string PackJsonOf(RulesPack p)
    {
        using var zip = new ZipArchive(new MemoryStream(RulesPackIo.ToBytes(p)), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry("pack.json")!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static RulesPack RoundTrip(RulesPack p) => RulesPackIo.FromBytes(RulesPackIo.ToBytes(p));

    private static List<string> Msgs(RulesPack p) => RulesValidator.Validate(p).Select(i => i.Message).ToList();

    private static RulesPack WithProp(PropertyDef d) => Base() with { Properties = Base().Properties.Append(d).ToList() };

    // ---- serialisation ----

    // Written by the pre-B2 code (commit 1d1f727) from an invented pack; a regression in how any existing field is written changes these bytes.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "t",
          "name": "TEST",
          "moduleSha256": "",
          "properties": [
            {
              "id": "p1",
              "name": "Fuel",
              "scope": "both",
              "default": 0,
              "sheetField": null,
              "appliesTo": null,
              "notes": null,
              "outliner": "none",
              "visibility": "public"
            },
            {
              "id": "p2",
              "name": "Steps",
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
              "sideId": null,
              "instructions": "",
              "ruleRefId": null,
              "checklist": [],
              "onStart": [
                {
                  "formulaId": "f1",
                  "forEach": "once",
                  "unitFilter": null
                }
              ],
              "onEnd": [
                {
                  "formulaId": "f1",
                  "forEach": "once",
                  "unitFilter": null
                }
              ],
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

    private const string OldFormulaJson = """
        {
          "id": "f1",
          "name": "Fuel Use",
          "text": "1",
          "bindings": [],
          "outputs": [],
          "appliesTo": null,
          "ruleRefId": null,
          "notes": null,
          "privateRolls": false
        }
        """;

    private const string OldPackSha256 = "bd7fefe14d22370e37d60f9e442152a66626b13311a714c3f3b90bd9fdfcc248";

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

    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    [Fact]
    public void B2_absent_fields_keep_bytes()
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
    public void B2_fields_round_trip()
    {
        var pack = Base() with
        {
            Properties = new() { new PropertyDef { Id = "p3", Name = "Kind", Scope = PropertyScope.Pieces, DefaultExpr = "1 + 1", ResetAt = new() { "ph1", "ph2" } } },
            Phases = new()
            {
                new PhaseDef { Id = "ph1", Name = "Supply", OnStart = new() { new StartEntry { FormulaId = "f1", AskSideId = "s1" } }, OnEnd = new() { new StartEntry { FormulaId = "f1", AskSideId = "s1" } } },
                new PhaseDef { Id = "ph2", Name = "Move" },
            },
        };
        var json = PackJsonOf(pack);
        Assert.Contains("\"defaultExpr\": \"1 \\u002B 1\"", json);
        Assert.Contains("resetAt", json);
        Assert.Contains("askSideId", json);
        var back = RoundTrip(pack);
        Assert.Equal("1 + 1", back.Properties[0].DefaultExpr);
        Assert.Equal(new[] { "ph1", "ph2" }, back.Properties[0].ResetAt);
        Assert.Equal("s1", back.Phases[0].OnStart[0].AskSideId);
        Assert.Equal("s1", back.Phases[0].OnEnd![0].AskSideId);
        Assert.Equal(json, PackJsonOf(back));
    }

    [Fact]
    public void Blank_fields_load_as_null()
    {
        var pack = Base() with
        {
            Properties = new() { new PropertyDef { Id = "p3", Name = "Kind", DefaultExpr = "  ", ResetAt = new() { " ", "" } },
                                 new PropertyDef { Id = "p4", Name = "Other", ResetAt = new() { " ph1 ", "ph1", "ph2", "" } } },
            Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", OnStart = new() { new StartEntry { FormulaId = "f1", AskSideId = " " } }, OnEnd = new() { new StartEntry { FormulaId = "f1", AskSideId = "" } } } },
        };
        var back = RoundTrip(pack);
        Assert.Null(back.Properties[0].DefaultExpr);
        Assert.Null(back.Properties[0].ResetAt);
        Assert.Equal(new[] { "ph1", "ph2" }, back.Properties[1].ResetAt);
        Assert.Null(back.Phases[0].OnStart[0].AskSideId);
        Assert.Null(back.Phases[0].OnEnd![0].AskSideId);
    }

    // ---- language ----

    [Fact]
    public void Parser_runs_arity()
    {
        Assert.IsType<Call>(Parser.Parse("runs(\"f1\")"));
        Assert.IsType<Call>(Parser.Parse("runs(\"f1\", \"turn\")"));
        Assert.Throws<ParseError>(() => Parser.Parse("runs()"));
        Assert.Throws<ParseError>(() => Parser.Parse("runs(\"f1\", \"turn\", \"x\")"));
        Assert.Contains("runs", Evaluator.FunctionNames);
    }

    private sealed class RunsCtx : IEvalContext
    {
        public readonly List<(string Id, string Period)> Calls = new();
        public Value GetVariable(string name) => Value.Of(1);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(1);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => true;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
        public double Runs(string formulaId, string period) { Calls.Add((formulaId, period)); return 5; }
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
    public void Evaluator_runs_args_and_default_period()
    {
        var ctx = new RunsCtx();
        Assert.Equal(5, Evaluator.Evaluate(Parser.Parse("runs(\"f1\")"), ctx).Number);
        Evaluator.Evaluate(Parser.Parse("runs(\"f2\", \"Turn\")"), ctx);
        Evaluator.Evaluate(Parser.Parse("runs(\"f3\", \"STAGE\")"), ctx);
        Evaluator.Evaluate(Parser.Parse("runs(\"f4\", \"game\") + 1"), ctx);
        Assert.Equal(new[] { ("f1", "game"), ("f2", "turn"), ("f3", "stage"), ("f4", "game") }, ctx.Calls);
    }

    [Theory]
    [InlineData("runs(Fuel)", "runs() needs a formula id in quotes")]
    [InlineData("runs(1)", "runs() needs a formula id in quotes")]
    [InlineData("runs(\"f1\", \"week\")", "runs() period must be \"stage\", \"turn\" or \"game\"")]
    [InlineData("runs(\"f1\", Fuel)", "runs() period must be \"stage\", \"turn\" or \"game\"")]
    public void Evaluator_runs_bad_args_texts(string text, string message)
    {
        var ex = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse(text), new RunsCtx()));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Default_context_runs_not_available()
    {
        var ex = Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("runs(\"f1\")"), new PlainCtx()));
        Assert.Equal("runs() is not available here", ex.Message);
    }

    // ---- validator ----

    [Fact]
    public void Validator_starting_value_errors()
    {
        Assert.Empty(Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, DefaultExpr = "Steps + 1" })));

        Assert.Contains("Property \"Roll\" has a starting value expression and is computed; remove one",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, Expr = "Steps", DefaultExpr = "1" })));
        var parse = Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, DefaultExpr = "1 + * 2" }));
        Assert.Single(parse);
        Assert.StartsWith("Property \"Roll\": the starting value expression does not parse: ", parse[0]);
        Assert.Contains("Property \"Roll\": starting value refers to unknown property \"Nope\"",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, DefaultExpr = "Nope" })));
        Assert.Contains("Property \"Roll\": starting value refers to unknown table \"Nope\"",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, DefaultExpr = "lookup(Nope, 1)" })));
        Assert.Contains("Property \"Roll\" has a starting value expression but is not a piece property",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Holders, DefaultExpr = "1" })));

        var calls = new Dictionary<string, string>
        {
            ["dice"] = "dice(1, 6)", ["holder"] = "holder(Depot, Fuel)", ["runs"] = "runs(\"f1\")",
            ["sum"] = "sum(Steps, \"\")", ["countof"] = "countof(\"\")", ["maxof"] = "maxof(Steps, \"\")", ["minof"] = "minof(Steps, \"\")",
            ["hexsum"] = "hexsum(Steps)", ["hexcount"] = "hexcount()", ["hexmax"] = "hexmax(Steps)", ["hexmin"] = "hexmin(Steps)",
            ["attachedsum"] = "attachedsum(Steps)", ["attachedcount"] = "attachedcount()", ["attachedmax"] = "attachedmax(Steps)", ["attachedmin"] = "attachedmin(Steps)",
        };
        foreach (var (fn, text) in calls)
            Assert.Contains($"Property \"Roll\": {fn}() is not available in a starting value",
                Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, DefaultExpr = text })));
    }

    [Fact]
    public void Validator_reset_errors()
    {
        Assert.Empty(Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, ResetAt = new() { "ph1" } })));
        Assert.Contains("Property \"Roll\" resets at phase \"zz\", which does not exist",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, ResetAt = new() { "ph1", "zz" } })));
        Assert.Contains("Property \"Roll\" is computed, so it has nothing to reset",
            Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, Expr = "Steps", ResetAt = new() { "ph1" } })));
        Assert.All(RulesValidator.Validate(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, ResetAt = new() { "zz" } })),
            i => Assert.Equal(RulesIssueSeverity.Error, i.Severity));
    }

    private static RulesPack AskPack(PhaseDef ph) => Base() with { Phases = new() { ph } };

    [Fact]
    public void Validator_ask_entry_errors()
    {
        var ok = new StartEntry { FormulaId = "f1", AskSideId = "s1" };
        Assert.Empty(Msgs(AskPack(new PhaseDef { Id = "a", Name = "Ask", OnStart = new() { ok }, OnEnd = new() { ok } })));

        foreach (var kind in new[] { "start", "end" })
        {
            List<StartEntry> Entries(StartEntry bad) => new() { new StartEntry { FormulaId = "f1" }, bad };
            PhaseDef Ph(StartEntry bad, string? side = null) => kind == "start"
                ? new PhaseDef { Id = "a", Name = "Ask", SideId = side, OnStart = Entries(bad) }
                : new PhaseDef { Id = "a", Name = "Ask", SideId = side, OnEnd = Entries(bad) };

            Assert.Equal(new[] { $"Phase \"Ask\": {kind} entry 2 asks side \"zz\", which does not exist" },
                Msgs(AskPack(Ph(ok with { AskSideId = "zz" }))));
            Assert.Equal(new[] { $"Phase \"Ask\": {kind} entry 2 asks a side, so it must run once, not for each unit" },
                Msgs(AskPack(Ph(ok with { ForEach = ForEachMode.Units }))));
            Assert.Equal(new[] { $"Phase \"Ask\": {kind} entry 2 asks a side, but only a phase without a side may ask one" },
                Msgs(AskPack(Ph(ok, "s1"))));
        }
    }

    [Fact]
    public void Validator_runs_tails_in_formula_warning_alert_filter()
    {
        var tails = new (string Expr, string Tail)[]
        {
            ("runs(Fuel)", "runs() needs a formula id in quotes"),
            ("runs(\"zz\")", "runs() names formula \"zz\", which does not exist"),
            ("runs(\"f1\", \"week\")", "runs() period must be \"stage\", \"turn\" or \"game\""),
        };
        foreach (var (expr, tail) in tails)
        {
            var formula = Base() with { Formulas = new() { new FormulaDef { Id = "f1", Name = "Fuel Use", Text = expr } } };
            Assert.Contains($"{tail}", string.Join("|", Msgs(formula)));
            Assert.Contains(Msgs(formula), m => m.EndsWith(tail, StringComparison.Ordinal) && m.StartsWith("Formula \"Fuel Use\": ", StringComparison.Ordinal));

            var warn = Base() with { Phases = new() { new PhaseDef { Id = "a", Name = "Ask", Warnings = new() { new PhaseWarning { Condition = expr, Message = "m" } } } } };
            Assert.Contains(Msgs(warn), m => m.StartsWith("Phase \"Ask\": warning condition ", StringComparison.Ordinal) && m.EndsWith(tail, StringComparison.Ordinal));

            var alert = Base() with { Alerts = new() { new AlertDef { Id = "al", Name = "Low", Condition = expr, Message = "m" } } };
            Assert.Contains(Msgs(alert), m => m.EndsWith(tail, StringComparison.Ordinal));

            var filter = Base() with { Phases = new() { new PhaseDef { Id = "a", Name = "Ask", OnStart = new() { new StartEntry { FormulaId = "f1", UnitFilter = expr } } } } };
            Assert.Contains(Msgs(filter), m => m.StartsWith("Phase \"Ask\": on-start filter ", StringComparison.Ordinal) && m.EndsWith(tail, StringComparison.Ordinal));
        }

        var good = Base() with { Formulas = new() { new FormulaDef { Id = "f1", Name = "Fuel Use", Text = "runs(\"f1\", \"Turn\") + runs(\"f1\")" } } };
        Assert.DoesNotContain(Msgs(good), m => m.Contains("runs()"));
    }

    [Fact]
    public void Validator_runs_forbidden_places()
    {
        var applies = Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, AppliesTo = "runs(\"f1\") > 0" }));
        Assert.Contains("Property \"Roll\": runs() is not available in a property's applies-to", applies);

        var computed = Msgs(WithProp(new PropertyDef { Id = "p9", Name = "Roll", Scope = PropertyScope.Pieces, Expr = "runs(\"f1\")" }));
        Assert.Contains("Property \"Roll\": runs() is not available in a computed property", computed);

        var side = Msgs(Base() with { Sides = new() { new SideDef { Id = "s1", Name = "North", UnitFilter = "runs(\"f1\") > 0" } } });
        Assert.Contains("Side \"North\": runs() is not available in a side's unit filter", side);

        var cond = Msgs(Base() with { Phases = new() { new PhaseDef { Id = "a", Name = "Ask", Condition = "runs(\"f1\") > 0" } } });
        Assert.Contains("Phase \"Ask\": condition may not use runs()", cond);
    }
}
