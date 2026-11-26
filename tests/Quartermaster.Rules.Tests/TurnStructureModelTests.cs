using System.IO.Compression;
using System.Text;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

/// <summary>B5: reactors, schedule, last turn/stage, victory conditions, open-ended blocks and segment().</summary>
public class TurnStructureModelTests
{
    // Written by the pre-B5 code (master 7993778) from an invented pack; a regression in how any existing field is written changes these bytes.
    private const string OldPackJson = """
        {
          "format": 4,
          "id": "t",
          "name": "TEST",
          "moduleSha256": "",
          "properties": [],
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
            },
            {
              "id": "s2",
              "name": "South",
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
            },
            {
              "id": "ph2",
              "name": "Move",
              "sideId": "s1",
              "instructions": "",
              "ruleRefId": null,
              "checklist": [],
              "onStart": [],
              "onMove": [],
              "warnings": [],
              "notes": null,
              "condition": "turn() \u003E 1"
            },
            {
              "id": "ph3",
              "name": "Fire",
              "sideId": "s2",
              "instructions": "",
              "ruleRefId": null,
              "checklist": [],
              "onStart": [],
              "onMove": [],
              "warnings": [],
              "notes": null
            }
          ],
          "blocks": [
            {
              "firstPhaseId": "ph2",
              "lastPhaseId": "ph3",
              "count": 2,
              "label": "Round"
            },
            {
              "firstPhaseId": "ph1",
              "lastPhaseId": "ph3",
              "count": 3,
              "label": "Pass"
            }
          ],
          "alerts": [
            {
              "id": "a1",
              "name": "Low",
              "condition": "1 \u003E 0",
              "message": "Low fuel",
              "severity": "warning",
              "sideId": null,
              "notes": null
            }
          ],
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

    private const string OldPackSha256 = "65866ad87a4f3b0948c2b9a109910a19111560ce6d708087cffbed34f1422251";

    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static byte[] Zip(string packJson, string? formulaJson = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = new List<(string, string)> { ("pack.json", Lf(packJson)) };
            if (formulaJson != null) files.Add(("formulas/f1.json", Lf(formulaJson)));
            foreach (var (name, text) in files)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
        }
        return ms.ToArray();
    }

    private static string Entry(byte[] zipBytes, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var r = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static string PackJsonOf(RulesPack p) => Entry(RulesPackIo.ToBytes(p), "pack.json");

    private static RulesPack RoundTrip(RulesPack p) => RulesPackIo.FromBytes(RulesPackIo.ToBytes(p));

    private static RulesPack Base() => new()
    {
        Format = 4, Id = "t", Name = "TEST",
        Sides = new() { new SideDef { Id = "s1", Name = "North" }, new SideDef { Id = "s2", Name = "South" } },
        Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply" }, new PhaseDef { Id = "ph2", Name = "Move", SideId = "s1" } },
    };

    [Fact]
    public void B5_absent_fields_keep_bytes()
    {
        var loaded = RulesPackIo.FromBytes(Zip(OldPackJson, OldFormulaJson));
        var again = RulesPackIo.ToBytes(loaded);
        Assert.Equal(Lf(OldPackJson) + "\n", Entry(again, "pack.json"));
        Assert.Equal(Lf(OldFormulaJson) + "\n", Entry(again, "formulas/f1.json"));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(loaded));
        Assert.Equal(OldPackSha256, RulesPackIo.ComputeSha256(RulesPackIo.FromBytes(again)));
        Assert.Equal(4, RulesPackIo.CurrentFormat);
        Assert.Equal(3, loaded.Blocks[1].Count);
    }

    [Fact]
    public void New_fields_round_trip_camelCase()
    {
        var pack = Base() with
        {
            Phases = new()
            {
                new PhaseDef { Id = "ph1", Name = "Supply", Reactors = new() { "s1", "s2" } },
                new PhaseDef { Id = "ph2", Name = "Move", SideId = "s1" },
            },
            Schedule = new()
            {
                new ScheduleRow { Id = "sched-1", Turn = 3, Stage = 2, PhaseId = "ph1", Piece = "Side == 1", Action = ScheduleAction.Leave, ZoneMapId = "m1", FormulaId = "f1", Label = "Boats leave" },
            },
            LastTurn = 12,
            LastStage = 4,
            Victory = new()
            {
                new VictoryCondition { Id = "victory-1", Condition = "turn() > 3", SideId = "s2", Message = "South holds the ford", PhaseIds = new() { "ph1", "ph2" } },
            },
        };
        var json = PackJsonOf(pack);
        Assert.Contains("\"reactors\"", json);
        Assert.Contains("\"schedule\"", json);
        Assert.Contains("\"action\": \"leave\"", json);
        Assert.Contains("\"lastTurn\": 12", json);
        Assert.Contains("\"lastStage\": 4", json);
        Assert.Contains("\"victory\"", json);
        Assert.Contains("\"phaseIds\"", json);
        var back = RoundTrip(pack);
        Assert.Equal(new[] { "s1", "s2" }, back.Phases[0].Reactors);
        Assert.Equal(pack.Schedule, back.Schedule);
        Assert.Equal(12, back.LastTurn);
        Assert.Equal(4, back.LastStage);
        Assert.Single(back.Victory!);
        Assert.Equal(new[] { "ph1", "ph2" }, back.Victory![0].PhaseIds);
        Assert.Equal("s2", back.Victory[0].SideId);
        Assert.Equal(json, PackJsonOf(back));
        Assert.Equal("sched-1", back.ById<ScheduleRow>("sched-1")!.Id);
        Assert.Equal("victory-1", back.ById<VictoryCondition>("victory-1")!.Id);
        Assert.Null(back.ById<ScheduleRow>("nope"));
    }

    [Fact]
    public void Arrive_action_omitted()
    {
        var pack = Base() with { Schedule = new() { new ScheduleRow { Id = "sched-1", Turn = 1, PhaseId = "ph1", Piece = "true" } } };
        var json = PackJsonOf(pack);
        Assert.DoesNotContain("\"action\"", json);
        Assert.DoesNotContain("\"stage\"", json);
        Assert.DoesNotContain("zoneMapId", json);
        Assert.DoesNotContain("formulaId", json);
        Assert.DoesNotContain("\"label\"", json);
        Assert.Equal(ScheduleAction.Arrive, RoundTrip(pack).Schedule![0].Action);
        Assert.Null(RoundTrip(pack).Schedule![0].Stage);
    }

    [Fact]
    public void Empty_lists_become_null()
    {
        var pack = Base() with
        {
            Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Reactors = new() } },
            Schedule = new(),
            Victory = new(),
        };
        var back = RoundTrip(pack);
        Assert.Null(back.Phases[0].Reactors);
        Assert.Null(back.Schedule);
        Assert.Null(back.Victory);
        var json = PackJsonOf(back);
        Assert.DoesNotContain("reactors", json);
        Assert.DoesNotContain("schedule", json);
        Assert.DoesNotContain("victory", json);
        Assert.DoesNotContain("lastTurn", json);
        Assert.DoesNotContain("lastStage", json);
        // blank and duplicate reactors are cleaned; all blank -> null
        var cleaned = RoundTrip(Base() with { Phases = new() { new PhaseDef { Id = "ph1", Name = "Supply", Reactors = new() { " s1 ", "s1", "", " " } }, new PhaseDef { Id = "ph2", Name = "Move", Reactors = new() { " ", "" } } } });
        Assert.Equal(new[] { "s1" }, cleaned.Phases[0].Reactors);
        Assert.Null(cleaned.Phases[1].Reactors);
    }

    [Fact]
    public void Blank_texts_become_null()
    {
        var pack = Base() with
        {
            Schedule = new() { new ScheduleRow { Id = "sched-1", Turn = 1, PhaseId = "ph1", Piece = "true", ZoneMapId = " ", FormulaId = "", Label = "  " } },
            Victory = new() { new VictoryCondition { Id = "victory-1", Condition = "true", SideId = " ", Message = "", PhaseIds = new() { " ", "" } } },
        };
        var back = RoundTrip(pack);
        var r = back.Schedule![0];
        Assert.Null(r.ZoneMapId);
        Assert.Null(r.FormulaId);
        Assert.Null(r.Label);
        var v = back.Victory![0];
        Assert.Null(v.SideId);
        Assert.Null(v.Message);
        Assert.Null(v.PhaseIds);
        var cleaned = RoundTrip(Base() with { Victory = new() { new VictoryCondition { Id = "v", Condition = "true", PhaseIds = new() { " ph1 ", "ph1", "ph2", "" } } } });
        Assert.Equal(new[] { "ph1", "ph2" }, cleaned.Victory![0].PhaseIds);
    }

    [Fact]
    public void Null_texts_in_json_become_empty()
    {
        const string json = """
            {"format":4,"id":"t","name":"TEST","phases":[{"id":"ph1","name":"Supply"}],
             "schedule":[null,{"id":null,"turn":1,"phaseId":null,"piece":null}],
             "victory":[null,{"id":null,"condition":null}]}
            """;
        var back = RulesPackIo.FromBytes(Zip(json));
        var r = Assert.Single(back.Schedule!);
        Assert.Equal("", r.Id);
        Assert.Equal("", r.PhaseId);
        Assert.Equal("", r.Piece);
        var v = Assert.Single(back.Victory!);
        Assert.Equal("", v.Id);
        Assert.Equal("", v.Condition);
    }

    [Fact]
    public void Count_zero_block_round_trips()
    {
        var pack = Base() with { Blocks = new() { new PhaseBlock { FirstPhaseId = "ph1", LastPhaseId = "ph2", Count = 0, Label = "Round" } } };
        Assert.Contains("\"count\": 0", PackJsonOf(pack));
        Assert.Equal(0, RoundTrip(pack).Blocks[0].Count);
    }

    // ---- language ----

    private sealed class DefaultCtx : IEvalContext
    {
        public Value GetVariable(string name) => Value.Of(0);
        public Value Lookup(string tableName, Value row, Value? col) => Value.Of(0);
        public IReadOnlyList<int> RollDice(int n, int sides) => new[] { 1 };
        public bool Has(string property) => false;
        public Value PieceName() => Value.Of("x");
        public Value Marker(string key) => Value.Of("x");
    }

    [Fact]
    public void Segment_parses_no_arguments()
    {
        var call = Assert.IsType<Call>(Parser.Parse("segment()"));
        Assert.Empty(call.Args);
        Assert.Throws<ParseError>(() => Parser.Parse("segment(1)"));
    }

    [Fact]
    public void Segment_default_context_is_zero()
    {
        Assert.Equal(0, Evaluator.Evaluate(Parser.Parse("segment()"), new DefaultCtx()).Number);
    }

    [Fact]
    public void Segment_in_function_names() => Assert.Contains("segment", Evaluator.FunctionNames);
}
