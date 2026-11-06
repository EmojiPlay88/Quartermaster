using System.Globalization;

namespace Quartermaster.App;

/// <summary>Command line: [&lt;vmod path&gt;] [--setup &lt;index&gt;|--new] [--smoke &lt;seconds&gt;] [--screenshot &lt;png&gt;] [--float-main-map] [--screenshot-float &lt;png&gt;] [--rules &lt;path&gt;|--new-rules &lt;name&gt;] [--screenshot-formula &lt;png&gt;] [--screenshot-ledger &lt;png&gt;] [--screenshot-workbench &lt;png&gt;].</summary>
sealed record StartupOptions(string? ModulePath, int? SetupIndex, bool NewGame, int? SmokeSeconds, string? Screenshot = null, bool FloatMainMap = false, string? ScreenshotFloat = null,
    string? RulesPath = null, string? NewRulesName = null, string? ScreenshotFormula = null, string? ScreenshotLedger = null, string? ScreenshotWorkbench = null)
{
    public bool Smoke => SmokeSeconds != null;

    public static StartupOptions Parse(string[] args, Action<string> warn)
    {
        string? path = null;
        int? setup = null, smoke = null;
        var newGame = false;
        string? shot = null, shotFloat = null, rules = null, newRules = null, shotFormula = null, shotLedger = null, shotBench = null;
        var floatMain = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--new": newGame = true; break;
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
                case "--screenshot-workbench":
                    if (i + 1 < args.Length) shotBench = args[++i];
                    else warn("--screenshot-workbench needs a file path; ignored.");
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) warn($"Unknown option '{a}'; ignored.");
                    else if (path == null) path = a;
                    else warn($"Extra argument '{a}'; ignored.");
                    break;
            }
        }
        return new StartupOptions(path, setup, newGame, smoke, shot, floatMain, shotFloat, rules, newRules, shotFormula, shotLedger, shotBench);
    }
}

static class Program
{
    public static bool Smoke { get; private set; }
    public static int ExitCode { get; set; }

    static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");

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

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            var opts = StartupOptions.Parse(args, AppLog);
            Smoke = opts.Smoke;
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetDefaultFont(new Font("Tahoma", 8.25f));
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Crash(ex);
            };
            Application.Run(new MainForm(opts));
        }
        catch (Exception ex)
        {
            Crash(ex);
        }
        return ExitCode;
    }
}
