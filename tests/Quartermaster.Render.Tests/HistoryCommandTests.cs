using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class HistoryCommandTests
{
    [Fact]
    public void Registry_HasUndoRedoStepBackDefaults()
    {
        var r = new CommandRegistry();
        foreach (var d in CommandDefaults.All) r.Register(d);
        var none = new Dictionary<string, string>();
        Assert.Equal("Ctrl+Z", r.EffectiveShortcut("edit.undo", none));
        Assert.Equal("Ctrl+Y", r.EffectiveShortcut("edit.redo", none));
        Assert.Equal("Ctrl+Shift+Z", r.EffectiveShortcut("edit.redoAlt", none));
        Assert.Equal("Ctrl+Left", r.EffectiveShortcut("phase.stepBack", none));
        Assert.Contains(r.All, d => d.Id == "phase.stepBackTo");
        Assert.Equal("", r.EffectiveShortcut("phase.stepBackTo", none));
        Assert.Empty(r.Conflicts(none));
    }

    [Theory]
    [InlineData("Ctrl+Z")]
    [InlineData("Ctrl+Y")]
    [InlineData("Ctrl+Shift+Z")]
    [InlineData("Ctrl+Left")]
    public void ShortcutGate_HistoryKeys_InTextControl_NotDispatched(string shortcut)
    {
        Assert.False(ShortcutGate.ShouldDispatch(shortcut, true, false));
        Assert.True(ShortcutGate.ShouldDispatch(shortcut, false, false));
    }

    [Fact]
    public void ToolbarIds_IncludeHistoryButtonsBeforeNextTurn()
    {
        var ids = ToolbarIds.All.Select(a => a.Id).ToList();
        var next = ids.IndexOf("turn.next");
        Assert.Equal(new[] { "edit.undo", "edit.redo", "phase.stepBack" }, ids.GetRange(next - 3, 3));
    }
}
