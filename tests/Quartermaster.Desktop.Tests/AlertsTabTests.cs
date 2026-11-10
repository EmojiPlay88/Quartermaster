using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 9: the workbench Alerts tab. Headless, invented alerts over the Red/Blue phase fixture.</summary>
[Collection("MacFlag")]
public sealed class AlertsTabTests : IDisposable
{
    const string RedUnits = "startsWith(name(), \"Red\")";

    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-alertstab-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    /// <summary>A game with Red 1, Red 2, Blue 1 on "Main Map" and three alerts: Reds (matches 2), Blues (matches 1), Broken (does not parse).</summary>
    ModuleSession Session(bool withGame = true)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        if (withGame) s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p => p with
        {
            Sides = fp.Sides,
            Alerts = new()
            {
                new AlertDef { Id = "a-red", Name = "Reds", Condition = RedUnits, Message = "{unit} is red", Severity = AlertSeverity.Info, Notes = "n1" },
                new AlertDef { Id = "a-blue", Name = "Blues", Condition = "startsWith(name(), \"Blue\")", Severity = AlertSeverity.Critical, SideId = "blue" },
                new AlertDef { Id = "a-bad", Name = "Broken", Condition = "Fuel <" },
            },
        });
        return s;
    }

    AlertsTab Tab(ModuleSession s)
    {
        var tab = new AlertsTab(s, _dialogs);
        var w = new Window { Width = 900, Height = 600, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static List<string> Ids(ModuleSession s) => s.Rules!.Alerts.Select(a => a.Id).ToList();

    [AvaloniaFact]
    public void Lists_alerts_and_shows_the_first()
    {
        var s = Session();
        var tab = Tab(s);
        Assert.Equal(["Reds", "Blues", "Broken"], tab.Rows.Select(r => r.Name).ToList());
        Assert.Equal(["Info", "Critical", "Warning"], tab.Rows.Select(r => r.Severity).ToList());
        Assert.Equal("a-red", tab.SelectedAlertId);
        Assert.Equal("Reds", tab.NameBox.Text);
        Assert.Equal(RedUnits, tab.ConditionBox.Text);
        Assert.Equal("{unit} is red", tab.MessageBox.Text);
        Assert.Equal("n1", tab.NotesBox.Text);
        Assert.Equal(0, tab.SeverityBox.SelectedIndex);
        Assert.Equal(0, tab.SideBox.SelectedIndex);   // (any)
        Assert.Equal("2 matches now", tab.MatchStatusText.Text);
    }

    [AvaloniaFact]
    public void Select_alert()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectAlert("a-blue");
        Pump();
        Assert.Equal("a-blue", tab.SelectedAlertId);
        Assert.Equal("Blues", tab.NameBox.Text);
        Assert.Equal(2, tab.SeverityBox.SelectedIndex);
        Assert.Equal(2, tab.SideBox.SelectedIndex);   // (any), Red, Blue
        Assert.Equal("1 match now", tab.MatchStatusText.Text);
        tab.SelectAlert("nope");
        Assert.Equal("a-blue", tab.SelectedAlertId);
        tab.SelectAlert("a-bad");
        Pump();
        Assert.StartsWith("Does not parse", tab.MatchStatusText.Text);
        Assert.Equal(2, tab.AlertList.SelectedIndex);
    }

    [AvaloniaFact]
    public void Add_remove_move_alert()
    {
        var s = Session();
        var tab = Tab(s);
        tab.SelectAlert("a-blue");
        Pump();
        Click(tab.AddButton);   // after the selected alert
        Assert.Equal(4, s.Rules!.Alerts.Count);
        Assert.Equal("New alert", s.Rules.Alerts[2].Name);
        Assert.Equal(s.Rules.Alerts[2].Id, tab.SelectedAlertId);
        Assert.Equal("New alert", tab.NameBox.Text);
        Assert.Equal("no condition", tab.MatchStatusText.Text);

        var newId = tab.SelectedAlertId!;
        Click(tab.UpButton);
        Assert.Equal(["a-red", newId, "a-blue", "a-bad"], Ids(s));
        Assert.Equal(newId, tab.SelectedAlertId);
        Click(tab.UpButton);
        Click(tab.UpButton);   // already first: nothing
        Assert.Equal([newId, "a-red", "a-blue", "a-bad"], Ids(s));
        Click(tab.DownButton);
        Assert.Equal(["a-red", newId, "a-blue", "a-bad"], Ids(s));
        Assert.Equal(["Reds", "New alert", "Blues", "Broken"], tab.Rows.Select(r => r.Name).ToList());

        Click(tab.RemoveButton);
        Assert.Equal(["a-red", "a-blue", "a-bad"], Ids(s));
        Assert.Equal("a-blue", tab.SelectedAlertId);   // the next one takes its place
        tab.SelectAlert("a-bad");
        Pump();
        Click(tab.RemoveButton);
        Assert.Equal("a-blue", tab.SelectedAlertId);
        Click(tab.RemoveButton);
        Click(tab.RemoveButton);
        Assert.Empty(s.Rules.Alerts);
        Assert.Null(tab.SelectedAlertId);
        Assert.Equal("", tab.MatchStatusText.Text);
        Assert.False(tab.NameBox.IsEnabled && tab.NameBox.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Match_status_counts_viewer_visible_only()
    {
        var s = Session();
        var tab = Tab(s);
        Assert.Equal("2 matches now", tab.MatchStatusText.Text);
        // Red owns the only map: a Blue viewer sees none of its pieces, so the count must not leak them
        s.UpdateRules(p => p with { MapOwners = new() { new MapOwner { MapId = "Main Map", SideId = "red" } } });
        Pump();
        Assert.Equal("2 matches now", tab.MatchStatusText.Text);   // everything is visible while no sides are filtered
        s.SetViewSides(new HashSet<string> { "blue" });
        Pump();
        Assert.Equal("0 matches now", tab.MatchStatusText.Text);
        s.SetViewSides(new HashSet<string> { "red" });
        Pump();
        Assert.Equal("2 matches now", tab.MatchStatusText.Text);
        s.SetViewSides(null);
        Pump();
        Assert.Equal("2 matches now", tab.MatchStatusText.Text);
    }

    [AvaloniaFact]
    public void Match_status_follows_typing_and_no_game()
    {
        var s = Session();
        var tab = Tab(s);
        tab.ConditionBox.Text = "startsWith(name(), \"Blue\")";
        Pump();
        Assert.Equal("1 match now", tab.MatchStatusText.Text);
        tab.ConditionBox.Text = "Fuel <";
        Pump();
        Assert.StartsWith("Does not parse", tab.MatchStatusText.Text);
        Assert.Same(Avalonia.Media.Brushes.Firebrick, tab.MatchStatusText.Foreground);
        tab.ConditionBox.Text = "";
        Pump();
        Assert.Equal("no condition", tab.MatchStatusText.Text);
        Assert.NotSame(Avalonia.Media.Brushes.Firebrick, tab.MatchStatusText.Foreground);   // back to the theme brush
        var none = Session(withGame: false);
        Assert.Equal("no game open", Tab(none).MatchStatusText.Text);
    }

    [AvaloniaFact]
    public void Fields_commit_after_the_pause_and_on_commit_pending()
    {
        var s = Session();
        var tab = Tab(s);
        tab.NameBox.Text = "Crimson";
        tab.MessageBox.Text = "m";
        tab.NotesBox.Text = "";
        tab.CommitPending();
        var a = s.Rules!.Alerts[0];
        Assert.Equal("Crimson", a.Name);
        Assert.Equal("m", a.Message);
        Assert.Null(a.Notes);
        Assert.Equal("Crimson", tab.Rows[0].Name);
        tab.SeverityBox.SelectedIndex = 2;
        Assert.Equal(AlertSeverity.Critical, s.Rules.Alerts[0].Severity);
        Assert.Equal("Critical", tab.Rows[0].Severity);
        tab.SideBox.SelectedIndex = 1;
        Assert.Equal("red", s.Rules.Alerts[0].SideId);
        tab.SideBox.SelectedIndex = 0;
        Assert.Null(s.Rules.Alerts[0].SideId);
    }

    [AvaloniaFact]
    public void Selecting_another_alert_flushes_the_typed_text_into_the_old_one()
    {
        var s = Session();
        var tab = Tab(s);
        tab.NameBox.Text = "Crimson";   // not yet committed: the pause timer has not fired
        tab.AlertList.SelectedIndex = 1;
        Pump();
        Assert.Equal("Crimson", s.Rules!.Alerts[0].Name);
        Assert.Equal("Blues", s.Rules.Alerts[1].Name);
        Assert.Equal("a-blue", tab.SelectedAlertId);
        Assert.Equal("Blues", tab.NameBox.Text);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session();
        var tab = Tab(s);
        Click(tab.AddButton);
        tab.NameBox.Text = "Low fuel";
        tab.ConditionBox.Text = "Fuel < 20";
        tab.MessageBox.Text = "{unit} is low";
        tab.NotesBox.Text = "line one\nline two";
        tab.CommitPending();
        tab.SeverityBox.SelectedIndex = 2;
        tab.SideBox.SelectedIndex = 1;
        Click(tab.DownButton);
        tab.CommitPending();
        var path = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        Assert.Equal(s.Rules!.Alerts, back.Alerts);
        var low = back.Alerts.Single(a => a.Name == "Low fuel");
        Assert.Equal("Fuel < 20", low.Condition);
        Assert.Equal("{unit} is low", low.Message);
        Assert.Equal("line one\nline two", low.Notes);
        Assert.Equal(AlertSeverity.Critical, low.Severity);
        Assert.Equal("red", low.SideId);
        Assert.Equal(["a-red", "a-blue", "a-bad"], back.Alerts.Select(a => a.Id).Where(i => i != low.Id).ToList());
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var s = Session();
        s.UpdateRules(p => p with { Alerts = p.Alerts.Select(a => a.Id == "a-red" ? a with { Notes = "a\r\nb", Message = "x" } : a).ToList() });
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.SelectAlert("a-blue");
        Pump();
        tab.SelectAlert("a-red");
        Pump();
        tab.NotesBox.Focus();
        tab.NameBox.Focus();   // leaving a CRLF text box with no edit
        Pump();
        tab.SideBox.SelectedIndex = 0;
        tab.SeverityBox.SelectedIndex = 0;
        tab.CommitPending();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session();
        var tab = Tab(s);
        s.EnterGuestMode();
        tab.NameBox.Text = "Zed";
        tab.CommitPending();
        Pump();
        Assert.Equal("Reds", s.Rules!.Alerts[0].Name);
        Assert.Equal("Reds", tab.NameBox.Text);
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Alert edit failed"));
    }

    [AvaloniaFact]
    public void Detaching_flushes_pending_text()
    {
        var s = Session();
        var tab = Tab(s);
        tab.NameBox.Text = "Crimson";
        _windows[0].Content = null;
        Pump();
        Assert.Equal("Crimson", s.Rules!.Alerts[0].Name);
    }

    [AvaloniaFact]
    public void Tab_without_a_pack_is_empty_and_disabled()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache-np"), null);
        _sessions.Add(s);
        var tab = Tab(s);
        Assert.Empty(tab.Rows);
        Assert.Null(tab.SelectedAlertId);
        Click(tab.AddButton);
        Assert.Null(s.Rules);
    }
}
