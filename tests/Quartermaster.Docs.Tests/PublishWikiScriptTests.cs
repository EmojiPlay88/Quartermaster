using System.Diagnostics;

namespace Quartermaster.Docs.Tests;

/// <summary>
/// A fact for build/publish-wiki.ps1, a Windows maintainer script run with Windows PowerShell (powershell.exe). Skipped, with the reason
/// shown, anywhere else: off Windows, or when powershell.exe is not on PATH.
/// </summary>
public sealed class WindowsPowerShellFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> SkipReason = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return "publish-wiki.ps1 is a Windows maintainer script (Windows PowerShell); not run on this OS.";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"exit 0\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit();
            return p.ExitCode == 0 ? null : "powershell.exe did not start cleanly.";
        }
        catch (System.ComponentModel.Win32Exception) { return "powershell.exe is not on PATH."; }
    });

    public WindowsPowerShellFactAttribute()
    {
        if (SkipReason.Value is { } reason) Skip = reason;
    }
}

/// <summary>
/// Runs build/publish-wiki.ps1 against local bare repos only (never github.com). The script takes its commit identity from git config;
/// the tests hand it a test noreply identity through GIT_CONFIG_COUNT (git's command-line config scope, which wins over every config file),
/// so they do not depend on the machine's own git identity and never change the script's noreply check.
/// </summary>
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

    private const string TestEmail = "qm-test@users.noreply.github.com";

    private static (int Exit, string Output) Run(string exe, IEnumerable<string> args, string? cwd = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd ?? Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
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

    /// <summary>git config as the command-line scope (git 2.31+): read by the script's <c>git config user.email</c> ahead of any file.</summary>
    private static Dictionary<string, string> Identity(string email) => new()
    {
        ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "user.name", ["GIT_CONFIG_VALUE_0"] = "Quartermaster test",
        ["GIT_CONFIG_KEY_1"] = "user.email", ["GIT_CONFIG_VALUE_1"] = email,
    };

    private static (int Exit, string Output) Publish(string remote, params string[] flags) => PublishAs(TestEmail, remote, flags);

    private static (int Exit, string Output) PublishAs(string email, string remote, params string[] flags)
    {
        var args = new List<string> { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), "-Remote", remote };
        args.AddRange(flags);
        return Run("powershell", args, env: Identity(email));
    }

    private static string Head(string bare) => Git(bare, "rev-parse", "HEAD").Trim();

    [WindowsPowerShellFact]
    public void Dry_run_with_pending_deletion_stops_with_exit_3()
    {
        var bare = NewBare(seed: true);
        var before = Head(bare);
        var (exit, output) = Publish(bare, "-DryRun");
        Assert.True(exit == 3, output);
        Assert.Contains("Old.md", output);
        Assert.Contains("-AllowDelete", output);
        Assert.Equal(before, Head(bare));
    }

    [WindowsPowerShellFact]
    public void Allow_delete_dry_run_changes_nothing()
    {
        var bare = NewBare(seed: true);
        var before = Head(bare);
        var (exit, output) = Publish(bare, "-AllowDelete", "-DryRun");
        Assert.True(exit == 0, output);
        Assert.Contains("Dry run", output);
        Assert.Equal(before, Head(bare));
    }

    [WindowsPowerShellFact]
    public void Allow_delete_pushes_wiki_tree_without_demo()
    {
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
        Assert.Equal(TestEmail, author);
    }

    [WindowsPowerShellFact]
    public void Identity_that_is_not_noreply_is_refused_and_nothing_is_pushed()
    {
        var bare = NewBare(seed: true);
        var before = Head(bare);
        var (exit, output) = PublishAs("someone@example.com", bare, "-AllowDelete");
        Assert.True(exit == 1, output);
        Assert.Contains("Refusing to publish", output);
        Assert.Equal(before, Head(bare));
    }

    [WindowsPowerShellFact]
    public void Empty_or_missing_remote_reports_empty_wiki_with_exit_2()
    {
        var (exitEmpty, outEmpty) = Publish(NewBare(seed: false), "-DryRun");
        Assert.True(exitEmpty == 2, outEmpty);
        Assert.Contains("The wiki is empty", outEmpty);

        var (exitMissing, outMissing) = Publish(Path.Combine(_tmp, "nope.git"), "-DryRun");
        Assert.True(exitMissing == 2, outMissing);
        Assert.Contains("The wiki is empty", outMissing);
    }
}
