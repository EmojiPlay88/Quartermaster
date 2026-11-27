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

    [Fact]
    public void Wiki_mentions_runs_function()
    {
        var wiki = Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");
        Assert.Contains("### runs(\"formula-id\") or runs(\"formula-id\", \"period\")", Page);
        Assert.Contains("runs(\"f-bridge-repair\", \"turn\") < 1", File.ReadAllText(Path.Combine(wiki, "Making-a-Rules-Pack.md")));
        Assert.Contains("not run:", File.ReadAllText(Path.Combine(wiki, "Window-Phases.md")));
        Assert.Contains("Starting value", File.ReadAllText(Path.Combine(wiki, "Window-Rules-Workbench.md")));
    }

    [Fact]
    public void Wiki_mentions_layer_function()
    {
        var wiki = Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");
        Assert.Contains("### layer(\"name\")", Page);
        Assert.DoesNotContain("B3 wiki pass", Page);
        var making = File.ReadAllText(Path.Combine(wiki, "Making-a-Rules-Pack.md"));
        Assert.Contains("layer(\"Levels\")", making);
        Assert.Contains("## Acting on other pieces", making);
        var wb = File.ReadAllText(Path.Combine(wiki, "Window-Rules-Workbench.md"));
        Assert.Contains("Palette entry", wb);
        Assert.Contains("Same hex", wb);
        Assert.Contains("same hex", File.ReadAllText(Path.Combine(wiki, "Window-Formula-Dialog.md")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wiki_mentions_row_function()
    {
        var wiki = Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");
        Assert.Contains("### row()", Page);
        Assert.Contains("row() can only be used in a choices filter", Page);
        Assert.Contains("Not one of the choices for this piece", Page);
        var making = File.ReadAllText(Path.Combine(wiki, "Making-a-Rules-Pack.md"));
        Assert.Contains("## Group and repeated runs", making);
        Assert.Contains("### Rolling once for a group", making);
        Assert.Contains("### Choices from a table", making);
        Assert.Contains("rolls once for the group but has no dice", making);
        var dialog = File.ReadAllText(Path.Combine(wiki, "Window-Formula-Dialog.md"));
        Assert.Contains("### Several runs on one counter", dialog);
        Assert.Contains("## Running on several counters", dialog);
        Assert.Contains("Roll once for the group", File.ReadAllText(Path.Combine(wiki, "Window-Rules-Workbench.md")));
    }
    [Fact]
    public void Wiki_mentions_visibility_reveals_and_sealed_answers()
    {
        var wiki = Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");
        string Read(string n) => File.ReadAllText(Path.Combine(wiki, n));
        Assert.Contains("### askside()", Page);
        Assert.Contains("#### Reveal and Margin", Page);
        Assert.Contains("The margin for Strength must be a whole number from 0 to 1000", Page);
        var making = Read("Making-a-Rules-Pack.md");
        foreach (var h in new[] { "## Visibility, reveals and sealed answers", "### Public values of a side's holder", "### Side alerts",
                     "### Revealing a value", "### Sealed answers", "### When the game ends", "### What the Problems list says for visibility" })
            Assert.Contains(h, making);
        foreach (var m in new[] { "output {n} reveals no property", "output {n} margin must be a whole number from 0 to 1000",
                     "start entry {n} is sealed, but formula \"{f}\" rolls dice", "start entry {n} is sealed, but the pack has fewer than two sides" })
            Assert.Contains(m, making);
        Assert.Contains("### Sealed answers", Read("Window-Phases.md"));
        Assert.Contains("Limits the alert to one side's units; only players holding that side see it, in every phase", Read("Window-Rules-Workbench.md"));
        Assert.Contains("Your sealed answer is already in.", Read("Playing-With-Others.md"));
        Assert.Contains("hidden", Read("Window-Formula-Dialog.md"));
    }
}
