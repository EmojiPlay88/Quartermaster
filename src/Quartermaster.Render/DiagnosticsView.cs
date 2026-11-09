using System.Text;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public enum DiagnosticsFilter { All, WarningsAndErrors, ErrorsOnly }

/// <summary>UI-free rules of the diagnostics window and the status-bar link to it.</summary>
public static class DiagnosticsView
{
    /// <summary>The combo's items, in order.</summary>
    public static readonly IReadOnlyList<string> FilterNames = new[] { "All", "Warnings and errors", "Errors only" };

    public static DiagnosticsFilter FilterFor(int comboIndex) => comboIndex switch
    {
        1 => DiagnosticsFilter.WarningsAndErrors,
        2 => DiagnosticsFilter.ErrorsOnly,
        _ => DiagnosticsFilter.All,
    };

    public static bool Passes(Diagnostic d, DiagnosticsFilter f) => (int)d.Severity >= f switch
    {
        DiagnosticsFilter.WarningsAndErrors => (int)DiagnosticSeverity.Warning,
        DiagnosticsFilter.ErrorsOnly => (int)DiagnosticSeverity.Error,
        _ => 0,
    };

    public static IReadOnlyList<Diagnostic> Apply(IReadOnlyList<Diagnostic> all, DiagnosticsFilter f) => all.Where(d => Passes(d, f)).ToList();

    /// <summary>One tab-separated line per entry (severity, category, message, context); a single space when empty (the clipboard refuses empty text).</summary>
    public static string CopyText(IReadOnlyList<Diagnostic> shown)
    {
        var sb = new StringBuilder();
        foreach (var d in shown) sb.AppendLine(string.Join('\t', d.Severity.ToString(), d.Category, d.Message, d.Context ?? ""));
        return sb.Length == 0 ? " " : sb.ToString();
    }

    /// <summary>The status-bar link: empty when nothing is wrong.</summary>
    public static string StatusLinkText(int warnings, int errors)
    {
        var n = warnings + errors;
        return n > 0 ? $"{n} warning(s)/error(s) - click here to view diagnostics" : "";
    }
}
