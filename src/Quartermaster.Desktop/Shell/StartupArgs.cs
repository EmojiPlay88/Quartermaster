using System.Globalization;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The Desktop command line: <c>[&lt;vmod&gt;] [--setup &lt;index&gt;|--new]</c>, parsed with the WinForms StartupOptions rules and warning
/// texts (unknown options and extra arguments are ignored with a warning). Later stages add their options here.
/// </summary>
public sealed record StartupArgs(string? ModulePath, int? SetupIndex, bool NewGame, string? ScreenshotPath = null)
{
    public static StartupArgs Parse(IReadOnlyList<string> args, Action<string> warn)
    {
        string? path = null;
        int? setup = null;
        var newGame = false;
        string? shot = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--new": newGame = true; break;
                case "--screenshot-main":
                    if (i + 1 < args.Count) { shot = args[i + 1]; i++; }
                    else warn($"{a} needs a file name; ignored.");
                    break;
                case "--setup":
                    if (i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { setup = n; i++; }
                    else warn($"{a} needs a non-negative integer; ignored.");
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) warn($"Unknown option '{a}'; ignored.");
                    else if (path == null) path = a;
                    else warn($"Extra argument '{a}'; ignored.");
                    break;
            }
        }
        return new StartupArgs(path, setup, newGame, shot);
    }
}
