using System.Text.RegularExpressions;
using Quartermaster.DemoModule;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Docs.Tests;

/// <summary>Every ```formula block in the wiki parses with the real parser; the walkthrough's blocks are copied from the demo pack.</summary>
public class WalkthroughFormulaTests
{
    private static string WikiDir => Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");

    private static readonly Regex BlockRx = new(@"^```formula[ \t]*\r?\n(?<body>.*?)\r?\n```[ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    private static IEnumerable<(string Page, string Body)> Blocks(string? onlyPage = null)
    {
        foreach (var page in Directory.GetFiles(WikiDir, "*.md"))
        {
            var name = Path.GetFileName(page);
            if (onlyPage != null && name != onlyPage) continue;
            foreach (Match m in BlockRx.Matches(File.ReadAllText(page)))
                yield return (name, m.Groups["body"].Value.Trim());
        }
    }

    [Fact]
    public void Walkthrough_formulas_parse()
    {
        var blocks = Blocks().ToList();
        Assert.Contains(blocks, b => b.Page == "Making-a-Rules-Pack.md");
        var problems = new List<string>();
        foreach (var (page, body) in blocks)
        {
            if (body.Length == 0) { problems.Add($"{page}: empty formula block"); continue; }
            try { Parser.Parse(body); }
            catch (ParseError e) { problems.Add($"{page}: '{body}': {e.Message}"); }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Every expression the demo pack holds: formula texts, applies-to filters, table row/column expressions, side filters, alert and warning conditions.</summary>
    private static HashSet<string> DemoExpressions()
    {
        var p = DemoContent.Pack("0");
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim()); }
        foreach (var f in p.Formulas)
        {
            Add(f.Text); Add(f.AppliesTo);
            foreach (var b in f.Bindings) { Add(b.RowExpr); Add(b.ColExpr); Add(b.AutoValue); }
        }
        foreach (var x in p.Properties) Add(x.AppliesTo);
        foreach (var s in p.Sides) Add(s.UnitFilter);
        foreach (var a in p.Alerts) Add(a.Condition);
        foreach (var ph in p.Phases)
        {
            foreach (var w in ph.Warnings) Add(w.Condition);
            foreach (var e in ph.OnStart) Add(e.UnitFilter);
            foreach (var e in ph.OnMove) Add(e.UnitFilter);
        }
        return set;
    }

    [Fact]
    public void Walkthrough_formulas_come_from_the_demo_pack()
    {
        var known = DemoExpressions();
        var missing = Blocks("Making-a-Rules-Pack.md").Select(b => b.Body).Where(b => !known.Contains(b)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Not in the demo pack: " + string.Join(" | ", missing));
    }
}
