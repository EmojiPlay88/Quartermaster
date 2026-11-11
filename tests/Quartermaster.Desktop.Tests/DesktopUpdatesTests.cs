using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Updates;
using Quartermaster.Render;
using Quartermaster.Render.Update;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5a task 5: the update screens on the test feed only. Headless, temp roots; every feed is a temp <see cref="FolderFeed"/> (or a
/// fake) handed to <see cref="DesktopUpdates"/> directly, never through the process environment, so nothing reaches GitHub.
/// </summary>
public sealed class DesktopUpdatesTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-desk-upd-" + Guid.NewGuid().ToString("N"));
    readonly string _layout;
    readonly string _feedDir;
    readonly string _updatesRoot;
    readonly FakeDialogs _dialogs = new();
    readonly List<Window> _windows = new();
    readonly List<Uri> _pages = new();

    public DesktopUpdatesTests()
    {
        _layout = Path.Combine(_tmp, "layout");
        _feedDir = Path.Combine(_tmp, "feed");
        _updatesRoot = Path.Combine(_layout, DesktopUpdates.FolderName);
        Directory.CreateDirectory(_layout);
        Directory.CreateDirectory(_feedDir);
    }

    public void Dispose()
    {
        foreach (var w in _windows.AsEnumerable().Reverse())
        {
            try
            {
                if (w is MainWindow m) m.ForceClose(); else w.Close();
            }
            catch (InvalidOperationException) { }
        }
        Dispatcher.UIThread.RunJobs();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void WaitFor(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    SettingsStore Store(bool checkOnStart = true)
    {
        var s = new SettingsStore(_layout);
        s.Load(null);
        s.Update(x => x with { CheckUpdatesOnStart = checkOnStart });
        return s;
    }

    DesktopUpdates Updates(IUpdateFeed? feed = null, Version? current = null)
    {
        var u = new DesktopUpdates(feed ?? new FolderFeed(_feedDir), _updatesRoot) { OpenPage = (_, uri) => _pages.Add(uri) };
        if (current != null) u.Current = current;
        return u;
    }

    LauncherWindow Launcher(DesktopUpdates updates, SettingsStore? store = null)
    {
        var w = new LauncherWindow(store ?? Store(), _layout, _dialogs, null, updates);
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        WaitFor(() => updates.CheckTask.IsCompleted);
        return w;
    }

    const string Notes = "TEST release notes for the update bar screenshot.\n\n- TEST: an invented change\n- TEST: another invented change";

    /// <summary>As WinForms LauncherShots.WriteTestFeed: release.json in the GitHub API shape plus the zip and its .sha256 (TEST content).
    /// Without <paramref name="zip"/> the zip is a dummy text file, not a Quartermaster build.</summary>
    void WriteTestFeed(string version, byte[]? zip = null, string body = Notes)
    {
        var zipName = $"Quartermaster-{version}-win-x64.zip";
        zip ??= System.Text.Encoding.UTF8.GetBytes("TEST dummy update asset - not a Quartermaster build");
        File.WriteAllBytes(Path.Combine(_feedDir, zipName), zip);
        File.WriteAllText(Path.Combine(_feedDir, zipName + ".sha256"), Convert.ToHexStringLower(SHA256.HashData(zip)) + "  " + zipName + "\n");
        var tag = "quartermaster-v" + version;
        var download = $"https://github.com/EmojiPlay88/Quartermaster/releases/download/{tag}/";
        var release = new Dictionary<string, object>
        {
            ["tag_name"] = tag,
            ["name"] = "TEST " + version,
            ["draft"] = false,
            ["prerelease"] = false,
            ["html_url"] = "https://github.com/EmojiPlay88/Quartermaster/releases/tag/" + tag,
            ["body"] = body,
            ["assets"] = new object[]
            {
                new Dictionary<string, object> { ["name"] = zipName, ["size"] = zip.Length, ["browser_download_url"] = download + zipName },
                new Dictionary<string, object> { ["name"] = zipName + ".sha256", ["size"] = 64 + 2 + zipName.Length + 1, ["browser_download_url"] = download + zipName + ".sha256" },
            },
        };
        File.WriteAllText(Path.Combine(_feedDir, "release.json"), JsonSerializer.Serialize(release));
    }

    /// <summary>A zip the installer stages: the "app exe" is a copy of Quartermaster.Render.dll, whose FileVersion is the solution version.</summary>
    static byte[] StageableZip()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
            Add(UpdateInstaller.AppExe, File.ReadAllBytes(typeof(AppVersion).Assembly.Location));
            Add("readme.txt", "TEST"u8.ToArray());
            Add(UpdateInstaller.ManifestName, "Quartermaster.App.exe\nreadme.txt\nquartermaster-files.txt\n"u8.ToArray());
        }
        return ms.ToArray();
    }

    static UpdateInfo Info(string notes) => new(new Version(9, 9, 9), "quartermaster-v9.9.9", notes, new Uri("a.zip", UriKind.Relative),
        new Uri("a.zip.sha256", UriKind.Relative), 10, new Uri("https://github.com/EmojiPlay88/Quartermaster/releases/tag/quartermaster-v9.9.9"));

    string? StateText() => File.Exists(Path.Combine(_updatesRoot, "state.json")) ? File.ReadAllText(Path.Combine(_updatesRoot, "state.json")) : null;

    sealed class BrokenFeed : IUpdateFeed
    {
        public Task<string?> LatestJson(CancellationToken ct) => throw new IOException("TEST broken feed");
        public Task<Stream> Open(Uri asset, CancellationToken ct) => throw new IOException("TEST broken feed");
    }

    /// <summary>Serves the release from a folder, but the zip download waits until it is cancelled.</summary>
    sealed class StallingFeed(FolderFeed inner) : IUpdateFeed
    {
        public Task<string?> LatestJson(CancellationToken ct) => inner.LatestJson(ct);

        public async Task<Stream> Open(Uri asset, CancellationToken ct)
        {
            if (asset.OriginalString.EndsWith(".zip", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, ct);
            return await inner.Open(asset, ct);
        }
    }

    // ---------- tests ----------

    [AvaloniaFact]
    public void No_feed_means_no_update_ui_and_no_check()
    {
        // Without the feed variable the Desktop app has no feed at all (never GitHub). The environment is read through a lookup, so the
        // test does not depend on the machine's own QUARTERMASTER_UPDATE_FEED.
        var asked = new List<string>();
        var fromEnv = DesktopUpdates.FromEnvironment(_layout, n => { asked.Add(n); return null; });
        Assert.Equal(new[] { UpdateFeeds.EnvVar }, asked);
        Assert.Null(fromEnv.Feed);
        Assert.Equal(Path.Combine(_layout, "Updates-desktop"), fromEnv.UpdatesRoot);
        Assert.Equal("Installing updates comes with the packaged release (stage 6).", DesktopUpdates.InstallLaterText);

        WriteTestFeed("9.9.9");   // a release is there, but nothing may look
        var w = new LauncherWindow(Store(), _layout, _dialogs, null, fromEnv);
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.UpdateBarHost.Children);
        Assert.Empty(w.FooterLinks.Children);
        Assert.False(fromEnv.CheckedThisRun);
        Assert.Null(fromEnv.Offered);
        Assert.False(Directory.Exists(_updatesRoot));
        Assert.Null(fromEnv.Bar);

        // In a game window from the loop: Help > Check for updates… is there but hidden.
        var g = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, _layout) { Updates = fromEnv };
        _windows.Add(g);
        g.LauncherAvailable = true;
        g.Show();
        Assert.Equal(new[] { "Interface &Tour", "Check for &updates…", "&About" }, g.MenuLabels()["&Help"]);
        Assert.False(g.FindMenuItem("Check for &updates…")!.IsVisible);
    }

    [AvaloniaFact]
    public void Feed_offer_shows_bar_text_and_actions()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        var w = Launcher(updates);
        var bar = Assert.IsType<UpdateBar>(Assert.Single(w.UpdateBarHost.Children));
        Assert.Same(updates.Bar, bar);
        Assert.True(bar.IsVisible);
        Assert.Equal("Quartermaster 9.9.9 is available.", bar.Message);
        Assert.Equal(new[] { "What's new", "Update now", "Later", "Skip this version" }, bar.Actions);
        Assert.Equal(Avalonia.Media.Color.Parse("#FFF4C8"), ((Avalonia.Media.ISolidColorBrush)bar.Background!).Color);
        Assert.Equal(new Avalonia.Thickness(0, 0, 0, 1), bar.BorderThickness);
        Assert.Equal("Check for updates", LinkText(Assert.Single(w.FooterLinks.Children)));
        Assert.True(updates.CheckedThisRun);
        Assert.Equal(new Version(9, 9, 9), updates.Offered!.Version);
        Assert.NotNull(updates.LastCheckUtc);
        Assert.Contains("lastCheckUtc", StateText());

        // The bar's Show/Clear contract.
        bar.Show("TEST", ("One", () => { }));
        Assert.Equal(new[] { "One" }, bar.Actions);
        bar.SetActionEnabled("One", false);
        Assert.False(bar.ActionButton("One")!.IsEnabled);
        bar.Clear();
        Assert.False(bar.IsVisible);
        Assert.Empty(bar.Actions);
        bar.Show("");
        Assert.False(bar.IsVisible);

        // After Back to launcher the next launcher shows the offer again without a second check.
        var second = Launcher(updates);
        Assert.Equal("Quartermaster 9.9.9 is available.", ((UpdateBar)second.UpdateBarHost.Children[0]).Message);
    }

    static string LinkText(Control c) => (string)((TextBlock)((Button)c).Content!).Text!;

    [AvaloniaFact]
    public void Skip_this_version_persists_in_desktop_state_not_settings()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        var w = Launcher(updates);
        Click(updates.Bar!.ActionButton("Skip this version")!);
        Assert.False(updates.Bar.IsVisible);
        Assert.Equal("9.9.9", updates.SkippedVersion);
        Assert.Null(updates.Offered);
        Assert.Contains("9.9.9", StateText());
        // The shared settings.json is not touched (its SkippedVersion stays WinForms-only).
        var settings = new SettingsStore(_layout);
        settings.Load(null);
        Assert.Null(settings.Current.SkippedVersion);
        Assert.Null(settings.Current.LastUpdateCheckUtc);

        // A new run reads it back: the automatic check finds the skipped version and stays quiet.
        var next = Updates();
        Assert.Equal("9.9.9", next.SkippedVersion);
        next.LastCheckUtc = null;
        var w2 = Launcher(next);
        Assert.True(next.CheckedThisRun);
        Assert.False(next.Bar!.IsVisible);
        Assert.Empty(_dialogs.Questions);

        // The manual check asks; Yes clears the skip and offers it.
        _dialogs.Answer = true;
        Click((Button)w2.FooterLinks.Children[0]);
        WaitFor(() => next.CheckTask.IsCompleted);
        Assert.Equal(("Quartermaster 9.9.9 is available, but you chose to skip it. Offer it again?", "Quartermaster"), Assert.Single(_dialogs.Questions));
        Assert.Null(next.SkippedVersion);
        Assert.True(next.Bar.IsVisible);
        Assert.Equal("Quartermaster 9.9.9 is available.", next.Bar.Message);
        Assert.Null(Updates().SkippedVersion);
        Assert.Same(updates.Offered, null);
        Assert.True(w.IsVisible);
    }

    [AvaloniaFact]
    public void Later_hides_bar_for_this_run()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        Launcher(updates);
        Click(updates.Bar!.ActionButton("Later")!);
        Assert.True(updates.Later);
        Assert.False(updates.Bar.IsVisible);
        Assert.Null(updates.SkippedVersion);
        Assert.NotNull(updates.Offered);

        // The next launcher this run stays quiet.
        var second = Launcher(updates);
        Assert.False(updates.Bar!.IsVisible);

        // A manual check offers it again.
        Click((Button)second.FooterLinks.Children[0]);
        WaitFor(() => updates.CheckTask.IsCompleted);
        Assert.False(updates.Later);
        Assert.True(updates.Bar.IsVisible);
    }

    [AvaloniaFact]
    public void Whats_new_shows_notes_or_placeholder()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        var w = Launcher(updates);
        Click(updates.Bar!.ActionButton("What's new")!);
        WaitFor(() => updates.WhatsNew is { IsVisible: true });
        var dlg = updates.WhatsNew!;
        Assert.Same(w, dlg.Owner);
        Assert.Equal("What's new in Quartermaster 9.9.9", dlg.Title);
        Assert.Equal(Notes.ReplaceLineEndings(), dlg.NotesBox.Text);
        Assert.True(dlg.NotesBox.IsReadOnly);
        Assert.Equal(new Avalonia.Media.FontFamily(Theme.ClassicFonts.Mono), dlg.NotesBox.FontFamily);
        Assert.Equal("Open the release page", LinkText(dlg.PageLink));
        Assert.Equal("Close", dlg.CloseButton.Content);
        Click(dlg.PageLink);
        Assert.Equal(new Uri("https://github.com/EmojiPlay88/Quartermaster/releases/tag/quartermaster-v9.9.9"), Assert.Single(_pages));
        Click(dlg.CloseButton);
        WaitFor(() => !dlg.IsVisible);

        var empty = new WhatsNewWindow(Info(" \r\n "), (_, _) => { });
        _windows.Add(empty);
        empty.Show();
        Assert.Equal("(This release has no notes.)", empty.NotesBox.Text);
        var crlf = new WhatsNewWindow(Info("a\r\nb\rc"), (_, _) => { });
        _windows.Add(crlf);
        Assert.Equal("a\r\nb\rc".ReplaceLineEndings(), crlf.NotesBox.Text);
    }

    [AvaloniaFact]
    public void Update_now_downloads_then_says_install_later_and_starts_nothing()
    {
        // A release of this build's version (the stand-in exe carries its FileVersion), offered to an older "current".
        var ver = AppVersion.Text;
        WriteTestFeed(ver, StageableZip());
        var updates = Updates(current: new Version(0, 0, 1));
        Launcher(updates);
        Assert.Equal($"Quartermaster {ver} is available.", updates.Bar!.Message);
        Click(updates.Bar.ActionButton("Update now")!);
        WaitFor(() => updates.Updating != null);
        var dlg = updates.Updating!;
        Assert.Equal($"Updating to Quartermaster {ver}", dlg.Title);
        WaitFor(() => updates.UpdateNowTask.IsCompleted);
        Assert.False(dlg.IsVisible);
        Assert.Null(dlg.Error);
        var staged = Assert.IsType<StagedUpdate>(dlg.Staged);
        Assert.True(File.Exists(Path.Combine(staged.StagedDir, UpdateInstaller.AppExe)));
        Assert.StartsWith(Path.GetFullPath(_updatesRoot), staged.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.0, dlg.ProgressFraction);
        Assert.Equal(new[] { ("Installing updates comes with the packaged release (stage 6).", "Quartermaster") }, _dialogs.Infos);
        Assert.Empty(_dialogs.Warnings);
        Assert.False(Directory.Exists(Path.Combine(staged.Root, "helper")));   // the helper is never copied or started
        Assert.False(File.Exists(UpdateInstaller.MarkerPath(_updatesRoot, new Version(ver))));
    }

    [AvaloniaFact]
    public void Update_now_failure_and_cancel_change_nothing()
    {
        // The dummy zip of the WinForms screenshot feed is not a zip: refused, and the version folder is gone.
        WriteTestFeed("9.9.9");
        var updates = Updates();
        Launcher(updates);
        Click(updates.Bar!.ActionButton("Update now")!);
        WaitFor(() => updates.Updating != null && updates.UpdateNowTask.IsCompleted);
        var w = Assert.Single(_dialogs.Warnings);
        Assert.StartsWith("The update was not installed; nothing was changed.\n\n", w.Message);
        Assert.Equal("Quartermaster", w.Caption);
        Assert.Empty(_dialogs.Infos);
        Assert.False(Directory.Exists(Path.Combine(_updatesRoot, "9.9.9")));

        // Cancel while downloading: "Cancelling…", the window closes by itself, no message.
        _dialogs.Warnings.Clear();
        var stalling = Updates(new StallingFeed(new FolderFeed(_feedDir)));
        stalling.LastCheckUtc = null;   // the first check above was today
        Launcher(stalling);
        Click(stalling.Bar!.ActionButton("Update now")!);
        WaitFor(() => stalling.Updating is { IsVisible: true });
        var dlg = stalling.Updating!;
        Assert.Equal(UpdateOffer.DownloadStatus("Quartermaster-9.9.9-win-x64.zip", new FileInfo(Path.Combine(_feedDir, "Quartermaster-9.9.9-win-x64.zip")).Length), dlg.StatusText);
        Assert.Equal("Cancel", dlg.CancelButton.Content);
        Click(dlg.CancelButton);
        Assert.Equal("Cancelling…", dlg.StatusText);
        Assert.False(dlg.CancelButton.IsEnabled);
        WaitFor(() => stalling.UpdateNowTask.IsCompleted);
        Assert.True(dlg.Cancelled);
        Assert.False(dlg.IsVisible);
        Assert.Empty(_dialogs.Warnings);
        Assert.Empty(_dialogs.Infos);
        Assert.False(Directory.Exists(Path.Combine(_updatesRoot, "9.9.9")));
    }

    [AvaloniaFact]
    public void Manual_check_up_to_date_and_failed_texts()
    {
        // No release in the feed: up to date.
        var updates = Updates();
        var w = Launcher(updates, Store(checkOnStart: false));
        Assert.False(updates.CheckedThisRun);
        var link = (Button)Assert.Single(w.FooterLinks.Children);
        Click(link);
        WaitFor(() => updates.CheckTask.IsCompleted);
        Assert.Equal(new[] { ($"Quartermaster {AppVersion.Text} is up to date.", "Quartermaster") }, _dialogs.Infos);
        Assert.True(link.IsEnabled);
        Assert.NotNull(updates.LastCheckUtc);

        // A broken feed: the plain reason, as a warning; the check time is not written.
        var broken = Updates(new BrokenFeed());
        broken.LastCheckUtc = null;
        var w2 = Launcher(broken, Store(checkOnStart: false));
        Click((Button)w2.FooterLinks.Children[0]);
        WaitFor(() => broken.CheckTask.IsCompleted);
        Assert.Equal(new[] { ("Could not check for updates.\n\nTEST broken feed", "Quartermaster") }, _dialogs.Warnings);
        Assert.Null(broken.LastCheckUtc);
        Assert.False(broken.Bar!.IsVisible);
    }

    [AvaloniaFact]
    public void Help_check_for_updates_in_game_asks_then_goes_back()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        var g = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, _layout) { Width = 1024, Height = 768, Updates = updates };
        _windows.Add(g);
        g.Show();
        var item = g.FindMenuItem("Check for &updates…")!;
        Assert.False(item.IsVisible);   // not started by the launcher loop
        g.LauncherAvailable = true;
        Assert.True(item.IsVisible);
        Assert.Equal(new[] { "Interface &Tour", "Check for &updates…", "&About" }, g.MenuLabels()["&Help"]);

        Assert.True(g.OpenModule(CopyDemo()));
        g.StartSetup(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(g.Session!.Game);

        // No: nothing happens.
        _dialogs.Answer = false;
        g.ClickMenu("Check for &updates…");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("Close the game and use the launcher to update.\n\nGo back to the launcher now (File → Back to launcher)?", "Check for updates"),
            Assert.Single(_dialogs.Questions));
        Assert.False(updates.CheckOnNextLauncher);
        Assert.True(g.IsVisible);

        // Yes: back to the launcher, which checks at once (a manual check: no dialog for an offer, the bar shows).
        _dialogs.Answer = true;
        g.ClickMenu("Check for &updates…");
        WaitFor(() => !g.IsVisible);
        Assert.True(g.ReturnToLauncher);
        Assert.True(updates.CheckOnNextLauncher);
        var store = Store(checkOnStart: false);   // the manual check runs whatever the setting says
        var w = Launcher(updates, store);
        Assert.False(updates.CheckOnNextLauncher);
        Assert.True(updates.CheckedThisRun);
        Assert.Equal("Quartermaster 9.9.9 is available.", updates.Bar!.Message);
        Assert.True(w.IsVisible);
    }

    /// <summary>Final review I1: with unsaved changes, Yes (go back) then Yes (discard) closes after the question; the launcher still checks.</summary>
    [AvaloniaFact]
    public void Help_check_for_updates_with_unsaved_changes_still_checks()
    {
        WriteTestFeed("9.9.9");
        var updates = Updates();
        var g = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, _layout) { Width = 1024, Height = 768, Updates = updates };
        _windows.Add(g);
        g.LauncherAvailable = true;
        g.Show();
        Assert.True(g.OpenModule(CopyDemo()));
        g.StartSetup(null);
        Dispatcher.UIThread.RunJobs();
        var p = g.Session!.Game!.State.Pieces.Values.First(x => x.MapId == g.Session.Model.Maps[0].Id);
        g.Actions!.Move(new[] { new Quartermaster.Core.MovePieces(new[] { p.Id }, p.MapId, new Quartermaster.Vassal.PointD(p.Pos.X + 40, p.Pos.Y + 40), null) });
        Dispatcher.UIThread.RunJobs();
        Assert.True(g.HasUnsavedChanges);

        _dialogs.Answer = true;   // Yes, go back; Yes, discard
        g.ClickMenu("Check for &updates…");
        WaitFor(() => !g.IsVisible);
        Assert.Equal(2, _dialogs.Questions.Count);
        Assert.Equal("The current game has unsaved changes. Discard them?", _dialogs.Questions[1].Message);
        Assert.True(g.ReturnToLauncher);
        Assert.True(updates.CheckOnNextLauncher);
        Launcher(updates, Store(checkOnStart: false));
        Assert.True(updates.CheckedThisRun);
        Assert.Equal("Quartermaster 9.9.9 is available.", updates.Bar!.Message);
    }

    string CopyDemo()
    {
        var mods = Path.Combine(_tmp, "mods");
        Directory.CreateDirectory(mods);
        var p = Path.Combine(mods, "Riverland-Demo.vmod");
        if (!File.Exists(p)) File.Copy(Demo("Riverland-Demo.vmod"), p);
        return p;
    }

    [AvaloniaFact]
    public void Auto_check_respects_setting_and_24h()
    {
        WriteTestFeed("9.9.9");

        // Switched off: no check.
        var off = Updates();
        Launcher(off, Store(checkOnStart: false));
        Assert.False(off.CheckedThisRun);
        Assert.False(off.Bar!.IsVisible);
        Assert.Null(off.LastCheckUtc);

        // Checked an hour ago: not due.
        var recent = Updates();
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        recent.LastCheckUtc = hourAgo;
        Launcher(recent);
        Assert.False(recent.CheckedThisRun);
        Assert.False(recent.Bar!.IsVisible);
        Assert.Equal(hourAgo, Updates().LastCheckUtc);   // persisted in state.json

        // A day ago: due; the check time is written.
        var due = Updates();
        due.LastCheckUtc = DateTime.UtcNow.AddHours(-25);
        Launcher(due);
        Assert.True(due.CheckedThisRun);
        Assert.True(due.Bar!.IsVisible);
        Assert.True(due.LastCheckUtc > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(due.LastCheckUtc, Updates().LastCheckUtc);

        // Once per run: the next launcher does not check again (the offer it found is shown).
        File.Delete(Path.Combine(_feedDir, "release.json"));
        due.LastCheckUtc = null;
        Launcher(due);
        Assert.True(due.Bar!.IsVisible);
        Assert.Null(due.LastCheckUtc);
    }
}
