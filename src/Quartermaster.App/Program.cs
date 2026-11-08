using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Command line: [&lt;vmod path&gt;] [--setup &lt;index&gt;|--new] [--smoke &lt;seconds&gt;] [--screenshot &lt;png&gt;] [--float-main-map] [--screenshot-float &lt;png&gt;] [--rules &lt;path&gt;|--new-rules &lt;name&gt;] [--smoke-launcher] [--screenshot-launcher &lt;png&gt;] [--screenshot-offline &lt;png&gt;] [--screenshot-formula &lt;png&gt;] [--screenshot-ledger &lt;png&gt;] [--screenshot-workbench &lt;png&gt;] [--workbench-tab tables|formulas|properties|phases] [--gm] [--screenshot-palette &lt;png&gt;] [--screenshot-log &lt;png&gt;] [--screenshot-overview &lt;png&gt;] [--screenshot-export &lt;png&gt;] [--screenshot-inventory &lt;png&gt;] [--screenshot-notes &lt;png&gt;] [--screenshot-settings &lt;png&gt;] [--difficulty hardcore|semiauto|fullyauto] [--smoke-phases] [--smoke-undo] [--screenshot-stepback &lt;png&gt;] [--screenshot-breakdown &lt;png&gt;] [--screenshot-outliner &lt;png&gt;] [--screenshot-alerts &lt;png&gt;] [--screenshot-tour &lt;png&gt;] [--screenshot-handover &lt;png&gt;] [--screenshot-playerview &lt;png&gt;] [--screenshot-phases &lt;png&gt;] [--screenshot-nextphase &lt;png&gt;] [--screenshot-cheatsheet &lt;png&gt;] [--smoke-online &lt;seconds&gt;] [--screenshot-online &lt;png&gt;] [--smoke-turns] [--screenshot-turns &lt;png&gt;] [--smoke-search] [--screenshot-search &lt;png&gt;] [--smoke-ocr] [--screenshot-ocr &lt;png&gt;] [--no-smoke-documents] [--settings-tab general|display|map|dice|toolbar|keyboard|folders].</summary>
sealed record StartupOptions(string? ModulePath, int? SetupIndex, bool NewGame, int? SmokeSeconds, string? Screenshot = null, bool FloatMainMap = false, string? ScreenshotFloat = null,
    string? RulesPath = null, string? NewRulesName = null, string? ScreenshotFormula = null, string? ScreenshotLedger = null, string? ScreenshotWorkbench = null, string? WorkbenchTab = null, string? ScreenshotLog = null, string? ScreenshotSettings = null, string? SettingsTab = null, bool GameMaster = false, string? ScreenshotPalette = null, string? ScreenshotOverview = null, string? ScreenshotExport = null, string? ScreenshotInventory = null, string? ScreenshotNotes = null, Difficulty? Difficulty = null, bool SmokePhases = false, string? ScreenshotPhases = null, string? ScreenshotNextPhase = null, string? ScreenshotCheatSheet = null, string? ScreenshotBatch = null, string? ScreenshotSetup = null, bool SmokeUndo = false, string? ScreenshotStepBack = null, string? ScreenshotBreakdown = null, string? ScreenshotOutliner = null, string? ScreenshotAlerts = null, string? ScreenshotTour = null, string? ScreenshotHandover = null, string? ScreenshotPlayerView = null, string? ScreenshotLauncher = null, string? ScreenshotOffline = null, bool SmokeLauncher = false, int? SmokeOnline = null, string? ScreenshotOnline = null, bool SmokeTurns = false, string? ScreenshotTurns = null, bool SmokeSearch = false, string? ScreenshotSearch = null, bool SmokeOcr = false, string? ScreenshotOcr = null, bool NoSmokeDocuments = false)
{
    public bool Smoke => SmokeSeconds != null;

    public static StartupOptions Parse(string[] args, Action<string> warn)
    {
        string? path = null;
        int? setup = null, smoke = null;
        var newGame = false;
        string? shotHandover = null, shotPlayerView = null, shotLauncher = null, shotOffline = null, shotTour = null, shot = null, shotFloat = null, rules = null, newRules = null, shotFormula = null, shotLedger = null, shotBench = null, benchTab = null, shotLog = null, shotSettings = null, settingsTab = null, shotPalette = null, shotOverview = null, shotExport = null, shotInventory = null, shotNotes = null, shotPhases = null, shotNext = null, shotCheat = null, shotBatch = null, shotSetup = null, shotStepBack = null, shotBreakdown = null, shotOutliner = null, shotAlerts = null;
        Difficulty? difficulty = null;
        var smokePhases = false;
        var smokeUndo = false;
        var smokeLauncher = false;
        var smokeTurns = false;
        string? shotTurns = null;
        var smokeSearch = false;
        string? shotSearch = null;
        var smokeOcr = false;
        string? shotOcr = null;
        var noDocs = false;
        int? smokeOnline = null;
        string? shotOnline = null;
        var gm = false;
        var floatMain = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--new": newGame = true; break;
                case "--updated-from" or "--update-failed" or "--update-refused" or "--screenshot-update-bar": // updater options live in AppUpdates
                    if (i + 1 < args.Length) AppUpdates.Arg(a, args[++i]);
                    else warn($"{a} needs a value; ignored.");
                    break;
                case "--screenshot-nextphase":
                    if (i + 1 < args.Length) shotNext = args[++i];
                    else warn("--screenshot-nextphase needs a file path; ignored.");
                    break;
                case "--screenshot-cheatsheet":
                    if (i + 1 < args.Length) shotCheat = args[++i];
                    else warn("--screenshot-cheatsheet needs a file path; ignored.");
                    break;
                case "--screenshot-batch":
                    if (i + 1 < args.Length) shotBatch = args[++i];
                    else warn("--screenshot-batch needs a file path; ignored.");
                    break;
                case "--screenshot-setup":
                    if (i + 1 < args.Length) shotSetup = args[++i];
                    else warn("--screenshot-setup needs a file path; ignored.");
                    break;
                case "--screenshot-breakdown":
                    if (i + 1 < args.Length) shotBreakdown = args[++i];
                    else warn("--screenshot-breakdown needs a file path; ignored.");
                    break;
                case "--screenshot-outliner":
                    if (i + 1 < args.Length) shotOutliner = args[++i];
                    else warn("--screenshot-outliner needs a file path; ignored.");
                    break;
                case "--screenshot-alerts":
                    if (i + 1 < args.Length) shotAlerts = args[++i];
                    else warn("--screenshot-alerts needs a file path; ignored.");
                    break;
                case "--screenshot-handover":
                    if (i + 1 < args.Length) shotHandover = args[++i];
                    else warn("--screenshot-handover needs a file path; ignored.");
                    break;
                case "--screenshot-playerview":
                    if (i + 1 < args.Length) shotPlayerView = args[++i];
                    else warn("--screenshot-playerview needs a file path; ignored.");
                    break;
                case "--screenshot-tour":
                    if (i + 1 < args.Length) shotTour = args[++i];
                    else warn("--screenshot-tour needs a file path; ignored.");
                    break;
                case "--screenshot-launcher":
                    if (i + 1 < args.Length) shotLauncher = args[++i];
                    else warn("--screenshot-launcher needs a file path; ignored.");
                    break;
                case "--screenshot-offline":
                    if (i + 1 < args.Length) shotOffline = args[++i];
                    else warn("--screenshot-offline needs a file path; ignored.");
                    break;
                case "--no-smoke-documents": noDocs = true; break; // screenshots: do not open the sample chart/rules text windows
                case "--smoke-phases": smokePhases = true; break;
                case "--smoke-undo": smokeUndo = true; break;
                case "--smoke-launcher": smokeLauncher = true; break;
                case "--smoke-turns": smokeTurns = true; break;
                case "--smoke-search": smokeSearch = true; break;
                case "--smoke-ocr": smokeOcr = true; break;
                case "--screenshot-ocr":
                    if (i + 1 < args.Length) shotOcr = args[++i];
                    else warn("--screenshot-ocr needs a file path; ignored.");
                    break;
                case "--screenshot-search":
                    if (i + 1 < args.Length) shotSearch = args[++i];
                    else warn("--screenshot-search needs a file path; ignored.");
                    break;
                case "--screenshot-turns":
                    if (i + 1 < args.Length) shotTurns = args[++i];
                    else warn("--screenshot-turns needs a file path; ignored.");
                    break;
                case "--smoke-online":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var so)) { smokeOnline = so; i++; }
                    else warn("--smoke-online needs a number of seconds; ignored.");
                    break;
                case "--screenshot-online":
                    if (i + 1 < args.Length) shotOnline = args[++i];
                    else warn("--screenshot-online needs a file path; ignored.");
                    break;
                case "--screenshot-stepback":
                    if (i + 1 < args.Length) shotStepBack = args[++i];
                    else warn("--screenshot-stepback needs a file path; ignored.");
                    break;
                case "--screenshot-phases":
                    if (i + 1 < args.Length) shotPhases = args[++i];
                    else warn("--screenshot-phases needs a file path; ignored.");
                    break;
                case "--difficulty":
                    if (i + 1 < args.Length && ParseDifficulty(args[i + 1]) is { } dm) { difficulty = dm; i++; }
                    else warn("--difficulty needs hardcore, semiauto or fullyauto; ignored.");
                    break;
                case "--gm": gm = true; break; // game master mode for this run only (not saved)
                case "--screenshot-palette":
                    if (i + 1 < args.Length) shotPalette = args[++i];
                    else warn("--screenshot-palette needs a file path; ignored.");
                    break;
                case "--setup" or "--smoke":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        if (a == "--setup") setup = n; else smoke = n;
                        i++;
                    }
                    else warn($"{a} needs a non-negative integer; ignored.");
                    break;
                case "--screenshot":
                    if (i + 1 < args.Length) shot = args[++i];
                    else warn("--screenshot needs a file path; ignored.");
                    break;
                case "--float-main-map": floatMain = true; break;
                case "--screenshot-float":
                    if (i + 1 < args.Length) shotFloat = args[++i];
                    else warn("--screenshot-float needs a file path; ignored.");
                    break;
                case "--rules":
                    if (i + 1 < args.Length) rules = args[++i];
                    else warn("--rules needs a path; ignored.");
                    break;
                case "--new-rules":
                    if (i + 1 < args.Length) newRules = args[++i];
                    else warn("--new-rules needs a name; ignored.");
                    break;
                case "--screenshot-formula":
                    if (i + 1 < args.Length) shotFormula = args[++i];
                    else warn("--screenshot-formula needs a file path; ignored.");
                    break;
                case "--screenshot-ledger":
                    if (i + 1 < args.Length) shotLedger = args[++i];
                    else warn("--screenshot-ledger needs a file path; ignored.");
                    break;
                case "--screenshot-ledger-tabs":
                    if (i + 1 < args.Length) MainForm.LedgerTabsShot = args[++i];
                    else warn("--screenshot-ledger-tabs needs a file path; ignored.");
                    break;
                case "--screenshot-workbench":
                    if (i + 1 < args.Length) shotBench = args[++i];
                    else warn("--screenshot-workbench needs a file path; ignored.");
                    break;
                case "--screenshot-inventory":
                    if (i + 1 < args.Length) shotInventory = args[++i];
                    else warn("--screenshot-inventory needs a file path; ignored.");
                    break;
                case "--screenshot-notes":
                    if (i + 1 < args.Length) shotNotes = args[++i];
                    else warn("--screenshot-notes needs a file path; ignored.");
                    break;
                case "--screenshot-export":
                    if (i + 1 < args.Length) shotExport = args[++i];
                    else warn("--screenshot-export needs a file path; ignored.");
                    break;
                case "--screenshot-overview":
                    if (i + 1 < args.Length) shotOverview = args[++i];
                    else warn("--screenshot-overview needs a file path; ignored.");
                    break;
                case "--screenshot-log":
                    if (i + 1 < args.Length) shotLog = args[++i];
                    else warn("--screenshot-log needs a file path; ignored.");
                    break;
                case "--screenshot-settings":
                    if (i + 1 < args.Length) shotSettings = args[++i];
                    else warn("--screenshot-settings needs a file path; ignored.");
                    break;
                case "--settings-tab":
                    if (i + 1 < args.Length) settingsTab = args[++i];
                    else warn("--settings-tab needs a tab name; ignored.");
                    break;
                case "--workbench-tab":
                    if (i + 1 < args.Length) benchTab = args[++i];
                    else warn("--workbench-tab needs tables, formulas, properties or phases; ignored.");
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) warn($"Unknown option '{a}'; ignored.");
                    else if (path == null) path = a;
                    else warn($"Extra argument '{a}'; ignored.");
                    break;
            }
        }
        return new StartupOptions(path, setup, newGame, smoke, shot, floatMain, shotFloat, rules, newRules, shotFormula, shotLedger, shotBench, benchTab, shotLog, shotSettings, settingsTab, gm, shotPalette, shotOverview, shotExport, shotInventory, shotNotes, difficulty, smokePhases, shotPhases, shotNext, shotCheat, shotBatch, shotSetup, smokeUndo, shotStepBack, shotBreakdown, shotOutliner, shotAlerts, shotTour, shotHandover, shotPlayerView, shotLauncher, shotOffline, smokeLauncher, smokeOnline, shotOnline, smokeTurns, shotTurns, smokeSearch, shotSearch, smokeOcr, shotOcr, noDocs);
    }

    static Difficulty? ParseDifficulty(string s) => s.ToLowerInvariant() switch
    {
        "hardcore" => Core.Difficulty.Hardcore,
        "semiauto" => Core.Difficulty.SemiAuto,
        "fullyauto" => Core.Difficulty.FullyAuto,
        _ => null,
    };
}

static class Program
{
    public static bool Smoke { get; private set; }
    public static int ExitCode { get; set; }

    /// <summary>Shows the launcher and returns what to play, or null when it was closed. Task 4 sets this; while null the app opens the game window directly as before.</summary>
    public static Func<LaunchRequest?>? LauncherHook { get; set; }

    /// <summary>True once the game window was closed through Back to launcher (so nothing reopens the last module by itself).</summary>
    public static bool Relaunched { get; private set; }

    /// <summary>The module the game window had open when it went Back to launcher (selected there whatever the setting says).</summary>
    public static string? JustPlayedModule { get; private set; }

    /// <summary>app.log and crash.log live here; QUARTERMASTER_LAYOUT_ROOT redirects them with everything else (<see cref="AppDataRoot"/>).</summary>
    static string LogDir => AppDataRoot.Path;

    /// <summary>Appends a line to app.log (used instead of dialogs in smoke mode).</summary>
    public static void AppLog(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "app.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch (Exception) { /* logging must never throw */ }
    }

    static void Crash(Exception ex)
    {
        ExitCode = 1;
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(Path.Combine(LogDir, "crash.log"), $"{DateTime.Now:s}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception) { /* nothing more we can do */ }
        if (Smoke) { Application.Exit(); return; }
        try
        {
            MessageBox.Show("Quartermaster hit an unexpected error:\n\n" + ex.Message +
                "\n\nDetails were written to " + Path.Combine(LogDir, "crash.log"),
                "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception) { }
    }

    /// <summary>Shows the launcher with a fresh settings store (each game window keeps its own) and returns what to play, or null when it was closed.</summary>
    static LaunchRequest? ShowLauncher()
    {
        var root = SettingsStore.DefaultRoot;
        var store = new SettingsStore(root);
        store.Load(null);
        using var form = new LauncherForm(store, root, Relaunched ? JustPlayedModule : null);
        Application.Run(form);
        return form.Result;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var opts = StartupOptions.Parse(args, AppLog);
            AppUpdates.OnStart(); // after an update: the notice only; the start marker waits for the launcher to be shown
            Smoke = opts.Smoke;
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetDefaultFont(new Font("Tahoma", 8.25f));
            ApplicationConfiguration.Initialize();
            OffscreenShots.InstallIfRequested(); // screenshot runs only: no window ever shows on the desktop
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Crash(ex);
            };
            LauncherHook ??= ShowLauncher;
            var smokeModule = opts.SmokeLauncher ? opts.ModulePath : null;
            if (opts.SmokeLauncher)
            {
                // Launcher smoke: the module path only seeds the library; the launcher is driven without dialogs.
                if (smokeModule == null || !opts.Smoke) { AppLog("--smoke-launcher needs a module path and --smoke N."); return 1; }
                opts = opts with { ModulePath = null };
                LauncherHook = LauncherShots.SmokeHook(smokeModule);
            }
            if (opts.SmokeOnline != null || opts.ScreenshotOnline != null)
            {
                OnlineSmoke.Run(opts);
                return ExitCode;
            }
            if (opts.ScreenshotLauncher != null || opts.ScreenshotOffline != null || AppUpdates.ScreenshotBarPath != null)
            {
                LauncherShots.Run(opts);
                return ExitCode;
            }
            var first = true;
            while (true)
            {
                LaunchRequest? launch = null;
                var o = opts;
                if (!first) o = opts with { ModulePath = null, SetupIndex = null, NewGame = false, RulesPath = null, NewRulesName = null };
                if ((o.SmokeLauncher || StartupDecision.For(o.ModulePath, o.Smoke) == StartupAction.Launcher) && LauncherHook is { } show)
                {
                    launch = show();
                    if (launch == null) break; // launcher closed: the app ends
                }
                first = false;
                var form = new MainForm(o, launch);
                Application.Run(form);
                var again = form.ReturnToLauncher;
                if (again) JustPlayedModule = form.OpenedModulePath ?? launch?.ModulePath;
                form.Dispose();
                if (!again) break;
                Relaunched = true;
            }
        }
        catch (Exception ex)
        {
            Crash(ex);
        }
        return ExitCode;
    }
}
