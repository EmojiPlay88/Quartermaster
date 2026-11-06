using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void SettingsStore_RoundTrip()
    {
        var store = new SettingsStore(_tmp);
        var s = new Settings
        {
            ReopenLastModule = true, AutosaveMinutes = 12, ConfirmDelete = false, GameMasterMode = true,
            GridDefault = GridMode.Always, LabelFontSize = 11, HighlightColor = "#112233", TabMinWidth = 70, TabMaxWidth = 200,
            WheelZoomStep = 1.5, SnapOnDrop = false, DragThresholdPx = 9, HoverDelayMs = 250,
            PhysicalDiceDefault = true, ShowLastRollInStatus = false, LogShowMoves = false, LogTimeFormat = "mm:ss",
            ToolbarVisible = new Dictionary<string, bool> { ["tool.diagnostics"] = false },
            ShortcutOverrides = new Dictionary<string, string> { ["file.save"] = "Ctrl+Alt+S", ["file.new"] = "" },
            LeftSplitterDistance = 0.4, LastModulePath = @"C:\x\y.vmod",
        };
        var raised = new List<Settings>();
        store.Changed += raised.Add;
        store.Save(s);
        Assert.Single(raised);
        Assert.Same(s, store.Current);

        var log = new DiagnosticLog();
        var back = new SettingsStore(_tmp).Load(log);
        Assert.Empty(log.Items);
        Assert.Equal(s with { ToolbarVisible = back.ToolbarVisible, ShortcutOverrides = back.ShortcutOverrides }, back);
        Assert.False(back.ToolbarVisible["tool.diagnostics"]);
        Assert.False(back.IsToolbarVisible("tool.diagnostics"));
        Assert.True(back.IsToolbarVisible("Missing"));
        Assert.Equal("Ctrl+Alt+S", back.ShortcutOverrides["file.save"]);
        Assert.Equal("", back.ShortcutOverrides["file.new"]);
        Assert.False(File.Exists(Path.Combine(_tmp, "settings.json.tmp")));
    }

    [Fact]
    public void SettingsStore_MissingFile_Defaults()
    {
        var log = new DiagnosticLog();
        var d = new Settings();
        var s = new SettingsStore(Path.Combine(_tmp, "nothing")).Load(log);
        Assert.Equal(d with { ToolbarVisible = s.ToolbarVisible, ShortcutOverrides = s.ShortcutOverrides }, s);
        Assert.Empty(s.ToolbarVisible);
        Assert.Equal(5, s.AutosaveMinutes);
        Assert.Equal(1.2, s.WheelZoomStep);
        Assert.Equal("#FFD700", s.HighlightColor);
        Assert.Empty(log.Items);
    }

    [Fact]
    public void SettingsStore_CorruptOrPartial_FallsBackPerField()
    {
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), """
        {
          "AutosaveMinutes": 9999,
          "WheelZoomStep": 0.5,
          "LabelFontSize": 12,
          "GridDefault": "Sideways",
          "HighlightColor": "banana",
          "ConfirmDelete": "yes",
          "HoverDelayMs": 300,
          "LogShowMoves": false,
          "ShortcutOverrides": { "a": "Ctrl+A", "b": 5 },
          "Unknown": 1
        }
        """);
        var log = new DiagnosticLog();
        var s = new SettingsStore(_tmp).Load(log);
        Assert.Equal(240, s.AutosaveMinutes);                // clamped
        Assert.Equal(1.05, s.WheelZoomStep);                 // clamped
        Assert.Equal(12, s.LabelFontSize);                   // kept
        Assert.Equal(GridMode.ModuleDefault, s.GridDefault); // invalid enum
        Assert.Equal("#FFD700", s.HighlightColor);           // invalid colour
        Assert.True(s.ConfirmDelete);                        // wrong type
        Assert.Equal(300, s.HoverDelayMs);
        Assert.False(s.LogShowMoves);
        Assert.Equal("Ctrl+A", s.ShortcutOverrides["a"]);
        Assert.False(s.ShortcutOverrides.ContainsKey("b"));
        var msgs = log.Items.Where(d => d.Severity == DiagnosticSeverity.Warning && d.Category == "settings").Select(d => d.Message).ToList();
        foreach (var field in new[] { "AutosaveMinutes", "WheelZoomStep", "GridDefault", "HighlightColor", "ConfirmDelete" })
            Assert.Single(msgs, m => m.Contains(field));
        Assert.DoesNotContain(msgs, m => m.Contains("LabelFontSize") || m.Contains("Unknown"));
    }

    [Fact]
    public void SettingsStore_GarbageFile_DefaultsWithOneDiagnostic()
    {
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), "{ not json");
        var log = new DiagnosticLog();
        var s = new SettingsStore(_tmp).Load(log);
        Assert.Equal(5, s.AutosaveMinutes);
        Assert.Single(log.Items);
    }

    [Fact]
    public void SettingsStore_LastDifficulty_RoundTripAndBadValue()
    {
        var store = new SettingsStore(_tmp);
        Assert.Equal(Quartermaster.Core.Difficulty.SemiAuto, new Settings().LastDifficulty);
        store.Save(new Settings { LastDifficulty = Quartermaster.Core.Difficulty.FullyAuto });
        Assert.Equal(Quartermaster.Core.Difficulty.FullyAuto, new SettingsStore(_tmp).Load(null).LastDifficulty);

        File.WriteAllText(Path.Combine(_tmp, "settings.json"), """{ "LastDifficulty": "Impossible" }""");
        var log = new DiagnosticLog();
        var s = new SettingsStore(_tmp).Load(log);
        Assert.Equal(Quartermaster.Core.Difficulty.SemiAuto, s.LastDifficulty);
        Assert.Single(log.Items, d => d.Message.Contains("LastDifficulty"));
    }

    [Fact]
    public void SettingsStore_EnumByNameIgnoringCase()
    {
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), """{ "GridDefault": "never" }""");
        Assert.Equal(GridMode.Never, new SettingsStore(_tmp).Load(null).GridDefault);
    }

    [Fact]
    public void ToolbarIds_MigrateCaptionKeys_ToStableIds()
    {
        var m = ToolbarIds.Migrate(new Dictionary<string, bool>
        {
            ["Diagnostics"] = false, ["1d6"] = false, ["Dice"] = false, ["tool.notes"] = false, ["Bogus"] = false,
            ["Save Game"] = false, ["tool.saveGame"] = true,
        });
        Assert.False(m["tool.diagnostics"]);
        Assert.False(m["dice.1d6"]);
        Assert.False(m["dice.menu"]);
        Assert.False(m["tool.notes"]);
        Assert.True(m["tool.saveGame"]);          // the new id wins over a legacy caption
        Assert.DoesNotContain("Bogus", m.Keys);   // unknown keys are ignored
        Assert.DoesNotContain("Diagnostics", m.Keys);
    }

    [Fact]
    public void SettingsStore_Load_MigratesToolbarCaptions()
    {
        File.WriteAllText(Path.Combine(_tmp, "settings.json"), "{\"ToolbarVisible\":{\"Open Game\":false,\"Weird\":false}}");
        var log = new DiagnosticLog();
        var back = new SettingsStore(_tmp).Load(log);
        Assert.False(back.IsToolbarVisible("tool.openGame"));
        Assert.True(back.IsToolbarVisible("Weird"));
        Assert.DoesNotContain("Open Game", back.ToolbarVisible.Keys);
    }
}
