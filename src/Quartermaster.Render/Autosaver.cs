using System.Globalization;
using System.Text.RegularExpressions;

namespace Quartermaster.Render;

/// <summary>
/// Timed autosave policy: writes <c>&lt;root&gt;/autosave/&lt;module sha&gt;/autosave-yyyyMMdd-HHmmssfff[-t{turn}].qsave</c> (UTC) through <c>saveTo</c> when the game
/// is dirty and the interval has elapsed since the last save (or since the game was last seen clean), then keeps the newest 20 plus the first save of each turn. Never throws.
/// </summary>
public sealed class Autosaver(Func<string?> moduleSha, Action<string> saveTo, string root, Action<string>? onError = null, Func<int?>? turn = null)
{
    DateTimeOffset? _last;

    /// <summary>The user saved by hand: the interval starts over.</summary>
    public void Reset(DateTimeOffset now) => _last = now;

    /// <returns>The written path, or null when nothing was saved.</returns>
    public string? Tick(DateTimeOffset now, int intervalMinutes, bool dirty)
    {
        if (intervalMinutes <= 0) { _last = now; return null; } // off: keep the clock moving so enabling it later does not fire at once
        if (!dirty || _last == null)
        {
            _last = now; // the interval counts from the last save, or from when the game was last seen clean
            return null;
        }
        if (now - _last < TimeSpan.FromMinutes(intervalMinutes)) return null;
        _last = now; // also after a failure, so a broken disk is retried once per interval, not every tick
        return Write(now);
    }

    /// <summary>Writes an autosave right away (phase boundary) unless autosave is off, prunes, and restarts the interval. Never throws.</summary>
    public string? SaveNow(DateTimeOffset now, int intervalMinutes)
    {
        _last = now;
        return intervalMinutes <= 0 ? null : Write(now);
    }

    /// <summary>Writes right away even when autosave is off (before a destructive action), prunes, and restarts the interval. Never throws.</summary>
    public string? SaveForced(DateTimeOffset now)
    {
        _last = now;
        return Write(now);
    }

    string? Write(DateTimeOffset now)
    {
        try
        {
            if (moduleSha() is not { Length: > 0 } sha) return null;
            var dir = Path.Combine(root, "autosave", sha);
            Directory.CreateDirectory(dir);
            int? t = null;
            try { t = turn?.Invoke(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
            var stem = "autosave-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                + (t is { } n ? "-t" + n.ToString(CultureInfo.InvariantCulture) : "");
            var path = Path.Combine(dir, stem + ".qsave");
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(dir, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".qsave");
            saveTo(path);
            Prune(dir);
            return path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { onError?.Invoke("Autosave failed: " + ex.Message); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> lies inside <c>&lt;root&gt;/autosave</c> (where pruning may delete files). Never throws.</summary>
    public static bool IsAutosavePath(string path, string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            var dir = Path.GetFullPath(Path.Combine(root, "autosave")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException) { return false; }
    }

    /// <summary>The folder holding autosaves of all modules.</summary>
    public static string AutosaveRoot(string root) => Path.Combine(root, "autosave");

    static readonly Regex TurnTag = new(@"-t(\d+)(?:-\d+)?\.qsave$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Deletes autosave files except the newest <paramref name="keepRecent"/> (by name, which sorts by time) and, per turn number in a <c>-t{n}</c> tag,
    /// the oldest file of that turn. Old-style names count only toward the recent set. Other files are left alone.
    /// </summary>
    public static void Prune(string dir, int keepRecent = 20)
    {
        try
        {
            // Order on the name without extension so "...-t3" sorts before its collision twin "...-t3-2".
            var files = Directory.GetFiles(dir, "autosave-*.qsave")
                .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal).ToList();
            var keep = new HashSet<string>(files.Skip(Math.Max(0, files.Count - Math.Max(0, keepRecent))));
            var seen = new HashSet<string>();
            foreach (var f in files) // oldest first
                if (TurnTag.Match(Path.GetFileName(f)) is { Success: true } m && seen.Add(m.Groups[1].Value.TrimStart('0') + "|")) keep.Add(f);
            foreach (var f in files.Where(f => !keep.Contains(f)))
            {
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* missing directory etc. */ }
    }
}
