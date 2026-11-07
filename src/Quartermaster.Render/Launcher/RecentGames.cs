using System.Text.Json;

namespace Quartermaster.Render;

/// <summary>A recently used game file.</summary>
public sealed record RecentSave(string Path, bool Missing, DateTimeOffset LastWrite);

/// <summary>The newest autosave of a module with the turn and phase it was taken at (turn 0 when unreadable).</summary>
public sealed record ContinueInfo(string Path, DateTimeOffset At, int Turn, string? Phase);

/// <summary>Per-module recent saves (kept in <see cref="Settings.RecentSaves"/>, max 10) and the "Continue" autosave lookup. Never throws.</summary>
public sealed class RecentGames(SettingsStore store, string layoutRoot)
{
    public const int Max = 10;

    /// <summary>Puts <paramref name="savePath"/> first for this module, dropping a duplicate and anything beyond ten.</summary>
    public void Add(string sha, string savePath)
    {
        if (string.IsNullOrEmpty(sha) || string.IsNullOrWhiteSpace(savePath)) return;
        var s = store.Current;
        var map = s.RecentSaves.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var list = (map.TryGetValue(sha, out var old) ? old : Array.Empty<string>())
            .Where(p => !string.Equals(p, savePath, StringComparison.OrdinalIgnoreCase)).Prepend(savePath).Take(Max).ToList();
        map[sha] = list;
        store.Save(s with { RecentSaves = map.ToDictionary(p => p.Key, p => p.Value) });
    }

    public IReadOnlyList<RecentSave> For(string sha)
    {
        if (!store.Current.RecentSaves.TryGetValue(sha, out var list)) return Array.Empty<RecentSave>();
        return list.Select(p =>
        {
            try
            {
                var fi = new FileInfo(p);
                return fi.Exists ? new RecentSave(p, false, new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero)) : new RecentSave(p, true, DateTimeOffset.MinValue);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new RecentSave(p, true, DateTimeOffset.MinValue); }
        }).ToList();
    }

    /// <summary>The newest autosave under <c>&lt;layoutRoot&gt;/autosave/&lt;sha&gt;</c>, or null when there is none.</summary>
    public ContinueInfo? Continue(string sha)
    {
        try
        {
            if (string.IsNullOrEmpty(sha) || sha.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var dir = Path.Combine(Autosaver.AutosaveRoot(layoutRoot), sha);
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).EnumerateFiles("autosave-*.qsave")
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
            if (newest == null) return null;
            var (turn, phase) = ReadTurn(newest.FullName);
            return new ContinueInfo(newest.FullName, new DateTimeOffset(newest.LastWriteTimeUtc, TimeSpan.Zero), turn, phase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    static (int, string?) ReadTurn(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("snapshot", out var snap) && snap.TryGetProperty("turn", out var t)
                && t.TryGetProperty("turn", out var n) && n.TryGetInt32(out var turn))
            {
                string? phase = t.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                return (turn, string.IsNullOrEmpty(phase) ? null : phase);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return (0, null);
    }
}
