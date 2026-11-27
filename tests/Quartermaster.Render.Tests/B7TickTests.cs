using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B7: a tick or acknowledge line is stamped with the phase's side and logs the rendered text. Invented PhaseFixture data.</summary>
public class B7TickTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7tick-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public B7TickTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    /// <summary>Movement (red) checklist: [0] links to the Combat item "fight" and the Supply item "stock"; Combat (no side) has "fight";
    /// Supply (red) has "stock". The current phase is Movement.</summary>
    private (ModuleSession S, PhaseController C) Make(string movementItem)
    {
        var fx = PhaseFixture.Module(true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with
            {
                Phases = p.Phases.Select(ph => ph.Id switch
                {
                    "ph-move" => ph with { Checklist = new() { new ChecklistItem { Id = "go", Text = movementItem }, new ChecklistItem { Id = "mv2", Text = "Advance" } },
                        Warnings = new() { new PhaseWarning { Condition = "1 > 0", Message = "Out of fuel" } } },
                    "ph-combat" => ph with { Checklist = new() { new ChecklistItem { Id = "fight", Text = "Resolve attacks" } } },
                    "ph-supply" => ph with { Checklist = new() { new ChecklistItem { Id = "stock", Text = "Count stores" } } },
                    _ => ph,
                }).ToList(),
            };
        });
        var c = new PhaseController(s, (cmd, _) => cmd);
        c.Start();
        while (s.Game!.State.Position is { } pos && s.Rules!.Phases[pos.PhaseIndex].Id != "ph-move") c.Advance();
        return (s, c);
    }

    private static LogEntry Line(ModuleSession s, string prefix) => s.Game!.State.Log.Single(l => l.Kind == LogKind.Turn && l.Text.StartsWith(prefix));

    [Fact]
    public void Tick_renders_links_same_side()
    {
        var (s, c) = Make("After {item:stock} finish");   // Supply is red, like Movement
        c.Tick(0, true);
        var line = Line(s, "Done: ");
        Assert.Equal("Done: After \"Count stores\" finish", line.Text);
        Assert.Equal("red", line.SideId);
    }

    [Fact]
    public void Tick_keeps_link_to_other_side_phase_raw()
    {
        var (s, c) = Make("Then {item:fight} and {item:stock}");
        s.UpdateRules(p => p with { Phases = p.Phases.Select(ph => ph.Id == "ph-combat" ? ph with { SideId = "blue" } : ph).ToList() });
        c.Tick(0, true);
        Assert.Equal("Done: Then {item:fight} and \"Count stores\"", Line(s, "Done: ").Text);   // blue's item stays a link; red's reads
    }

    [Fact]
    public void Tick_side_less_phase_unstamped_and_ack_stamped()
    {
        var (s, c) = Make("Plain");
        c.Tick(0, true);
        Assert.Equal("red", Line(s, "Done: ").SideId);
        c.Acknowledge(0);
        Assert.Equal("red", Line(s, "Acknowledged: ").SideId);
    }

    [Fact]
    public void Hotseat_other_player_log_has_no_tick_text()
    {
        var (s, c) = Make("Secret {item:stock} plan");
        c.Tick(0, true);
        c.Acknowledge(0);
        s.SetViewSides(Blue);
        var log = s.View!.Log;
        Assert.DoesNotContain(log, l => l.Kind == LogKind.Turn && (l.Text.StartsWith("Done: ") || l.Text.StartsWith("Acknowledged: ")));
        Assert.DoesNotContain("Count stores", string.Join("|", log.Select(l => l.Text)));
    }

    [Fact]
    public void Tick_in_phase_of_unknown_side_fails_closed()
    {
        // Final review F5: a phase whose side is not in the pack stamps its lines with that side, so no player view shows them.
        var (s, c) = Make("Plain");
        s.UpdateRules(p => p with { Phases = p.Phases.Select(ph => ph.Id == "ph-move" ? ph with { SideId = "ghost" } : ph).ToList() });
        c.Tick(0, true);
        c.Acknowledge(0);
        Assert.Equal("ghost", Line(s, "Done: ").SideId);
        Assert.Equal("ghost", Line(s, "Acknowledged: ").SideId);
        foreach (var side in new[] { "red", "blue" })
        {
            s.SetViewSides(new HashSet<string> { side });
            Assert.DoesNotContain(s.View!.Log, l => l.Kind == LogKind.Turn && (l.Text.StartsWith("Done: ") || l.Text.StartsWith("Acknowledged: ")));
        }
    }
}
