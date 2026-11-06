using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class NotesSyncTests
{
    [Fact]
    public void ShouldCommit_OnlyWhenTextDiffers()
    {
        Assert.False(NotesSync.ShouldCommit("", null));
        Assert.False(NotesSync.ShouldCommit("", ""));
        Assert.False(NotesSync.ShouldCommit("a", "a"));
        Assert.True(NotesSync.ShouldCommit("a", null));
        Assert.True(NotesSync.ShouldCommit("", "old"));
    }

    [Fact]
    public void ShouldReload_NotWhileFocusedOrPending()
    {
        Assert.True(NotesSync.ShouldReload(false, false));
        Assert.False(NotesSync.ShouldReload(true, false));
        Assert.False(NotesSync.ShouldReload(false, true));
    }
}
