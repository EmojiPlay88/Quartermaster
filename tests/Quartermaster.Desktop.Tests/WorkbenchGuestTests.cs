using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5c task 5: an online guest's Rules Workbench is read-only (WinForms WorkbenchWindow.ApplyReadOnly): the strip, the
/// "(read-only)" title, every tab's editors switched off, no "no pack" prompt and no "Use as rule ref". The session is marked guest
/// without a network, as the other guest-flag tests do. Invented TEST module and pack; headless.
/// </summary>
public sealed class WorkbenchGuestTests : IDisposable
{
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-wbguest-" + Guid.NewGuid().ToString("N"));
    readonly List<Window> _windows = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public WorkbenchGuestTests()
    {
        Directory.CreateDirectory(_tmp);
        WorkbenchWindow.PruneCaches = _ => { };   // never the real cache folders
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static RulesPack Pack(string sha) => PhaseFixture.Pack(sha) with { Name = "TEST guest pack" };

    ModuleSession Session()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        return s;
    }

    WorkbenchWindow Bench(ModuleSession s)
    {
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        return wb;
    }

    IEnumerable<Control> TabContents(WorkbenchWindow wb) => wb.Tabs.Items.OfType<TabItem>().Select(t => (Control)t.Content!);

    [AvaloniaFact]
    public void Guest_workbench_is_read_only()
    {
        var s = Session();
        s.EnterGuestMode();
        var wb = Bench(s);
        Assert.False(wb.PromptShown);   // a guest waits for the host's pack: no "New rules pack…" offer

        s.LoadGuestRules(Pack(s.Archive.Sha256));   // the host's pack arrives with the welcome
        Pump();
        Assert.True(wb.ReadOnlyShown);
        Assert.Equal("Read-only: the host's rules pack for this online game. Only the host can change it.", wb.ReadOnlyText);
        Assert.Equal("Rules Workbench — TEST guest pack (read-only)", wb.Title);
        Assert.EndsWith(" (read-only)", wb.Title, StringComparison.Ordinal);
        Assert.False(wb.PromptShown);
        Assert.Equal(6, TabContents(wb).Count());
        Assert.All(TabContents(wb), c => Assert.False(c.IsEnabled));
        Assert.False(wb.Formulas.IsEnabled);

        // The rule-ref search action stays unavailable even with a formula selected.
        wb.ShowFormula("f-water");
        Pump();
        Assert.False(wb.SearchPanelForTests.CanUseRuleRef!());

        // A later snapshot replacing the pack re-applies it.
        s.LoadGuestRules(Pack(s.Archive.Sha256) with { Name = "TEST guest pack 2" });
        Pump();
        Assert.Equal("Rules Workbench — TEST guest pack 2 (read-only)", wb.Title);
        Assert.True(wb.ReadOnlyShown);
        Assert.All(TabContents(wb), c => Assert.False(c.IsEnabled));
    }

    [AvaloniaFact]
    public void Host_workbench_is_editable()
    {
        var s = Session();
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var wb = Bench(s);
        Assert.False(wb.ReadOnlyShown);
        Assert.Equal("Rules Workbench — TEST guest pack", wb.Title);
        Assert.All(TabContents(wb), c => Assert.True(c.IsEnabled));
        wb.ShowFormula("f-water");
        Pump();
        Assert.True(wb.SearchPanelForTests.CanUseRuleRef!());
    }
}
