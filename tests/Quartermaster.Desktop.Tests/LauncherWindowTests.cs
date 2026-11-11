using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5a task 2: the launcher and Play offline windows, headless, with a temp layout root and a temp module copy.</summary>
public sealed class LauncherWindowTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-launcher-" + Guid.NewGuid().ToString("N"));
    readonly string _layout;
    readonly string _module;
    readonly FakeDialogs _dialogs = new();
    readonly List<Window> _windows = new();

    public LauncherWindowTests()
    {
        _layout = Path.Combine(_tmp, "layout");
        Directory.CreateDirectory(_layout);
        Directory.CreateDirectory(Path.Combine(_tmp, "mods"));
        _module = Path.Combine(_tmp, "mods", "Riverland-Demo.vmod");
        File.Copy(Demo("Riverland-Demo.vmod"), _module);
    }

    public void Dispose()
    {
        foreach (var w in _windows) { try { w.Close(); } catch (InvalidOperationException) { } }
        Dispatcher.UIThread.RunJobs();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

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

    SettingsStore Store()
    {
        var s = new SettingsStore(_layout);
        s.Load(null);
        return s;
    }

    string CopyModule(string name)
    {
        var p = Path.Combine(_tmp, "mods", name);
        File.Copy(_module, p);
        return p;
    }

    LauncherWindow Launcher(SettingsStore store, string? justPlayed = null, bool waitForRefresh = true)
    {
        var w = new LauncherWindow(store, _layout, _dialogs, justPlayed);
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        if (waitForRefresh) WaitFor(() => w.RefreshTask.IsCompleted);
        return w;
    }

    // ---------- launcher ----------

    [AvaloniaFact]
    public void Lists_added_module_with_title_version_and_last_played()
    {
        var store = Store();
        var lib = new ModuleLibrary(store);
        lib.Add(_module);
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero);
        lib.MarkPlayed(Path.GetFullPath(_module), when);

        var w = Launcher(store);
        Assert.Equal("Quartermaster", w.Title);
        Assert.Equal((680d, 400d), (w.Width, w.Height));
        Assert.Equal((560d, 320d), (w.MinWidth, w.MinHeight));
        Assert.Equal(string.Format(LauncherModel.FooterFormat, AppVersion.Text), w.FooterText);
        Assert.Equal(new[] { "Module", "Version", "File", "Last played" }, w.ColumnHeaders);
        var info = ModuleInfo.Read(_module);
        Assert.False(string.IsNullOrWhiteSpace(info.Title));
        var cells = Assert.Single(w.Cells);
        Assert.Equal(new[] { info.Title, info.Version, "Riverland-Demo.vmod", when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }, cells);
        // Hashed by the refresh; with "Select the last module" on (the default) the first present module is selected.
        Assert.False(string.IsNullOrEmpty(w.Library.Rows[0].Entry.Sha256));
        Assert.Equal(0, w.Modules.SelectedIndex);
        Assert.True(w.PlayButton.IsEnabled);
        Assert.True(w.PlayButton.IsDefault);
        Assert.Equal("Play offline ▶", w.PlayButton.Content);
        Assert.Equal("Add module…", w.AddButton.Content);
        Assert.Equal("Remove", w.RemoveButton.Content);
        Assert.Equal("", w.ProgressText);
    }

    [AvaloniaFact]
    public void Nothing_selected_when_select_last_is_off()
    {
        var store = Store();
        store.Update(s => s with { ReopenLastModule = false });
        new ModuleLibrary(store).Add(_module);
        var w = Launcher(store);
        Assert.Equal(-1, w.Modules.SelectedIndex);
        Assert.False(w.PlayButton.IsEnabled);
        Assert.False(w.RemoveButton.IsEnabled);
        Assert.Equal("Rules pack: (none)", w.RulesText);
    }

    [AvaloniaFact]
    public void Just_played_module_is_selected()
    {
        var store = Store();
        store.Update(s => s with { ReopenLastModule = false });
        var second = CopyModule("Second.vmod");
        var lib = new ModuleLibrary(store);
        lib.Add(_module);
        lib.Add(second);
        var w = Launcher(store, justPlayed: second);
        Assert.Equal(Path.GetFullPath(second), w.SelectedRow!.Entry.Path, ignoreCase: true);
    }

    [AvaloniaFact]
    public void Play_offline_waits_while_module_is_checked()
    {
        var store = Store();
        new ModuleLibrary(store).Add(_module);
        using var gate = new ManualResetEventSlim(false);
        var w = new LauncherWindow(store, _layout, _dialogs, null);
        _windows.Add(w);
        w.Library.Hasher = p => { gate.Wait(TimeSpan.FromSeconds(20)); return VmodArchive.ComputeSha256(p); };
        w.Show();
        WaitFor(() => w.ProgressText.StartsWith("Checking modules…", StringComparison.Ordinal));
        Assert.False(w.PlayButton.IsEnabled);
        Assert.Equal(LauncherModel.NotReadyText, ToolTip.GetTip(w.PlayButton));
        Assert.True(ToolTip.GetShowOnDisabled(w.PlayButton));
        gate.Set();
        WaitFor(() => w.RefreshTask.IsCompleted);
        Assert.Equal("", w.ProgressText);
        Assert.True(w.PlayButton.IsEnabled);
        Assert.Null(ToolTip.GetTip(w.PlayButton));
    }

    [AvaloniaFact]
    public void Add_module_uses_multi_select_and_adds_each()
    {
        var store = Store();
        var a = CopyModule("A.vmod");
        var b = CopyModule("B.vmod");
        var w = Launcher(store);
        Assert.Empty(w.Cells);
        _dialogs.OpenFileLists.Enqueue(new[] { a, b });
        Click(w.AddButton);
        WaitFor(() => w.Cells.Count == 2 && w.RefreshTask.IsCompleted);
        var picker = Assert.Single(_dialogs.MultiPickers);
        Assert.Equal("Add module", picker.Title);
        Assert.Equal(new[] { "VASSAL modules (*.vmod)", "All files (*.*)" }, picker.Filters.Select(f => f.Name));
        Assert.Equal(new[] { "*.vmod" }, picker.Filters[0].Patterns);
        Assert.Equal(2, w.Library.Rows.Count);
        Assert.All(w.Library.Rows, r => Assert.False(string.IsNullOrEmpty(r.Entry.Sha256)));   // the added modules were hashed
        Assert.Equal(Path.GetFullPath(b), w.SelectedRow!.Entry.Path, ignoreCase: true);       // the last one added is selected
    }

    [AvaloniaFact]
    public void Remove_and_delete_key_remove_selected()
    {
        var store = Store();
        var lib = new ModuleLibrary(store);
        lib.Add(_module);
        lib.Add(CopyModule("Second.vmod"));
        var w = Launcher(store);
        Assert.Equal(2, w.Cells.Count);
        w.Modules.SelectedIndex = 0;
        Click(w.RemoveButton);
        Assert.Single(w.Cells);
        Assert.Single(store.Current.Modules);
        w.Modules.SelectedIndex = 0;
        w.Modules.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, Source = w.Modules });
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(w.Cells);
        Assert.Empty(store.Current.Modules);
        Assert.True(File.Exists(_module));   // the file is left alone
        Assert.False(w.RemoveButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Play_offline_on_missing_module_refuses()
    {
        var store = Store();
        var gone = CopyModule("Gone.vmod");
        new ModuleLibrary(store).Add(gone);
        File.Delete(gone);
        var w = Launcher(store);
        w.Modules.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Gone.vmod " + LauncherModel.NotFound, w.Cells[0][2]);
        Assert.False(w.PlayButton.IsEnabled);
        Assert.Equal(LauncherModel.MissingTooltip, ToolTip.GetTip(w.PlayButton));
        Assert.True(w.RemoveButton.IsEnabled);
        // A double-click still reaches Play offline, which says why it refuses (WinForms PlayOffline).
        w.PlayOfflineAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((LauncherModel.MissingModuleText, "Quartermaster"), Assert.Single(_dialogs.Infos));
        Assert.Null(w.Offline);
        Assert.False(w.Result.IsCompleted);
    }

    [AvaloniaFact]
    public void Online_lobby_is_disabled_with_stage_tip()
    {
        var store = Store();
        new ModuleLibrary(store).Add(_module);
        var w = Launcher(store);
        Assert.Equal("Online lobby ▶", w.OnlineButton.Content);
        Assert.False(w.OnlineButton.IsEnabled);
        Assert.Equal("Online play comes with stage 5c.", ToolTip.GetTip(w.OnlineButton));
        Assert.True(ToolTip.GetShowOnDisabled(w.OnlineButton));
    }

    [AvaloniaFact]
    public void Rules_pack_change_and_use_none_persist()
    {
        var store = Store();
        new ModuleLibrary(store).Add(_module);
        var pack = Path.Combine(_tmp, "mods", "Riverland-Demo.qrules");
        File.Copy(Demo("Riverland-Demo.qrules"), pack);
        var w = Launcher(store);
        Assert.Equal("Rules pack: (none)", w.RulesText);
        Assert.Equal("change…", w.ChangeLink.Content is TextBlock t ? t.Text : w.ChangeLink.Content);
        Assert.False(w.NoneLink.IsVisible);
        Assert.Equal("Play this module without a rules pack.", ToolTip.GetTip(w.NoneLink));

        _dialogs.OpenFiles.Enqueue(pack);
        Click(w.ChangeLink);
        WaitFor(() => w.RulesText == "Rules pack: Riverland-Demo.qrules");
        Assert.StartsWith("Rules pack for ", _dialogs.PickerTitles.Last(), StringComparison.Ordinal);
        Assert.Equal("Rules packs (*.qrules)", _dialogs.LastOpenFilters![0].Name);
        Assert.True(w.NoneLink.IsVisible);
        w.Close();

        // Reopened: the pack is remembered for the module.
        var reopened = Store();
        var lib = new ModuleLibrary(reopened);
        Assert.Equal(pack, lib.RulesPackFor(lib.Rows[0].Entry), ignoreCase: true);
        var w2 = Launcher(reopened);
        Assert.Equal("Rules pack: Riverland-Demo.qrules", w2.RulesText);
        Click(w2.NoneLink);
        Assert.Equal("Rules pack: (none)", w2.RulesText);
        Assert.False(w2.NoneLink.IsVisible);
        w2.Close();

        var again = new ModuleLibrary(Store());
        Assert.Null(again.RulesPackFor(again.Rows[0].Entry));
    }

    [AvaloniaFact]
    public void Closing_launcher_completes_result_null()
    {
        var w = Launcher(Store());
        Assert.False(w.Result.IsCompleted);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.Result.IsCompleted);
        Assert.Null(w.Result.Result);
    }

    [AvaloniaFact]
    public void Update_slot_is_attached_once_open()
    {
        var slot = new RecordingSlot();
        var w = new LauncherWindow(Store(), _layout, _dialogs, null, slot);
        _windows.Add(w);
        Assert.Empty(slot.Attached);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(w, Assert.Single(slot.Attached));
        Assert.NotNull(w.UpdateBarHost);
        Assert.NotNull(w.FooterLinks);
    }

    sealed class RecordingSlot : IUpdateSlot
    {
        public readonly List<LauncherWindow> Attached = new();
        public void Attach(LauncherWindow w) => Attached.Add(w);
    }

    [AvaloniaFact]
    public void Play_offline_back_returns_to_launcher_and_a_choice_completes_it()
    {
        var store = Store();
        new ModuleLibrary(store).Add(_module);
        var w = Launcher(store);
        Click(w.PlayButton);
        WaitFor(() => w.Offline is { IsVisible: true });
        var first = w.Offline!;
        Click(first.BackButton);
        WaitFor(() => w.Offline == null);
        Assert.False(w.Result.IsCompleted);
        Assert.True(w.IsVisible);

        _dialogs.Setup = new SetupChoice(null, Difficulty.Hardcore);
        Click(w.PlayButton);
        WaitFor(() => w.Offline is { IsVisible: true });
        Click(w.Offline!.NewGameButton);
        WaitFor(() => w.Result.IsCompleted, 60);
        var req = w.Result.Result!;
        Assert.Equal(LaunchKind.NewGame, req.Kind);
        Assert.Equal(Path.GetFullPath(_module), req.ModulePath, ignoreCase: true);
        Assert.False(w.IsVisible);
    }

    // ---------- Play offline ----------

    static readonly string FakeSha = new('a', 64);

    ModuleRow Row(string? path = null) =>
        new(new ModuleEntry(Path.GetFullPath(path ?? _module), FakeSha, 1, DateTime.UtcNow, "Riverland Demo", "1.0", null), false);

    PlayOfflineWindow Offline(SettingsStore store, ModuleRow? row = null)
    {
        var w = new PlayOfflineWindow(row ?? Row(), new ModuleLibrary(store), new RecentGames(store, _layout), store, _dialogs, Path.Combine(_tmp, "cache"));
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    string Autosave(string name, int turn, string phase, DateTime writtenUtc)
    {
        var dir = Path.Combine(Autosaver.AutosaveRoot(_layout), FakeSha);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "{\"format\":7,\"snapshot\":{\"pieces\":[],\"turn\":{\"turn\":" + turn + ",\"phase\":\"" + phase + "\"}}}");
        File.SetLastWriteTimeUtc(p, writtenUtc);
        return p;
    }

    [AvaloniaFact]
    public void Continue_line_shows_latest_autosave()
    {
        var store = Store();
        var none = Offline(store);
        Assert.Equal("Play offline – Riverland Demo", none.Title);
        Assert.Equal((520d, 360d), (none.Width, none.Height));
        Assert.Equal("Continue: (no autosave yet)", none.ContinueText);
        Assert.False(none.ContinueButton.IsEnabled);
        Assert.Equal(new[] { "Recent games", "Saved" }, none.ColumnHeaders);
        Assert.Equal("Turn files come with stage 5d.", ToolTip.GetTip(none.OpenTurnButton));
        Assert.False(none.OpenTurnButton.IsEnabled);
        Assert.False(none.OpenSelectedButton.IsEnabled);
        none.Close();

        Autosave("autosave-old.qsave", 2, "Supply", new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
        var newest = Autosave("autosave-new.qsave", 5, "Movement", new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc));
        var w = Offline(store);
        var expected = PlayOfflineModel.ContinueText(new ContinueInfo(newest, new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), 5, "Movement"));
        Assert.Equal(expected, w.ContinueText);
        Assert.EndsWith("(turn 5, Movement)", w.ContinueText, StringComparison.Ordinal);
        Assert.True(w.ContinueButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Continue_returns_continue_request()
    {
        var store = Store();
        var save = Autosave("autosave-1.qsave", 3, "Combat", DateTime.UtcNow);
        var w = Offline(store);
        Click(w.ContinueButton);
        WaitFor(() => w.Result.IsCompleted);
        var req = w.Result.Result!;
        Assert.Equal(LaunchKind.Continue, req.Kind);
        Assert.Equal(save, req.SavePath);
        Assert.Equal(Path.GetFullPath(_module), req.ModulePath);
        Assert.Null(req.RulesPath);
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void New_game_returns_new_game_request_and_stores_difficulty()
    {
        var store = Store();
        store.Update(s => s with { LastDifficulty = Difficulty.FullyAuto });
        _dialogs.Setup = new SetupChoice(null, Difficulty.Hardcore);
        var w = Offline(store);
        Click(w.NewGameButton);
        WaitFor(() => w.Result.IsCompleted, 60);
        Assert.Equal(1, _dialogs.SetupChooserShown);
        Assert.False(_dialogs.LastPreselectNew);
        Assert.Equal(Difficulty.FullyAuto, _dialogs.LastChooserDifficulty);
        var req = w.Result.Result!;
        Assert.Equal(LaunchKind.NewGame, req.Kind);
        Assert.Equal(new NewGameRequest(GameMode.Offline, null, Difficulty.Hardcore, null, null, null), req.NewGame);
        Assert.Null(req.SavePath);
        Assert.Equal(Difficulty.Hardcore, store.Current.LastDifficulty);
        Assert.Equal(Difficulty.Hardcore, Store().Current.LastDifficulty);   // saved
    }

    [AvaloniaFact]
    public void New_game_busy_blocks_closing_and_buttons()
    {
        var store = Store();
        var gate = new TaskCompletionSource<SetupChoice?>();
        _dialogs.SetupGate = gate;
        var w = Offline(store);
        Click(w.NewGameButton);
        WaitFor(() => _dialogs.SetupChooserShown == 1, 60);
        Assert.False(w.NewGameButton.IsEnabled);
        Assert.False(w.OpenSavedButton.IsEnabled);
        Assert.False(w.BackButton.IsEnabled);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsVisible);   // closing is refused while the setup chooser is open
        Assert.False(w.Result.IsCompleted);
        gate.SetResult(null);       // Cancel in the chooser
        WaitFor(() => w.NewGameButton.IsEnabled);
        Assert.True(w.BackButton.IsEnabled);
        Assert.False(w.Result.IsCompleted);
        Click(w.BackButton);
        WaitFor(() => w.Result.IsCompleted);
        Assert.Null(w.Result.Result);
    }

    [AvaloniaFact]
    public void Open_saved_game_starts_in_newest_save_folder()
    {
        var store = Store();
        var dir = Path.Combine(_tmp, "saves");
        Directory.CreateDirectory(dir);
        var save = Path.Combine(dir, "game.qsave");
        File.WriteAllText(save, "{}");
        new RecentGames(store, _layout).Add(FakeSha, save);
        var w = Offline(store);
        Assert.Equal(new[] { "game.qsave", File.GetLastWriteTimeUtc(save).ToLocalTime().ToString("yyyy-MM-dd HH:mm") }, Assert.Single(w.Cells));
        var picked = Path.Combine(dir, "other.qsave");
        _dialogs.OpenFiles.Enqueue(picked);
        Click(w.OpenSavedButton);
        WaitFor(() => w.Result.IsCompleted);
        Assert.Equal(dir, _dialogs.LastOpenFolder);
        Assert.Equal("Open saved game", _dialogs.PickerTitles.Last());
        Assert.Equal("Quartermaster saves (*.qsave)", _dialogs.LastOpenFilters![0].Name);
        Assert.Equal(new LaunchRequest(Path.GetFullPath(_module), null, LaunchKind.OpenSave, null, picked), w.Result.Result);
    }

    [AvaloniaFact]
    public void Open_selected_missing_save_says_not_found()
    {
        var store = Store();
        var gone = Path.Combine(_tmp, "gone.qsave");
        new RecentGames(store, _layout).Add(FakeSha, gone);
        var w = Offline(store);
        Assert.Equal(new[] { "gone.qsave " + LauncherModel.NotFound, "" }, Assert.Single(w.Cells));
        w.Recent.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.OpenSelectedButton.IsEnabled);
        w.OpenSelectedAsync().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((PlayOfflineModel.SaveMissingText, "Quartermaster"), Assert.Single(_dialogs.Infos));
        Assert.False(w.Result.IsCompleted);
    }

    [AvaloniaFact]
    public void Open_selected_present_save_returns_open_request()
    {
        var store = Store();
        var save = Path.Combine(_tmp, "here.qsave");
        File.WriteAllText(save, "{}");
        new RecentGames(store, _layout).Add(FakeSha, save);
        var w = Offline(store);
        w.Recent.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.OpenSelectedButton.IsEnabled);
        Click(w.OpenSelectedButton);
        WaitFor(() => w.Result.IsCompleted);
        Assert.Equal(LaunchKind.OpenSave, w.Result.Result!.Kind);
        Assert.Equal(save, w.Result.Result.SavePath);
    }

    [AvaloniaFact]
    public void Back_returns_null()
    {
        var w = Offline(Store());
        Assert.Equal("Back", w.BackButton.Content);
        Assert.True(w.BackButton.IsCancel);
        Click(w.BackButton);
        WaitFor(() => w.Result.IsCompleted);
        Assert.Null(w.Result.Result);
        Assert.False(w.IsVisible);
    }
}
