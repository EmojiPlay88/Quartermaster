using System.Text.RegularExpressions;
using Quartermaster.DemoModule;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Docs.Tests;

/// <summary>The Formula Reference page documents exactly the functions and operators the formula language really has.</summary>
public class FormulaReferenceTests
{
    private static string Page => File.ReadAllText(Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki", "Formula-Reference.md"));

    private static HashSet<string> DocumentedFunctions(string text) =>
        Regex.Matches(text, @"^###[ \t]+(?<n>[A-Za-z_][A-Za-z0-9_]*)\(", RegexOptions.Multiline)
            .Select(m => m.Groups["n"].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>First-cell backticked operators of the table under the "## Operators" heading.</summary>
    private static HashSet<string> DocumentedOperators(string text)
    {
        var m = Regex.Match(text, @"^## Operators[ \t]*\r?\n(?<body>.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(m.Success, "No '## Operators' section found");
        return Regex.Matches(m.Groups["body"].Value, @"^\|[ \t]*`(?<op>[^`]+)`[ \t]*\|", RegexOptions.Multiline)
            .Select(x => x.Groups["op"].Value).ToHashSet(StringComparer.Ordinal);
    }

    private static string Diff(string kind, IEnumerable<string> expected, HashSet<string> actual)
    {
        var exp = expected.ToHashSet(StringComparer.Ordinal);
        var missing = exp.Except(actual).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var extra = actual.Except(exp).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return missing.Count == 0 && extra.Count == 0 ? ""
            : $"{kind}: missing from page [{string.Join(", ", missing)}]; on page but not in the language [{string.Join(", ", extra)}]. ";
    }

    [Fact]
    public void Reference_documents_exactly_the_registry()
    {
        var text = Page;
        var problems = Diff("functions", Evaluator.FunctionNames, DocumentedFunctions(text))
                     + Diff("operators", Evaluator.OperatorNames, DocumentedOperators(text));
        Assert.True(problems.Length == 0, problems);
    }
}
