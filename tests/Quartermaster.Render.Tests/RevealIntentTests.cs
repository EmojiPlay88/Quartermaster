using System.Globalization;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B7 #47: a guest may run a formula whose Reveal names another side's holder (it writes nothing; the host reads the value), and nothing
/// the host sends before Apply carries the revealed value. Invented Red/Blue names and numbers only.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class RevealIntentTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-reveal-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private const double Secret = 61616;
    private static readonly string SecretText = Secret.ToString(CultureInfo.InvariantCulture);
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    public RevealIntentTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <summary>"Supply" is a Private value of the Red side's depot; "Spy on the depot" reveals it.</summary>
    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "RedDepot", SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-spy", Name = "Spy on the depot", Text = "1",
                    Outputs = new() { new FormulaOutput { Action = OutputAction.Reveal, Target = OutputTarget.Holder, HolderId = "reddepot", Property = "supply" } },
                },
                new FormulaDef
                {
                    Id = "f-spy-wide", Name = "Glimpse the depot", Text = "1",
                    Outputs = new() { new FormulaOutput { Action = OutputAction.Reveal, Target = OutputTarget.Holder, HolderId = "reddepot", Property = "supply", Margin = "4" } },
                },
            }).ToList(),
        };
    }

    private (ModuleSession S, IntentExecutor X) Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(2, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, Secret, "manual", null, null, null, null));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        return (s, new IntentExecutor(s, c));
    }

    [Fact]
    public void Guest_reveal_other_side_holder_allowed()
    {
        var (s, x) = Open();
        var r = x.Execute("p2", Blue, new FormulaIntent("f-spy", null, null, new Dictionary<string, string>(), true));
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Verdict!.Applied);
        var line = $"RedDepot: Supply {SecretText}";
        Assert.Contains(s.Game!.State.Log, l => l.Kind == LogKind.System && l.Text == line);
        // the line is the game's outcome: the Blue player's view keeps it, the value itself stays the Red side's
        var view = PlayerView.For(s.Game.State, s.Rules!, s.Model, Blue);
        Assert.Contains(view.Log, l => l.Text == line);
        Assert.False(view.Ledgers.TryGetValue(LedgerKey.Holder("reddepot"), out var props) && props.ContainsKey("supply"));
        Assert.Equal(Secret, Ledger.Get(s.Game.State, LedgerKey.Holder("reddepot"), "supply"));   // nothing written
    }

    [Fact]
    public void Guest_preview_carries_no_reveal_value()
    {
        var (s, x) = Open();
        var log = s.Game!.Log.Count;
        foreach (var id in new[] { "f-spy", "f-spy-wide" })
        {
            var r = x.Execute("p2", Blue, new FormulaIntent(id, null, null, new Dictionary<string, string>(), true, true));
            Assert.NotNull(r.Verdict);
            Assert.False(r.Verdict!.Applied);
            Assert.Null(r.Verdict.Message);   // not refused: the host ran it and it can apply
            var json = JsonSerializer.Serialize(r, QuartermasterJson.Options);
            Assert.DoesNotContain(SecretText, json);
            Assert.DoesNotContain("between", json);
            Assert.True(r.Verdict.Rolls is null or { Count: 0 });   // the window die is never sent
        }
        Assert.Equal(log, s.Game.Log.Count);   // a preview executes nothing
        // the host's own screen for the Blue player: the dialog never shows the value before Apply
        s.SetViewSides(Blue);
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f-spy")!, null);
        FormulaMasking.Configure(m, s);
        var v = m.Evaluate(s.Game.State);
        var shown = string.Join("|", v.Boxes.SelectMany(b => new[] { b.Text, b.Tooltip ?? "", b.CaptionSuffix }))
            + "|" + v.ResultText + "|" + v.WorkingText + "|" + v.Status;
        Assert.DoesNotContain(SecretText, shown);
        Assert.Contains("reveal Supply", v.Trace.Working);
    }

    [Fact]
    public void WithOutputAction_reveal_keeps_property_drops_key()
    {
        var o = new FormulaOutput
        {
            Target = OutputTarget.Holder, HolderId = "reddepot", Property = "supply", Key = "hex()", Slot = "Wreck", Layer = "Levels",
            ToMapId = "map-b", PropertyChoice = new() { "supply" }, PropertySelect = "1",
        };
        var reveal = FormulaEdit.WithOutputAction(o, OutputAction.Reveal);
        Assert.Equal(OutputAction.Reveal, reveal.Action);
        Assert.Equal("supply", reveal.Property);
        Assert.Null(reveal.Key);
        Assert.Null(reveal.Slot);
        Assert.Null(reveal.Layer);
        Assert.Null(reveal.ToMapId);
        Assert.Null(reveal.PropertyChoice);
        Assert.Null(reveal.PropertySelect);

        // Margin: blank is null; kept only for Reveal
        var wide = FormulaEdit.WithMargin(reveal, " 2 ");
        Assert.Equal("2", wide.Margin);
        Assert.Null(FormulaEdit.WithMargin(reveal, "  ").Margin);
        Assert.Equal("2", FormulaEdit.WithOutputAction(wide, OutputAction.Reveal).Margin);
        var write = FormulaEdit.WithOutputAction(wide, OutputAction.Write);
        Assert.Null(write.Margin);
        Assert.Equal("supply", write.Property);
        Assert.Equal("", FormulaEdit.WithOutputAction(wide, OutputAction.Remove).Property);
        Assert.Null(FormulaEdit.WithOutputAction(wide, OutputAction.Remove).Margin);
    }
}
