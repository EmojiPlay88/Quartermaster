using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2a task 3: rules packs in the Desktop main window, headless, fake dialogs, temp folders for the remembered-pack index.</summary>
[Collection("MacFlag")]
public sealed class RulesPackTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-rules-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", "Riverland-Demo.vmod"));

    MainWindow NewWindow(FakeDialogs? dialogs = null, bool remember = false)
    {
        var w = new MainWindow(dialogs ?? _dialogs, Path.Combine(_tmp, "cache"), remember ? Path.Combine(_tmp, "rules") : null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    string TmpFile(string name)
    {
        Directory.CreateDirectory(_tmp);
        return Path.Combine(_tmp, name);
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void Menu_has_rules_items_in_winforms_order()
    {
        var w = NewWindow();
        var file = w.MenuLabels()["&File"].ToList();
        var i = file.IndexOf("New &Rules Pack...");
        Assert.True(i > file.IndexOf("Save Game &As..."));
        Assert.Equal(new[] { "New &Rules Pack...", "Open Rules &Pack...", "Save Rules Pac&k", "Save Rules Pack A&s...", "E&xit" }, file.Skip(i));

        // Enablement from CommandAvailability: nothing without a module; Save needs a pack.
        Assert.False(w.FindMenuItem("New &Rules Pack...")!.IsEnabled);
        Assert.False(w.FindMenuItem("Open Rules &Pack...")!.IsEnabled);
        Assert.True(w.OpenModule(Demo()));
        Assert.True(w.FindMenuItem("New &Rules Pack...")!.IsEnabled);
        Assert.True(w.FindMenuItem("Open Rules &Pack...")!.IsEnabled);
        Assert.False(w.FindMenuItem("Save Rules Pac&k")!.IsEnabled);
        Assert.False(w.FindMenuItem("Save Rules Pack A&s...")!.IsEnabled);
        Assert.False(w.FindMenuItem("&Save Game")!.IsEnabled);   // stage-1 items keep their behaviour
        _dialogs.Texts.Enqueue("Mine");
        w.ClickMenu("New &Rules Pack...");
        Assert.True(w.FindMenuItem("Save Rules Pac&k")!.IsEnabled);
        Assert.True(w.FindMenuItem("Save Rules Pack A&s...")!.IsEnabled);
    }

    [AvaloniaFact]
    public void New_open_save_roundtrip()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));

        _dialogs.Texts.Enqueue("  Mine ");
        w.ClickMenu("New &Rules Pack...");
        Assert.Equal(new[] { "New Rules Pack|Name of the new rules pack:|Rules" }, _dialogs.TextPrompts);
        Assert.Equal("Mine", w.Session!.Rules!.Name);
        Assert.EndsWith(" — rules: Mine*", w.Title);

        var path = TmpFile("mine.qrules");
        _dialogs.SaveFiles.Enqueue(path);
        w.ClickMenu("Save Rules Pac&k");
        Assert.True(File.Exists(path));
        Assert.EndsWith(" — rules: Mine", w.Title);
        Assert.Contains("Save Rules Pack", _dialogs.PickerTitles);

        // Save again: no picker; Save As: picker.
        _dialogs.PickerTitles.Clear();
        w.ClickMenu("Save Rules Pac&k");
        Assert.Empty(_dialogs.PickerTitles);
        var copy = TmpFile("copy.qrules");
        _dialogs.SaveFiles.Enqueue(copy);
        w.ClickMenu("Save Rules Pack A&s...");
        Assert.True(File.Exists(copy));
        Assert.Equal(copy, w.Session.RulesPath);

        // Open in a second window.
        var d2 = new FakeDialogs();
        var w2 = NewWindow(d2);
        Assert.True(w2.OpenModule(Demo()));
        d2.OpenFiles.Enqueue(path);
        w2.ClickMenu("Open Rules &Pack...");
        Assert.Equal("Mine", w2.Session!.Rules!.Name);
        Assert.Equal(path, w2.Session.RulesPath);
        Assert.EndsWith(" — rules: Mine", w2.Title);
        Assert.Equal(new[] { "Open Rules Pack" }, d2.PickerTitles);

        // A cancelled name changes nothing.
        d2.Texts.Enqueue(null);
        w2.ClickMenu("New &Rules Pack...");
        Assert.Equal(path, w2.Session.RulesPath);
    }

    [AvaloniaFact]
    public void Remembered_pack_loads_with_module()
    {
        var w = NewWindow(remember: true);
        Assert.True(w.OpenModule(Demo()));
        Assert.Null(w.Session!.Rules);
        _dialogs.Texts.Enqueue("Kept");
        w.ClickMenu("New &Rules Pack...");
        _dialogs.SaveFiles.Enqueue(TmpFile("kept.qrules"));
        w.ClickMenu("Save Rules Pac&k");
        w.ForceClose();

        var w2 = NewWindow(new FakeDialogs(), remember: true);
        Assert.True(w2.OpenModule(Demo()));
        Assert.Equal("Kept", w2.Session!.Rules!.Name);
        Assert.False(w2.Session.RulesDirty);
        Assert.EndsWith(" — rules: Kept", w2.Title);

        // Without remembering (null root) nothing is loaded.
        var w3 = NewWindow(new FakeDialogs());
        Assert.True(w3.OpenModule(Demo()));
        Assert.Null(w3.Session!.Rules);
    }

    [AvaloniaFact]
    public void Remembered_pack_missing_is_ignored()
    {
        var w = NewWindow(remember: true);
        Assert.True(w.OpenModule(Demo()));
        _dialogs.Texts.Enqueue("Gone");
        w.ClickMenu("New &Rules Pack...");
        var path = TmpFile("gone.qrules");
        _dialogs.SaveFiles.Enqueue(path);
        w.ClickMenu("Save Rules Pac&k");
        w.ForceClose();
        File.Delete(path);

        var d2 = new FakeDialogs();
        var w2 = NewWindow(d2, remember: true);
        Assert.True(w2.OpenModule(Demo()));
        Assert.Null(w2.Session!.Rules);
        Assert.Empty(d2.Warnings);
        Assert.DoesNotContain(w2.Session.Log.Items, d => d.Category == "rules");
        Assert.DoesNotContain("rules:", w2.Title);
    }

    [AvaloniaFact]
    public void Pack_for_other_module_warns()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        var path = TmpFile("other.qrules");
        RulesPackIo.Save(RulesPackIo.CreateEmpty("Other", new string('0', 64)), path);
        _dialogs.OpenFiles.Enqueue(path);
        w.ClickMenu("Open Rules &Pack...");
        // WinForms loads it anyway and logs a warning (ModuleSession.OpenRules); no message box.
        Assert.Equal("Other", w.Session!.Rules!.Name);
        Assert.Contains(w.Session.Log.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Category == "rules"
            && d.Message == "Rules pack was made for a different module");
        Assert.Empty(_dialogs.Warnings);

        // A pack that cannot be read is a warning box, as WinForms Guard.
        var bad = TmpFile("bad.qrules");
        File.WriteAllText(bad, "not a pack");
        _dialogs.OpenFiles.Enqueue(bad);
        w.ClickMenu("Open Rules &Pack...");
        Pump();
        Assert.Single(_dialogs.Warnings);
        Assert.Equal("Other", w.Session.Rules!.Name);
    }

    [AvaloniaFact]
    public void Close_with_dirty_rules_asks()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        _dialogs.Texts.Enqueue("Mine");
        w.ClickMenu("New &Rules Pack...");

        _dialogs.YesNoCancel.Enqueue(null);   // Cancel keeps the window open
        w.Close();
        Pump();
        Assert.True(w.IsVisible);
        Assert.Equal(("Save changes to the rules pack 'Mine'?", "Quartermaster"), _dialogs.Questions.Single());

        var path = TmpFile("closing.qrules");
        _dialogs.SaveFiles.Enqueue(path);
        _dialogs.YesNoCancel.Enqueue(true);   // Yes saves, then closes
        w.Close();
        Pump();
        Pump();
        Assert.True(File.Exists(path));
        Assert.False(w.IsVisible);
    }

    [AvaloniaFact]
    public void Open_module_with_dirty_rules_asks_and_cancel_keeps_module()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        var first = w.Session;
        _dialogs.Texts.Enqueue("Mine");
        w.ClickMenu("New &Rules Pack...");

        _dialogs.OpenFiles.Enqueue(Demo());
        _dialogs.YesNoCancel.Enqueue(null);
        w.ClickMenu("&Open Module...");
        Pump();
        Assert.Same(first, w.Session);
        Assert.Contains("Save changes to the rules pack 'Mine'?", _dialogs.Questions.Select(q => q.Message));

        _dialogs.OpenFiles.Enqueue(Demo());
        _dialogs.YesNoCancel.Enqueue(false);   // No: discard the pack and switch
        w.ClickMenu("&Open Module...");
        Pump();
        Assert.NotSame(first, w.Session);
    }

    [AvaloniaFact]
    public void Command_line_rules_loads()
    {
        var path = TmpFile("cli.qrules");
        RulesPackIo.Save(RulesPackIo.CreateEmpty("FromCli", new string('0', 64)), path);
        var warnings = new List<string>();
        var a = StartupArgs.Parse(new[] { Demo(), "--rules", path, "--new" }, warnings.Add);
        Assert.Equal(path, a.RulesPath);
        Assert.Empty(warnings);
        var w = NewWindow();
        w.ApplyStartup(a);
        Assert.Equal("FromCli", w.Session!.Rules!.Name);
        Assert.NotNull(w.Session.Game);
        Assert.EndsWith(" — rules: FromCli", w.Title);

        var b = StartupArgs.Parse(new[] { Demo(), "--new-rules", "Fresh", "--new" }, warnings.Add);
        var w2 = NewWindow(new FakeDialogs());
        w2.ApplyStartup(b);
        Assert.Equal("Fresh", w2.Session!.Rules!.Name);

        StartupArgs.Parse(new[] { "--rules" }, warnings.Add);
        Assert.Equal(new[] { "--rules needs a path; ignored." }, warnings);
    }
}
