using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests;

/// <summary>What the app does when the updater helper starts it with --updated-from / --update-failed.</summary>
public class UpdateStartupTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-upd-start-" + Guid.NewGuid().ToString("N"), "Updates");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, true); } catch (Exception) { }
    }

    [Fact]
    public void ConfirmStarted_writes_the_marker_the_helper_waits_for()
    {
        var v = new Version(0, 2, 0);
        var n = UpdateStartup.ConfirmStarted(_root, v);
        Assert.True(File.Exists(UpdateInstaller.MarkerPath(_root, v)));
        Assert.Equal("Updated to 0.2.0.", n.Text);
        Assert.Null(n.Link);
    }

    [Fact]
    public void ConfirmStarted_removes_older_downloads_and_keeps_the_current_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "0.1.0", "staged"));
        Directory.CreateDirectory(Path.Combine(_root, "0.2.0", "helper"));
        File.WriteAllText(Path.Combine(_root, "started-0.1.0"), "x");
        UpdateStartup.ConfirmStarted(_root, new Version(0, 2, 0));
        Assert.False(Directory.Exists(Path.Combine(_root, "0.1.0")));
        Assert.True(Directory.Exists(Path.Combine(_root, "0.2.0", "helper")));
        Assert.False(File.Exists(Path.Combine(_root, "started-0.1.0")));
    }

    [Fact]
    public void ConfirmStarted_never_throws_when_the_marker_cannot_be_written()
    {
        // The updates "folder" is a file: the marker cannot be created, the app still starts.
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a folder");
        var logged = new List<string>();
        var n = UpdateStartup.ConfirmStarted(_root, new Version(0, 2, 0), logged.Add);
        Assert.Equal("Updated to 0.2.0.", n.Text);
        Assert.Single(logged);
    }

    [Fact]
    public void Failed_says_what_happened_without_a_link_when_the_rollback_finished()
    {
        var n = UpdateStartup.Failed(_root, "0.2.0", new Version(0, 1, 0));
        Assert.Equal("The update to 0.2.0 didn't start, so Quartermaster went back to 0.1.0.", n.Text);
        Assert.Null(n.Link);
    }

    [Fact]
    public void Failed_points_at_the_release_page_when_the_swap_journal_is_left()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, UpdateStartup.JournalName), "{}");
        var n = UpdateStartup.Failed(_root, "0.2.0", new Version(0, 1, 0));
        Assert.Equal("The update to 0.2.0 didn't start, so Quartermaster went back to 0.1.0. Some files may need a fresh download from the release page.", n.Text);
        Assert.Equal("https://github.com/EmojiPlay88/Quartermaster/releases/tag/quartermaster-v0.2.0", n.Link!.ToString());
    }

    [Theory]
    [InlineData("0.2.0", "https://github.com/EmojiPlay88/Quartermaster/releases/tag/quartermaster-v0.2.0")]
    [InlineData("../x", "https://github.com/EmojiPlay88/Quartermaster/releases")]
    [InlineData("1.2.3.4", "https://github.com/EmojiPlay88/Quartermaster/releases")]
    public void ReleasePage_only_builds_a_tag_link_from_a_real_version(string text, string expected) =>
        Assert.Equal(expected, UpdateStartup.ReleasePage(text).ToString());
}
