using System.Text.RegularExpressions;

namespace Quartermaster.Docs.Tests;

/// <summary>Lint over every top-level docs/wiki/*.md page: links, images, banned content, sidebar coverage.</summary>
public class WikiLintTests
{
    private static string WikiDir => Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");

    private static string[] Pages() => Directory.GetFiles(WikiDir, "*.md");

    private static readonly Regex LinkRx = new(@"(?<bang>!?)\[(?<text>[^\]]*)\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);

    private static IEnumerable<(string Page, string Target, bool IsImage)> AllLinks()
    {
        foreach (var page in Pages())
        {
            var text = File.ReadAllText(page);
            foreach (Match m in LinkRx.Matches(text))
                yield return (Path.GetFileName(page), m.Groups["target"].Value, m.Groups["bang"].Value == "!");
        }
    }

    private static bool IsExternal(string t) =>
        t.StartsWith("#") || Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9+.-]*:");

    private static string StripAnchor(string t)
    {
        var i = t.IndexOf('#');
        return i >= 0 ? t[..i] : t;
    }

    [Fact]
    public void Wiki_has_pages()
    {
        Assert.NotEmpty(Pages());
        Assert.Contains(Pages(), p => Path.GetFileName(p) == "Home.md");
        Assert.Contains(Pages(), p => Path.GetFileName(p) == "_Sidebar.md");
    }

    [Fact]
    public void Links_use_wiki_form_and_resolve()
    {
        var names = Pages().Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (page, rawTarget, isImage) in AllLinks())
        {
            if (isImage || IsExternal(rawTarget)) continue;
            var target = StripAnchor(rawTarget);
            if (target.Length == 0) continue;
            if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{page}: link '{rawTarget}' must not end in .md");
            else if (!names.Contains(target))
                problems.Add($"{page}: link '{rawTarget}' does not match any page file name");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Referenced images under images/ (relative names) plus any problems with the references themselves.</summary>
    private static (HashSet<string> Referenced, List<string> Problems) ImageReferences()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (page, target, isImage) in AllLinks())
        {
            if (!isImage || IsExternal(target)) continue;
            var t = StripAnchor(target);
            if (!t.StartsWith("images/", StringComparison.Ordinal))
            {
                problems.Add($"{page}: image '{target}' must live under images/");
                continue;
            }
            referenced.Add(t["images/".Length..]);
            if (!File.Exists(Path.Combine(WikiDir, t.Replace('/', Path.DirectorySeparatorChar))))
                problems.Add($"{page}: image '{target}' does not exist");
        }
        return (referenced, problems);
    }

    [Fact]
    public void Images_exist()
    {
        var (_, problems) = ImageReferences();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Images_are_referenced()
    {
        var imagesDir = Path.Combine(WikiDir, "images");
        var (referenced, _) = ImageReferences();
        var problems = new List<string>();
        if (Directory.Exists(imagesDir))
            foreach (var f in Directory.GetFiles(imagesDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(imagesDir, f).Replace(Path.DirectorySeparatorChar, '/');
                if (!referenced.Contains(rel)) problems.Add($"images/{rel} is not referenced by any page");
            }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static readonly (string Name, Regex Rx)[] Banned =
    {
        ("personal Windows path", new Regex(@"[A-Za-z]:\\Users\\(?!<name>|Public)", RegexOptions.Compiled)),
        ("personal unix path", new Regex(@"/home/[a-z]", RegexOptions.Compiled)),
        ("email domain", new Regex(@"@gmail", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("user handle", new Regex(@"emojiplay105", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("real-game name CNA", new Regex(@"\bCNA\b", RegexOptions.Compiled)),
        ("real-game name", new Regex(@"Campaign for North Africa", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    [Fact]
    public void No_banned_content()
    {
        var files = Pages().Append(Path.Combine(WikiDir, "demo", "README.md")).Where(File.Exists);
        var problems = new List<string>();
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                foreach (var (name, rx) in Banned)
                    if (rx.IsMatch(lines[i]))
                        problems.Add($"{Path.GetRelativePath(WikiDir, f)}:{i + 1}: banned {name}");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_page_in_sidebar()
    {
        var sidebar = File.ReadAllText(Path.Combine(WikiDir, "_Sidebar.md"));
        var linked = LinkRx.Matches(sidebar)
            .Where(m => m.Groups["bang"].Value != "!")
            .Select(m => StripAnchor(m.Groups["target"].Value))
            .ToHashSet(StringComparer.Ordinal);
        var missing = Pages().Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => n != "_Sidebar" && !linked.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Pages missing from _Sidebar: " + string.Join(", ", missing));
    }
}
