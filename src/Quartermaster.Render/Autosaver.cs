using System.Globalization;

namespace Quartermaster.Render;

/// <summary>
/// Timed autosave policy: writes <c>&lt;root&gt;/autosave/&lt;module sha&gt;/autosave-yyyyMMdd-HHmmss.qsave</c> (UTC) through <c>saveTo</c> when the game
/// is dirty and the interval has elapsed since the last save (or since the game was last seen clean), then keeps the newest 10. Never throws.
/// </summary>
public sealed class Autosaver(Func<string?> moduleSha, Action<string> saveTo, string root, Action<string>? onError = null)
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
        try
        {
            if (moduleSha() is not { Length: > 0 } sha) return null;
            var dir = Path.Combine(root, "autosave", sha);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "autosave-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".qsave");
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

    /// <summary>Deletes all but the newest <paramref name="keep"/> autosave files (by name, which sorts by time). Other files are left alone.</summary>
    public static void Prune(string dir, int keep = 10)
    {
        try
        {
            var old = Directory.GetFiles(dir, "autosave-*.qsave")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));
            foreach (var f in old)
            {
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* missing directory etc. */ }
    }
}
