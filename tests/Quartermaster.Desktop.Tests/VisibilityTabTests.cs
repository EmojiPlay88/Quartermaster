using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 9: the workbench Visibility tab. Headless, invented sides and holders.</summary>
[Collection("MacFlag")]
public sealed class VisibilityTabTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-vistab-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Properties Fuel, Water; formulas of the phase fixture; sides Red, Blue; holders Depot (Red) and Yard; map "Main Map".</summary>
    ModuleSession Session(Func<RulesPack, RulesPack>? tweak = null, Func<FixtureModule>? module = null)
    {
        var fx = module?.Invoke() ?? PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        s.NewRules("Invented");
        var fp = PhaseFixture.Pack("x");
        s.UpdateRules(p =>
        {
            p = p with
            {
                Properties = fp.Properties, Formulas = fp.Formulas, Sides = fp.Sides,
                Holders = new() { new HolderDef { Id = "depot", Name = "Depot", SideId = "red" }, new HolderDef { Id = "yard", Name = "Yard" } },
            };
            return tweak?.Invoke(p) ?? p;
        });
        return s;
    }

    VisibilityTab Tab(ModuleSession s)
    {
        var tab = new VisibilityTab(s);
        var w = new Window { Width = 1000, Height = 700, Content = tab };
        _windows.Add(w);
        w.Show();
        Pump();
        return tab;
    }

    static void Pick(EditGrid g, int row, int col, string item)
    {
        g.BeginEdit(row, col);
        ((ComboBox)g.Editor!).SelectedItem = item;
        g.CommitEdit();
        Pump();
    }

    [AvaloniaFact]
    public void Lists_everything_with_current_choices()
    {
        var s = Session();
        var tab = Tab(s);
        Assert.Equal(["Fuel", "Water"], Enumerable.Range(0, 2).Select(i => tab.PropertyGrid.Cell(i, 0)));
        Assert.Equal("Private", tab.PropertyGrid.Cell(0, 1));   // the default
        Assert.Equal("Red", tab.HolderGrid.Cell(0, 1));
        Assert.Equal("(none)", tab.HolderGrid.Cell(1, 1));
        Assert.Equal(1, tab.MapGrid.RowCount);
        Assert.Equal("(none)", tab.MapGrid.Cell(0, 1));
        Assert.Equal(s.Rules!.Formulas.Count, tab.FormulaChecks.Count);
        Assert.Equal("Weather roll  [Private dice]", tab.FormulaChecks[0].Content);
    }

    [AvaloniaFact]
    public void Property_visibility_and_private_rolls_commit()
    {
        var s = Session();
        var tab = Tab(s);
        Pick(tab.PropertyGrid, 1, 1, "Public");
        Assert.Equal(Visibility.Public, s.Rules!.Properties[1].Visibility);
        Assert.Equal(Visibility.Private, s.Rules.Properties[0].Visibility);
        Pick(tab.PropertyGrid, 1, 1, "Private");
        Assert.Equal(Visibility.Private, s.Rules.Properties[1].Visibility);

        tab.FormulaChecks[0].IsChecked = true;
        Pump();
        Assert.True(s.Rules.Formulas[0].PrivateRolls);
        Assert.False(s.Rules.Formulas[1].PrivateRolls);
        tab.FormulaChecks[0].IsChecked = false;
        Pump();
        Assert.False(s.Rules.Formulas[0].PrivateRolls);
    }

    [AvaloniaFact]
    public void Holder_and_map_owner_commit()
    {
        var s = Session();
        var tab = Tab(s);
        Pick(tab.HolderGrid, 1, 1, "Blue");
        Assert.Equal("blue", s.Rules!.Holders[1].SideId);
        Assert.Equal("red", s.Rules.Holders[0].SideId);
        Pick(tab.HolderGrid, 0, 1, "(none)");
        Assert.Null(s.Rules.Holders[0].SideId);

        Pick(tab.MapGrid, 0, 1, "Red");
        Assert.Equal("red", Assert.Single(s.Rules.MapOwners).SideId);
        Assert.Equal("Main Map", s.Rules.MapOwners[0].MapId);
        Pick(tab.MapGrid, 0, 1, "Blue");
        Assert.Equal("blue", Assert.Single(s.Rules.MapOwners).SideId);
        Pick(tab.MapGrid, 0, 1, "(none)");
        Assert.Empty(s.Rules.MapOwners);
    }

    [AvaloniaFact]
    public void Dangling_owner_is_shown_as_missing_and_left_alone()
    {
        var s = Session(p => p with
        {
            Holders = new() { new HolderDef { Id = "depot", Name = "Depot", SideId = "gone" } },
            MapOwners = new() { new MapOwner { MapId = "Lost Map", SideId = "red" } },
        });
        var tab = Tab(s);
        Assert.Equal("(missing)", tab.HolderGrid.Cell(0, 1));
        Assert.Equal(2, tab.MapGrid.RowCount);
        Assert.Equal("Lost Map (not in module)", tab.MapGrid.Cell(1, 0));
        Assert.Equal("Red", tab.MapGrid.Cell(1, 1));
        Pick(tab.HolderGrid, 0, 1, "(missing)");
        Assert.Equal("gone", s.Rules!.Holders[0].SideId);
        Pick(tab.MapGrid, 1, 1, "(none)");   // clearing the dangling map owner
        Assert.Empty(s.Rules.MapOwners);
    }

    /// <summary>The phase fixture's map plus an org display "Org A" (a chart-window WidgetMap).</summary>
    static FixtureModule OrgModule()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.ChartWindow name=\"Charts\"><VASSAL.build.widget.TabWidget entryName=\"Tab\">"
            + "<VASSAL.build.widget.WidgetMap mapName=\"Org A\"/></VASSAL.build.widget.TabWidget></VASSAL.build.module.ChartWindow></VASSAL.build.GameModule>");
        return new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
    }

    [AvaloniaFact]
    public void Org_display_suffix()
    {
        var s = Session(module: OrgModule);
        var tab = Tab(s);
        Assert.Equal(2, tab.MapGrid.RowCount);
        var names = Enumerable.Range(0, 2).Select(i => tab.MapGrid.Cell(i, 0)).ToList();
        Assert.Contains("Main Map", names);
        Assert.Contains("Org A (org display)", names);
        var org = s.Model.Maps.Single(m => m.IsOrgDisplay);
        var row = names.IndexOf("Org A (org display)");
        Pick(tab.MapGrid, row, 1, "Blue");
        Assert.Equal(org.Id, Assert.Single(s.Rules!.MapOwners).MapId);
    }

    [AvaloniaFact]
    public void Edits_round_trip_through_qrules()
    {
        Directory.CreateDirectory(_tmp);
        var s = Session();
        var tab = Tab(s);
        Pick(tab.PropertyGrid, 0, 1, "Public");
        Pick(tab.HolderGrid, 1, 1, "Blue");
        Pick(tab.MapGrid, 0, 1, "Red");
        tab.FormulaChecks[1].IsChecked = true;
        tab.CommitPending();
        Pump();
        var path = Path.Combine(_tmp, "p.qrules");
        RulesPackIo.Save(s.Rules!, path);
        var back = RulesPackIo.Load(path);
        Assert.Equal(4, back.Format);
        Assert.Equal(Visibility.Public, back.Properties.Single(x => x.Id == "fuel").Visibility);
        Assert.Equal("blue", back.Holders.Single(h => h.Id == "yard").SideId);
        Assert.Equal("red", Assert.Single(back.MapOwners).SideId);
        Assert.True(back.Formulas.Single(f => f.Id == s.Rules!.Formulas[1].Id).PrivateRolls);
        Assert.False(back.Formulas.Single(f => f.Id == s.Rules!.Formulas[0].Id).PrivateRolls);
        Assert.Equal(s.Rules!.Holders.Select(h => (h.Id, h.SideId)).OrderBy(x => x.Id), back.Holders.Select(h => (h.Id, h.SideId)).OrderBy(x => x.Id));
    }

    [AvaloniaFact]
    public void Opening_and_selecting_leaves_pack_untouched()
    {
        var s = Session();
        var pack = s.Rules;
        var dirty = s.RulesDirty;
        var tab = Tab(s);
        tab.PropertyGrid.SelectCell(1, 1);
        tab.HolderGrid.BeginEdit(0, 1);   // opened and left as it was
        tab.CommitPending();
        tab.MapGrid.BeginEdit(0, 1);
        tab.CommitPending();
        Pump();
        Assert.Same(pack, s.Rules);
        Assert.Equal(dirty, s.RulesDirty);
    }

    [AvaloniaFact]
    public void Refused_edit_in_guest_mode_redraws_from_the_pack()
    {
        var s = Session();
        var tab = Tab(s);
        s.EnterGuestMode();
        Pick(tab.PropertyGrid, 0, 1, "Public");
        Assert.Equal(Visibility.Private, s.Rules!.Properties[0].Visibility);
        Assert.Equal("Private", tab.PropertyGrid.Cell(0, 1));
        Assert.Contains(s.Log.Items, e => e.Message.StartsWith("Visibility edit failed"));
        tab.FormulaChecks[0].IsChecked = true;
        Pump();
        Assert.False(s.Rules.Formulas[0].PrivateRolls);
        Assert.False(tab.FormulaChecks[0].IsChecked);
    }

    [AvaloniaFact]
    public void External_change_rebuilds()
    {
        var s = Session();
        var tab = Tab(s);
        s.UpdateRules(p => p with { Holders = p.Holders.Select(h => h.Id == "yard" ? h with { SideId = "blue" } : h).ToList() });
        Pump();
        Assert.Equal("Blue", tab.HolderGrid.Cell(1, 1));
    }
}
