using System.Globalization;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class LauncherModelTests
{
    static ModuleRow Row(string path, string title = "T", bool missing = false, string? sha = "abc")
        => new(new ModuleEntry(path, sha, 1, DateTime.UtcNow, title, "1.0", null), missing);

    [Fact]
    public void Missing_row_texts()
    {
        var r = Row(Path.Combine("dir", "x.vmod"), "", missing: true);
        Assert.Equal("x.vmod (file not found)", LauncherModel.FileText(r));
        Assert.Equal("x.vmod", LauncherModel.TitleText(r));
        Assert.Equal("Name", LauncherModel.TitleText(Row(Path.Combine("dir", "x.vmod"), "Name")));
        Assert.Equal("x.vmod", LauncherModel.FileText(Row(Path.Combine("dir", "x.vmod"))));
        Assert.Equal("(file not found)", LauncherModel.NotFound);
    }

    [Fact]
    public void Last_played_is_local_time_or_empty()
    {
        var t = new DateTimeOffset(2026, 3, 4, 22, 15, 0, TimeSpan.Zero);
        var e = Row("a.vmod").Entry with { LastPlayed = t };
        Assert.Equal(t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), LauncherModel.LastPlayedText(e));
        Assert.Equal("", LauncherModel.LastPlayedText(Row("a.vmod").Entry));
    }

    [Fact]
    public void Initial_selection_prefers_just_played_then_last_module_then_first_present()
    {
        var rows = new[] { Row("a.vmod", missing: true), Row("b.vmod"), Row("c.vmod") };
        var s = new Settings { ReopenLastModule = true, LastModulePath = "C.VMOD" };
        Assert.Equal(1, LauncherModel.InitialSelection(rows, "B.vmod", s));
        Assert.Equal(2, LauncherModel.InitialSelection(rows, null, s));
        Assert.Equal(1, LauncherModel.InitialSelection(rows, null, s with { LastModulePath = "zzz" }));
        Assert.Equal(-1, LauncherModel.InitialSelection([], "b.vmod", s));
    }

    [Fact]
    public void Initial_selection_ignores_last_module_when_reopen_off()
    {
        var rows = new[] { Row("a.vmod"), Row("b.vmod") };
        var s = new Settings { ReopenLastModule = false, LastModulePath = "b.vmod" };
        Assert.Equal(-1, LauncherModel.InitialSelection(rows, null, s));
        Assert.Equal(1, LauncherModel.InitialSelection(rows, "b.vmod", s));
    }

    [Fact]
    public void Buttons_disable_play_on_missing_and_not_ready()
    {
        Assert.Equal(new LauncherButtons(false, false, null), LauncherModel.Buttons(null, true));
        var missing = LauncherModel.Buttons(Row("a.vmod", missing: true), true);
        Assert.False(missing.PlayOffline);
        Assert.True(missing.Remove);
        Assert.Equal(LauncherModel.MissingTooltip, missing.PlayOfflineTip);
        var notReady = LauncherModel.Buttons(Row("a.vmod"), false);
        Assert.False(notReady.PlayOffline);
        Assert.Equal(LauncherModel.NotReadyText, notReady.PlayOfflineTip);
        var ok = LauncherModel.Buttons(Row("a.vmod"), true);
        Assert.True(ok.PlayOffline);
        Assert.True(ok.Remove);
        Assert.Null(ok.PlayOfflineTip);
    }

    [Fact]
    public void Rules_label_none_and_named()
    {
        Assert.Equal("Rules pack: (none)", LauncherModel.RulesLabel(null));
        Assert.Equal("Rules pack: (none)", LauncherModel.RulesLabel(""));
        Assert.Equal("Rules pack: pack.qrules", LauncherModel.RulesLabel(Path.Combine("d", "pack.qrules")));
        Assert.Equal("Rules pack: pack", LauncherModel.RulesLabel(Path.Combine("d", "pack") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Texts_are_verbatim()
    {
        Assert.Equal("This module's file was not found. Remove it from the list or add it again.", LauncherModel.MissingModuleText);
        Assert.Equal("This module's file was not found. Remove it or add it again.", LauncherModel.MissingTooltip);
        Assert.Equal("This module is still being checked; try again in a moment.", LauncherModel.NotReadyText);
        Assert.Equal("Quartermaster {0}", LauncherModel.FooterFormat);
    }

    [Fact]
    public void Continue_text_matches_winforms()
    {
        var at = new DateTimeOffset(2026, 3, 4, 22, 15, 0, TimeSpan.Zero);
        var when = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        Assert.Equal("Continue: (no autosave yet)", PlayOfflineModel.ContinueText(null));
        Assert.Equal($"Continue: autosave {when} (turn 3, Movement)", PlayOfflineModel.ContinueText(new ContinueInfo("p", at, 3, "Movement")));
        Assert.Equal($"Continue: autosave {when} (turn 3)", PlayOfflineModel.ContinueText(new ContinueInfo("p", at, 3, null)));
        Assert.Equal($"Continue: autosave {when} (Movement)", PlayOfflineModel.ContinueText(new ContinueInfo("p", at, 0, "Movement")));
        Assert.Equal($"Continue: autosave {when}", PlayOfflineModel.ContinueText(new ContinueInfo("p", at, 0, null)));
    }

    [Fact]
    public void Title_and_finish()
    {
        var r = Row(Path.Combine("d", "x.vmod"), "");
        Assert.Equal("Play offline – x.vmod", PlayOfflineModel.Title(r));
        Assert.Equal("Play offline – Name", PlayOfflineModel.Title(Row("x.vmod", "Name")));
        var req = PlayOfflineModel.Finish(r, "", LaunchKind.Continue, null, "s.qsave");
        Assert.Equal(new LaunchRequest(r.Entry.Path, null, LaunchKind.Continue, null, "s.qsave"), req);
        Assert.Equal("r.qrules", PlayOfflineModel.Finish(r, "r.qrules", LaunchKind.NewGame, null, null).RulesPath);
        Assert.Equal("That saved game's file was not found.", PlayOfflineModel.SaveMissingText);
    }
}
