using System.Text.RegularExpressions;

namespace Quartermaster.Render;

/// <summary>Sizes and clearing of the per-module caches (<c>&lt;root&gt;/&lt;module sha256&gt;/tiles</c> and <c>/docs</c>). Only 64-hex folders directly under the root count as module caches; links are never followed.</summary>
public static class CacheFolders
{
    static readonly Regex Sha = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    static IEnumerable<string> ModuleDirs(string root)
    {
        foreach (var d in Directory.EnumerateDirectories(root))
        {
            if (!Sha.IsMatch(Path.GetFileName(d))) continue;
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
            yield return d;
        }
    }

    public static (long Tiles, long Docs) Sizes(string root)
    {
        long tiles = 0, docs = 0;
        try
        {
            foreach (var m in ModuleDirs(root))
            {
                tiles += Size(Path.Combine(m, "tiles"));
                docs += Size(Path.Combine(m, "docs"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (tiles, docs);
    }

    static long Size(string dir)
    {
        long n = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { n += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return n;
    }

    static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

    /// <summary>Deletes <paramref name="kind"/> ("tiles" or "docs") under every module cache except <paramref name="openModuleDir"/>. Returns bytes freed and whether the open module was skipped.</summary>
    public static (long Freed, bool SkippedOpen) Clear(string root, string kind, string? openModuleDir)
    {
        long freed = 0;
        var skipped = false;
        if (kind is not ("tiles" or "docs")) return (0, false);
        try
        {
            foreach (var m in ModuleDirs(root))
            {
                var sub = Path.Combine(m, kind);
                if (!Directory.Exists(sub)) continue;
                if (openModuleDir != null && string.Equals(Norm(m), Norm(openModuleDir), StringComparison.OrdinalIgnoreCase))
                {
                    skipped = true;
                    continue;
                }
                var size = Size(sub);
                try { Directory.Delete(sub, true); freed += size; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use elsewhere: keep */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (freed, skipped);
    }
}
