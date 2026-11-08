using System.Diagnostics;

namespace Quartermaster.Docs.Tests;

/// <summary>Runs build/publish-wiki.ps1 against local bare repos only (never github.com).</summary>
public class PublishWikiScriptTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pub-" + Guid.NewGuid().ToString("N"));

    public PublishWikiScriptTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_tmp, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (int Exit, string Output) Run(string exe, IEnumerable<string> args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp + err.Result);
    }

    private static string Git(string cwd, params string[] args)
    {
        var (code, output) = Run("git", args, cwd);
        Assert.True(code == 0, $"git {string.Join(' ', args)} failed: {output}");
        return output;
    }

    private static bool PowerShellAvailable()
    {
        try { return Run("powershell", new[] { "-NoProfile", "-Command", "exit 0" }).Exit == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private string NewBare(bool seed)
    {
        var bare = Path.Combine(_tmp, "remote-" + Guid.NewGuid().ToString("N")[..6] + ".git");
        Git(_tmp, "init", "--bare", "-b", "main", bare);
        if (seed)
        {
            var seedDir = Path.Combine(_tmp, "seed-" + Guid.NewGuid().ToString("N")[..6]);
            Git(_tmp, "init", "-b", "main", seedDir);
            File.WriteAllText(Path.Combine(seedDir, "Home.md"), "old home\n");
            File.WriteAllText(Path.Combine(seedDir, "Old.md"), "old page\n");
            Git(seedDir, "add", "-A");
            Git(seedDir, "-c", "user.name=t", "-c", "user.email=t@users.noreply.github.com", "commit", "-m", "seed");
            Git(seedDir, "push", bare, "main");
        }
        return bare;
    }

    private static string ScriptPath() => Path.Combine(DemoModuleTests.RepoRoot(), "build", "publish-wiki.ps1");

    private static (int Exit, string Output) Publish(string remote, params string[] flags)
    {
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), "-Remote", remote };
        args.AddRange(flags);
        return Run("powershell", args);
    }

    private static string Head(string bare) => Git(bare, "rev-parse", "HEAD").Trim();

    [Fact]
    public void Dry_run_with_pending_deletion_stops_with_exit_3()
    {
        if (!PowerShellAvailable()) return; // powershell.exe not available on this machine
        var bare = NewBare(seed: true);
        var before = Head(bare);
        var (exit, output) = Publish(bare, "-DryRun");
        Assert.True(exit == 3, output);
        Assert.Contains("Old.md", output);
        Assert.Contains("-AllowDelete", output);
        Assert.Equal(before, Head(bare));
    }

    [Fact]
    public void Allow_delete_dry_run_changes_nothing()
    {
        if (!PowerShellAvailable()) return;
        var bare = NewBare(seed: true);
        var before = Head(bare);
        var (exit, output) = Publish(bare, "-AllowDelete", "-DryRun");
        Assert.True(exit == 0, output);
        Assert.Contains("Dry run", output);
        Assert.Equal(before, Head(bare));
    }

    [Fact]
    public void Allow_delete_pushes_wiki_tree_without_demo()
    {
        if (!PowerShellAvailable()) return;
        var bare = NewBare(seed: true);
        var (exit, output) = Publish(bare, "-AllowDelete");
        Assert.True(exit == 0, output);

        var wiki = Path.Combine(DemoModuleTests.RepoRoot(), "docs", "wiki");
        var expected = Directory.GetFiles(wiki, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(wiki, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(r => !r.StartsWith("demo/", StringComparison.Ordinal))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        var actual = Git(bare, "ls-tree", "-r", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Old.md", actual);

        var author = Git(bare, "log", "-1", "--format=%ae").Trim();
        Assert.EndsWith("@users.noreply.github.com", author);
    }

    [Fact]
    public void Empty_or_missing_remote_reports_empty_wiki_with_exit_2()
    {
        if (!PowerShellAvailable()) return;
        var (exitEmpty, outEmpty) = Publish(NewBare(seed: false), "-DryRun");
        Assert.True(exitEmpty == 2, outEmpty);
        Assert.Contains("The wiki is empty", outEmpty);

        var (exitMissing, outMissing) = Publish(Path.Combine(_tmp, "nope.git"), "-DryRun");
        Assert.True(exitMissing == 2, outMissing);
        Assert.Contains("The wiki is empty", outMissing);
    }
}
