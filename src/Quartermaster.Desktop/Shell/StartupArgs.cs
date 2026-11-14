using System.Globalization;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The Desktop command line: <c>[&lt;vmod&gt;] [--setup &lt;index&gt;|--new] [--rules &lt;path&gt;|--new-rules &lt;name&gt;]</c>, parsed with the WinForms StartupOptions rules and warning
/// texts (unknown options and extra arguments are ignored with a warning). Later stages add their options here.
/// </summary>
public sealed record StartupArgs(string? ModulePath, int? SetupIndex, bool NewGame, string? ScreenshotPath = null, string? RulesPath = null, string? NewRulesName = null, string? ScreenshotState = null,
    string? UpdatedFrom = null, string? UpdateFailed = null, string? UpdateRefused = null)
{
    public static StartupArgs Parse(IReadOnlyList<string> args, Action<string> warn)
    {
        string? path = null;
        int? setup = null;
        var newGame = false;
        string? shot = null, rules = null, newRules = null, state = null, updatedFrom = null, updateFailed = null, updateRefused = null;
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
                case "--screenshot-state":
                    if (i + 1 < args.Count) state = args[++i];
                    else warn("--screenshot-state needs a state name; ignored.");
                    break;
                case "--updated-from":
                    if (i + 1 < args.Count) updatedFrom = args[++i];
                    else warn("--updated-from needs a version; ignored.");
                    break;
                case "--update-failed":
                    if (i + 1 < args.Count) updateFailed = args[++i];
                    else warn("--update-failed needs a version; ignored.");
                    break;
                case "--update-refused":
                    if (i + 1 < args.Count) updateRefused = args[++i];
                    else warn("--update-refused needs a version; ignored.");
                    break;
                case "--rules":
                    if (i + 1 < args.Count) rules = args[++i];
                    else warn("--rules needs a path; ignored.");
                    break;
                case "--new-rules":
                    if (i + 1 < args.Count) newRules = args[++i];
                    else warn("--new-rules needs a name; ignored.");
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
        return new StartupArgs(path, setup, newGame, shot, rules, newRules, state, updatedFrom, updateFailed, updateRefused);
    }
}
