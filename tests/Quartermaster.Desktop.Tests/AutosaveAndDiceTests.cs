using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4b task 6: File > Open Autosave, physical dice, the remembered difficulty and the outliner kept between runs.
/// Headless, fake dialogs, temp layout root (never the real app data).</summary>
[Collection("MacFlag")]
public sealed class AutosaveAndDiceTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-autosave-dice-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public AutosaveAndDiceTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static bool WaitFor(Func<bool> condition, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Pump();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Pump();
        return condition();
    }

    MainWindow NewWindow(FakeDialogs? dialogs = null)
    {
        var w = new MainWindow(dialogs ?? _dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    /// <summary>The minimal module with a die "Weather" of two faces.</summary>
    string DiceModule()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.SpecialDiceButton name=\"Dice\">" +
            "<VASSAL.build.module.SpecialDie name=\"Weather\"><VASSAL.build.module.SpecialDieFace text=\"Sun\"/><VASSAL.build.module.SpecialDieFace text=\"Rain\"/></VASSAL.build.module.SpecialDie>" +
            "</VASSAL.build.module.SpecialDiceButton></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml);
        _fixtures.Add(fx);
        return fx.Build();
    }

    MainWindow Game(FakeDialogs? dialogs = null)
    {
        var w = NewWindow(dialogs);
        Assert.True(w.OpenModule(DiceModule()));
        w.StartSetup(null);
        Pump();
        return w;
    }

    static void Save(MainWindow w, Func<Settings, Settings> change)
    {
        w.SettingsStore.Save(change(w.SettingsStore.Current));
        Pump();
    }

    static List<RollDice> Rolls(MainWindow w) => w.Session!.Game!.Log.OfType<RollDice>().ToList();

    // ---------- Open Autosave ----------

    [AvaloniaFact]
    public void Open_autosave_starts_in_module_folder()
    {
        var w = Game();
        var root = Autosaver.AutosaveRoot(LayoutRoot);
        var mine = Path.Combine(root, w.Session!.Archive.Sha256);

        w.ClickMenu("Open Auto&save...");
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Count == 1));
        Assert.Equal("Open Autosave", _dialogs.PickerTitles[0]);
        Assert.Null(_dialogs.LastOpenFolder);   // nothing saved yet

        Directory.CreateDirectory(root);
        w.ClickMenu("Open Auto&save...");
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Count == 2));
        Assert.Equal(root, _dialogs.LastOpenFolder);

        Directory.CreateDirectory(mine);
        w.ClickMenu("Open Auto&save...");
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Count == 3));
        Assert.Equal(mine, _dialogs.LastOpenFolder);

        // Open Game itself is unchanged: its own title, no start folder.
        w.ClickMenu("Open &Game...");
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Count == 4));
        Assert.Equal("Open Game", _dialogs.PickerTitles[3]);
        Assert.Null(_dialogs.LastOpenFolder);
    }

    [AvaloniaFact]
    public void Open_autosave_is_refused_without_a_module()
    {
        var w = NewWindow();
        var item = w.FindMenuItem("Open Auto&save...");
        Assert.NotNull(item);
        Assert.False(item!.IsEffectivelyEnabled);
        w.ClickMenu("Open Auto&save...");
        Pump();
        Assert.Empty(_dialogs.PickerTitles);
    }

    [AvaloniaFact]
    public void Opened_autosave_is_not_save_target()
    {
        var w = Game();
        var dir = Path.Combine(Autosaver.AutosaveRoot(LayoutRoot), w.Session!.Archive.Sha256);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "auto-1.qsave");
        w.Session!.SaveNative(file);

        _dialogs.OpenFiles.Enqueue(file);
        w.ClickMenu("Open Auto&save...");
        Assert.True(WaitFor(() => (w.Title ?? "").Contains("(recovered autosave)")));
        Assert.Null(w.SavePath);

        // Save has no file to write to: it asks (Save As); the fake cancels.
        w.ClickMenu("&Save Game");
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Contains("Save Game")));
        Assert.Null(w.SavePath);
        Assert.Contains("(recovered autosave)", w.Title ?? "");
    }

    // ---------- physical dice ----------

    [AvaloniaFact]
    public void Physical_roll_asks_each_die()
    {
        var w = Game();
        Save(w, s => s with { PhysicalDiceDefault = true });
        _dialogs.Texts.Enqueue("3");
        _dialogs.Texts.Enqueue("5");
        w.ClickMenu("&2d6");
        Assert.True(WaitFor(() => Rolls(w).Count == 1));
        Assert.Equal(new[] { "Physical dice|Value of die 1 of 2 (1–6):|", "Physical dice|Value of die 2 of 2 (1–6):|" }, _dialogs.TextPrompts);
        Assert.Equal(new[] { 3, 5 }, Rolls(w)[0].Results);
        Assert.Empty(_dialogs.Infos);

        // The setting off: dice roll by themselves again, nothing is asked.
        Save(w, s => s with { PhysicalDiceDefault = false });
        w.ClickMenu("&1d6");
        Pump();
        Assert.Equal(2, Rolls(w).Count);
        Assert.Equal(2, _dialogs.TextPrompts.Count);
    }

    [AvaloniaFact]
    public void Physical_bad_value_reasks()
    {
        var w = Game();
        Save(w, s => s with { PhysicalDiceDefault = true });
        foreach (var t in new[] { "2", "x", "7", "0", "5" }) _dialogs.Texts.Enqueue(t);
        w.ClickMenu("&2d6");
        Assert.True(WaitFor(() => Rolls(w).Count == 1));
        Assert.Equal(new[] { 2, 5 }, Rolls(w)[0].Results);
        Assert.Equal(3, _dialogs.Infos.Count);
        Assert.All(_dialogs.Infos, i => Assert.Equal(("Enter a whole number from 1 to 6.", "Physical dice"), i));
        // The second die was asked again each time, with the same text.
        Assert.Equal(1, _dialogs.TextPrompts.Count(p => p.Contains("die 1 of 2")));
        Assert.Equal(4, _dialogs.TextPrompts.Count(p => p.Contains("die 2 of 2")));
    }

    [AvaloniaFact]
    public void Physical_cancel_records_nothing()
    {
        var w = Game();
        Save(w, s => s with { PhysicalDiceDefault = true });
        _dialogs.Texts.Enqueue("3");
        _dialogs.Texts.Enqueue(null);   // Cancel on die 2 of 2
        w.ClickMenu("&2d6");
        Assert.True(WaitFor(() => _dialogs.TextPrompts.Count == 2));
        Pump();
        Assert.Empty(Rolls(w));

        // The provider did not stay behind: the next roll asks again from die 1.
        _dialogs.Texts.Enqueue("4");
        w.ClickMenu("&1d6");
        Assert.True(WaitFor(() => Rolls(w).Count == 1));
        Assert.Equal(new[] { 4 }, Rolls(w)[0].Results);
    }

    [AvaloniaFact]
    public void Physical_multi_roll_asks_count_times_times()
    {
        var w = Game();
        Save(w, s => s with { PhysicalDiceDefault = true });
        _dialogs.Dice = new DiceChoice(new DiceSpec(2, 6, 0), 2);
        foreach (var t in new[] { "1", "2", "3", "4" }) _dialogs.Texts.Enqueue(t);
        w.ClickMenu("&Multi-roll...");
        Assert.True(WaitFor(() => Rolls(w).Count == 2));
        Assert.Equal(4, _dialogs.TextPrompts.Count);
        Assert.Equal(new[] { "Multi-roll 1/2", "Multi-roll 2/2" }, Rolls(w).Select(r => r.Purpose));
        Assert.Equal(new[] { 1, 2 }, Rolls(w)[0].Results);
        Assert.Equal(new[] { 3, 4 }, Rolls(w)[1].Results);

        // Cancelling the last die of the last roll records no partial multi-roll.
        foreach (var t in new[] { "1", "1", "1" }) _dialogs.Texts.Enqueue(t);
        _dialogs.Texts.Enqueue(null);
        w.ClickMenu("&Multi-roll...");
        Assert.True(WaitFor(() => _dialogs.TextPrompts.Count == 8));
        Pump();
        Assert.Equal(2, Rolls(w).Count);
    }

    [AvaloniaFact]
    public void Physical_module_die_asks_one_of_faces()
    {
        var w = Game();
        Save(w, s => s with { PhysicalDiceDefault = true });
        _dialogs.Texts.Enqueue("2");
        w.ClickMenu("Weather");
        Assert.True(WaitFor(() => Rolls(w).Count == 1));
        Assert.Equal(new[] { "Physical dice|Value of die 1 of 1 (1–2):|" }, _dialogs.TextPrompts);
        var roll = Rolls(w)[0];
        Assert.Equal(new[] { 2 }, roll.Results);
        Assert.Equal(new[] { "Rain" }, roll.FaceTexts);
    }

    [AvaloniaFact]
    public void Formula_window_physical_starts_from_setting()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        w.StartSetup(0, Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Pump();

        static CheckBox Box(FormulaWindow win) =>
            win.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Physical dice");

        var off = FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null)!;
        Pump();
        Assert.False(Box(off).IsChecked);
        off.Close();
        Pump();

        Save(w, x => x with { PhysicalDiceDefault = true });
        var on = FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null)!;
        Pump();
        Assert.True(Box(on).IsChecked);
        // The user can still turn it off for this window.
        Box(on).IsChecked = false;
        Pump();
        Assert.False(Box(on).IsChecked);
    }

    // ---------- remembered difficulty ----------

    [AvaloniaFact]
    public void New_game_remembers_difficulty()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(DiceModule()));
        Assert.Equal(Difficulty.SemiAuto, w.SettingsStore.Current.LastDifficulty);

        _dialogs.Setup = new SetupChoice(null, Difficulty.Hardcore);
        w.ClickMenu("&New Game");
        Assert.True(WaitFor(() => _dialogs.SetupChooserShown == 1));
        Assert.True(WaitFor(() => w.SettingsStore.Current.LastDifficulty == Difficulty.Hardcore));
        Assert.Contains("\"LastDifficulty\"", File.ReadAllText(Path.Combine(LayoutRoot, "settings.json")));

        // A later run of the app starts its chooser from it (the value the chooser is given is the store's).
        var w2 = NewWindow();
        Assert.Equal(Difficulty.Hardcore, w2.SettingsStore.Current.LastDifficulty);
        Assert.True(w2.OpenModule(DiceModule()));
        _dialogs.Setup = new SetupChoice(null, Difficulty.FullyAuto);
        w2.ClickMenu("&New Game");
        Assert.True(WaitFor(() => w2.SettingsStore.Current.LastDifficulty == Difficulty.FullyAuto));
    }
    // ---------- outliner ----------

    [AvaloniaFact]
    public void Outliner_visibility_and_grouping_survive_restart()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var w = NewWindow();
        Assert.True(w.OpenModule(fx.Build()));
        w.StartSetup(0, Difficulty.FullyAuto);
        Pump();
        Assert.False(w.OutlinerShown);
        Assert.False(w.SettingsStore.Current.OutlinerVisible);

        w.ClickMenu("&Outliner");
        Pump();
        Assert.True(w.OutlinerShown);
        Assert.True(w.SettingsStore.Current.OutlinerVisible);
        w.Outliner!.Grouping = OutlinerGrouping.BySide;
        Pump();
        Assert.Equal(OutlinerGrouping.BySide, w.SettingsStore.Current.OutlinerGrouping);
        w.ForceClose();

        var w2 = NewWindow();
        Assert.True(w2.OpenModule(fx.Build()));
        w2.StartSetup(0, Difficulty.FullyAuto);
        Pump();
        Assert.True(w2.OutlinerShown);
        Assert.Equal(OutlinerGrouping.BySide, w2.Outliner!.Grouping);

        // Hiding it is remembered too.
        w2.ClickMenu("&Outliner");
        Pump();
        Assert.False(w2.OutlinerShown);
        Assert.False(w2.SettingsStore.Current.OutlinerVisible);
    }
}
