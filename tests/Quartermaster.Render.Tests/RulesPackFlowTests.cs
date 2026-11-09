using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

public class RulesPackFlowTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-rpf-" + Guid.NewGuid().ToString("N"));

    public RulesPackFlowTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private sealed class FakeUi : IRulesPackUi
    {
        public bool? SaveAnswer;
        public string? OpenPath, SavePath, Name;
        public List<string> Asked = new(), Warnings = new();
        public string? SuggestedName, NameTitle, NamePrompt, NameDefault;
        public bool? AskSaveChanges(string packName) { Asked.Add(packName); return SaveAnswer; }
        public string? PickOpen() => OpenPath;
        public string? PickSave(string suggestedName) { SuggestedName = suggestedName; return SavePath; }
        public string? AskName(string title, string prompt, string def) { NameTitle = title; NamePrompt = prompt; NameDefault = def; return Name; }
        public void Warn(string text) => Warnings.Add(text);
    }

    private ModuleSession Open(FixtureModule fx) => ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);

    private static FixtureModule Fx() => new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());

    [Fact]
    public void Dirty_pack_yes_saves()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Old");
        var path = Path.Combine(_tmp, "old.qrules");
        var ui = new FakeUi { SaveAnswer = true, SavePath = path };
        Assert.True(new RulesPackFlow(s, ui).ConfirmSaved());
        Assert.Equal(new[] { "Old" }, ui.Asked);
        Assert.True(File.Exists(path));
        Assert.False(s.RulesDirty);
    }

    [Fact]
    public void Dirty_pack_no_discards()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Old");
        var ui = new FakeUi { SaveAnswer = false };
        Assert.True(new RulesPackFlow(s, ui).ConfirmSaved());
        Assert.Empty(Directory.GetFiles(_tmp, "*.qrules"));
        Assert.True(s.RulesDirty);
    }

    [Fact]
    public void Dirty_pack_cancel_aborts()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Old");
        var ui = new FakeUi { SaveAnswer = null, Name = "Other" };
        var flow = new RulesPackFlow(s, ui);
        Assert.False(flow.ConfirmSaved());
        flow.New();
        Assert.Equal("Old", s.Rules!.Name);
        Assert.Null(ui.NameTitle);   // never even asked for a name
    }

    [Fact]
    public void Dirty_pack_yes_but_save_dialog_cancelled_aborts()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Old");
        var ui = new FakeUi { SaveAnswer = true, SavePath = null };
        Assert.False(new RulesPackFlow(s, ui).ConfirmSaved());
    }

    [Fact]
    public void Clean_session_is_not_asked()
    {
        using var fx = Fx();
        using var s = Open(fx);
        var ui = new FakeUi();
        Assert.True(new RulesPackFlow(s, ui).ConfirmSaved());
        Assert.Empty(ui.Asked);
    }

    [Fact]
    public void Save_failure_during_confirm_is_warned_and_aborts()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Old");
        var bad = Path.Combine(_tmp, "no-such-dir", "x.qrules");
        var ui = new FakeUi { SaveAnswer = true, SavePath = bad };
        Assert.False(new RulesPackFlow(s, ui).ConfirmSaved());
        Assert.Single(ui.Warnings);
    }

    [Fact]
    public void New_asks_name_and_creates()
    {
        using var fx = Fx();
        using var s = Open(fx);
        var ui = new FakeUi { Name = "  Mine  " };
        var flow = new RulesPackFlow(s, ui);
        flow.New();
        Assert.Equal(("New Rules Pack", "Name of the new rules pack:", "Rules"), (ui.NameTitle, ui.NamePrompt, ui.NameDefault));
        Assert.Equal("Mine", s.Rules!.Name);
        ui.SaveAnswer = false;   // discard the dirty "Mine"
        ui.Name = "   ";
        flow.New();
        Assert.Equal("Mine", s.Rules!.Name);   // a blank name creates nothing
    }

    [Fact]
    public void Open_loads_the_picked_pack()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("P");
        var path = Path.Combine(_tmp, "p.qrules");
        s.SaveRules(path);
        s.NewRules("Q");
        s.SaveRules(Path.Combine(_tmp, "q.qrules"));
        var flow = new RulesPackFlow(s, new FakeUi { OpenPath = path });
        flow.Open();
        Assert.Equal("P", s.Rules!.Name);
        Assert.Equal(path, s.RulesPath);
    }

    [Fact]
    public void Save_as_writes_qrules()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Pack");
        var path = Path.Combine(_tmp, "as.qrules");
        var ui = new FakeUi { SavePath = path };
        Assert.True(new RulesPackFlow(s, ui).Save(saveAs: true));
        Assert.Equal("Pack", ui.SuggestedName);
        Assert.True(File.Exists(path));
        Assert.Equal(path, s.RulesPath);
        ui.SavePath = null;   // a plain save with a known path does not ask again
        Assert.True(new RulesPackFlow(s, ui).Save(false));
    }

    [Fact]
    public void Save_cancelled_dialog_returns_false()
    {
        using var fx = Fx();
        using var s = Open(fx);
        s.NewRules("Pack");
        Assert.False(new RulesPackFlow(s, new FakeUi()).Save(false));
    }

    [Fact]
    public void Save_without_pack_is_a_noop_true()
    {
        using var fx = Fx();
        using var s = Open(fx);
        Assert.True(new RulesPackFlow(s, new FakeUi()).Save(false));
    }
}
