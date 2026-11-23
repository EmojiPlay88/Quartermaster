using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>A formula limited to other phases is greyed in the piece menu's Calculate submenu, with its reason as the tooltip. Headless, invented module.</summary>
[Collection("MacFlag")]
public sealed class PieceMenuLimitTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-menulimit-" + Guid.NewGuid().ToString("N"));
    readonly FixtureModule _fx = PhaseFixture.Module();
    ModuleSession? _s;
    Window? _w;

    public void Dispose()
    {
        _w?.Close();
        _s?.Dispose();
        _fx.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [AvaloniaFact]
    public void PieceMenu_disabled_formula_tooltip()
    {
        _s = ModuleSession.Open(_fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _s.LoadSetup(_s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Alice", new[] { "red" }) });
        _s.NewRules("t");
        _s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(_s.Archive.Sha256);
            return p with { Formulas = p.Formulas.Select(f => f.Id == "f-water" ? f with { Phases = new() { "ph-move" } } : f).ToList() };
        });
        var view = new MapView(_s, _s.Model.Maps[0]) { MenuServices = new MapMenuServices { Dialogs = new FakeDialogs() } };
        _w = new Window { Width = 800, Height = 600, Content = view };
        _w.Show();

        view.OpenContextMenu(PhaseFixture.RedOne);   // no phase entered yet
        var menu = Assert.IsType<ContextMenu>(view.OpenMenu);
        var calc = menu.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Calculate");
        var water = calc.Items.OfType<MenuItem>().Single(m => MenuText.ToWinForms((string)m.Header!) == "Daily water");
        Assert.False(water.IsEnabled);
        Assert.Equal("Only in: Movement", ToolTip.GetTip(water));
        Assert.True(ToolTip.GetShowOnDisabled(water));
        menu.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
