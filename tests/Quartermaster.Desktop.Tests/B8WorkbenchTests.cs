using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>B8 task 5: the Workbench Sides box editors for alliance, former ids and Merge into…. Headless, invented data.</summary>
[Collection("MacFlag")]
public sealed class B8WorkbenchTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b8wb-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    ModuleSession Session(bool noSides = false)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p => p with
        {
            Sides = noSides ? new() : new()
            {
                new SideDef { Id = "north", Name = "Northern League", FormerIds = new() { "league" } },
                new SideDef { Id = "south", Name = "Southern Compact" },
                new SideDef { Id = "east", Name = "East Shore" },
            },
            Formulas = fp.Formulas,
            Phases = new() { new PhaseDef { Id = "p-east", Name = "Supply", SideId = "east" }, new PhaseDef { Id = "p-south", Name = "Move", SideId = "south" } },
        });
        return s;
    }

    PhasesTab Phases(ModuleSession s)
    {
        var tab = new PhasesTab(s, _dialogs, _ => null, null);
        var win = new Window { Width = 1100, Height = 800, Content = tab };
        _windows.Add(win);
        win.Show();
        Pump();
        return tab;
    }

    static SideDef Side(ModuleSession s, string id) => s.Rules!.Sides.Single(x => x.Id == id);

    [AvaloniaFact]
    public void Alliance_box_commits_on_flush()
    {
        var s = Session();
        var tab = Phases(s);
        tab.SideList.SelectedIndex = 1;
        Pump();
        Assert.Equal("", tab.AllianceBox.Text ?? "");
        tab.AllianceBox.Text = "  Upriver Pact ";
        tab.CommitPending();
        Assert.Equal("Upriver Pact", Side(s, "south").AllianceId);
        Assert.Null(Side(s, "north").AllianceId);
        tab.AllianceBox.Text = " ";
        tab.CommitPending();
        Assert.Null(Side(s, "south").AllianceId);
    }

    [AvaloniaFact]
    public void Former_ids_box_commits_on_flush()
    {
        var s = Session();
        var tab = Phases(s);
        tab.SideList.SelectedIndex = 0;
        Pump();
        Assert.Equal("league", tab.FormerIdsBox.Text);
        tab.FormerIdsBox.Text = " league , old,, old ";
        tab.CommitPending();
        Assert.Equal(new[] { "league", "old" }, Side(s, "north").FormerIds);
        tab.FormerIdsBox.Text = "";
        tab.CommitPending();
        Assert.Null(Side(s, "north").FormerIds);
    }

    [AvaloniaFact]
    public void Boxes_disabled_without_side()
    {
        var s = Session(noSides: true);
        var tab = Phases(s);
        Assert.False(tab.AllianceBox.IsEnabled);
        Assert.False(tab.FormerIdsBox.IsEnabled);
        s.UpdateRules(p => p with { Sides = new() { new SideDef { Id = "north", Name = "Northern League" } } });
        Pump();
        Assert.True(tab.AllianceBox.IsEnabled);
        Assert.True(tab.FormerIdsBox.IsEnabled);
    }

    [AvaloniaFact]
    public void Merge_button_merges_after_confirm()
    {
        var s = Session();
        var tab = Phases(s);
        tab.SideList.SelectedIndex = 2;   // East Shore
        Pump();
        _dialogs.Texts.Enqueue("southern compact");
        _dialogs.Answer = true;
        Click(tab.SideMergeButton);
        Assert.Equal("Merge side|Name of the side to merge \"East Shore\" into:|", _dialogs.TextPrompts[^1]);
        Assert.Equal("Merge \"East Shore\" into \"Southern Compact\"? Its phases, holders, maps and alerts move to \"Southern Compact\", and saved games that used it read as \"Southern Compact\".", _dialogs.Questions[^1].Message);
        Assert.Equal("Merge side", _dialogs.Questions[^1].Caption);
        Assert.True(_dialogs.YesNoDefaultNo[^1]);
        Assert.Equal(["Northern League", "Southern Compact"], s.Rules!.Sides.Select(x => x.Name).ToList());
        Assert.Equal(new[] { "east" }, Side(s, "south").FormerIds);
        Assert.All(s.Rules.Phases, p => Assert.Equal("south", p.SideId));
        Assert.Equal(1, tab.SideList.SelectedIndex);   // the target is selected
    }

    [AvaloniaFact]
    public void Merge_typed_id_resolves_the_side()
    {
        var s = Session();
        var tab = Phases(s);
        tab.SideList.SelectedIndex = 2;
        Pump();
        _dialogs.Texts.Enqueue("south");   // an id, not a name
        _dialogs.Answer = true;
        Click(tab.SideMergeButton);
        Assert.Equal(["Northern League", "Southern Compact"], s.Rules!.Sides.Select(x => x.Name).ToList());
        Assert.All(s.Rules.Phases, p => Assert.Equal("south", p.SideId));
    }

    [AvaloniaFact]
    public void Merge_cancelled_changes_nothing()
    {
        var s = Session();
        var tab = Phases(s);
        tab.SideList.SelectedIndex = 2;
        Pump();
        var before = s.Rules;
        _dialogs.Texts.Enqueue(null);
        Click(tab.SideMergeButton);   // prompt cancelled
        Assert.Same(before, s.Rules);
        _dialogs.Texts.Enqueue("Southern Compact");
        _dialogs.Answer = false;
        Click(tab.SideMergeButton);   // confirm declined
        Assert.Same(before, s.Rules);
        Assert.Equal(3, s.Rules!.Sides.Count);
        var warnings = _dialogs.Warnings.Count;
        _dialogs.Texts.Enqueue("East Shore");
        Click(tab.SideMergeButton);
        Assert.Equal("A side cannot be merged into itself", _dialogs.Warnings[^1].Message);
        _dialogs.Texts.Enqueue("Nowhere");
        Click(tab.SideMergeButton);
        Assert.Equal("No side is named \"Nowhere\"", _dialogs.Warnings[^1].Message);
        Assert.Equal(warnings + 2, _dialogs.Warnings.Count);
        Assert.Same(before, s.Rules);
    }

    [AvaloniaFact]
    public void Tooltips_text()
    {
        var tab = Phases(Session());
        Assert.Equal("Sides with the same alliance see what each other sees, may act on each other's units and in each other's phases, and a phase's formulas for each unit and on move reach every allied side's counters. Blank = no alliance.", ToolTip.GetTip(tab.AllianceBox));
        Assert.Equal("Ids this side had in older versions of the pack, separated by commas. Saved games that use them read as this side.", ToolTip.GetTip(tab.FormerIdsBox));
        Assert.Equal("Merge into…", tab.SideMergeButton.Content);
    }

    [AvaloniaFact]
    public void Problems_list_shows_former_id_error()
    {
        var s = Session();
        s.UpdateRules(p => p with { Sides = p.Sides.Select(x => x.Id == "south" ? x with { FormerIds = new() { "north" } } : x).ToList() });
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        wb.CommitPending();
        Pump();
        Assert.Contains(wb.IssueRows, r => r.Problem.Contains("Side \"Southern Compact\": former id \"north\" is the id of side \"Northern League\"", StringComparison.Ordinal));
    }
}
