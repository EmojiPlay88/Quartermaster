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

    /// <summary>Puts <paramref name="savePath"/> first for this module, dropping a duplicate and anything beyond ten. Keys are matched ignoring case.</summary>
    public void Add(string sha, string savePath)
    {
        if (string.IsNullOrEmpty(sha) || string.IsNullOrWhiteSpace(savePath)) return;
        store.Update(s =>
        {
            var map = s.RecentSaves.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            var list = (map.TryGetValue(sha, out var old) ? old : Array.Empty<string>())
                .Where(p => !string.Equals(p, savePath, StringComparison.OrdinalIgnoreCase)).Prepend(savePath).Take(Max).ToList();
            map.Remove(sha);
            map[sha.ToLowerInvariant()] = list;
            return s with { RecentSaves = map.ToDictionary(p => p.Key, p => p.Value) };
        });
    }

    public IReadOnlyList<RecentSave> For(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return Array.Empty<RecentSave>();
        var found = store.Current.RecentSaves.FirstOrDefault(p => string.Equals(p.Key, sha, StringComparison.OrdinalIgnoreCase)).Value;
        if (found == null) return Array.Empty<RecentSave>();
        return found.Select(p =>
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

    /// <summary>Forward-only scan for snapshot.turn.{turn,phase} that stops as soon as that object ends. Any failure gives (0, null).</summary>
    static (int, string?) ReadTurn(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16384);
            var buf = new byte[16384];
            var len = 0;
            var state = new JsonReaderState();
            var eof = false;
            var depth = 0; // container depth: 1 = root, 2 = snapshot, 3 = turn
            var inSnapshot = false;
            var inTurn = false;
            var turn = -1;
            string? phase = null;
            string? prop = null;
            while (true)
            {
                if (!eof)
                {
                    if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                    var n = fs.Read(buf, len, buf.Length - len);
                    if (n == 0) eof = true; else len += n;
                }
                var reader = new Utf8JsonReader(buf.AsSpan(0, len), eof, state);
                var done = false;
                while (reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.PropertyName:
                            prop = reader.GetString();
                            break;
                        case JsonTokenType.StartObject:
                            depth++;
                            if (depth == 2 && Is(prop, "snapshot")) inSnapshot = true;
                            else if (depth == 3 && inSnapshot && Is(prop, "turn")) inTurn = true;
                            break;
                        case JsonTokenType.StartArray:
                            depth++;
                            break;
                        case JsonTokenType.EndArray:
                            depth--;
                            break;
                        case JsonTokenType.EndObject:
                            if (inTurn && depth == 3) done = true;
                            depth--;
                            break;
                        default:
                            if (inTurn && depth == 3)
                            {
                                if (Is(prop, "turn") && reader.TokenType == JsonTokenType.Number) turn = reader.GetInt32();
                                else if (Is(prop, "phase") && reader.TokenType == JsonTokenType.String) phase = reader.GetString();
                            }
                            break;
                    }
                    if (done) break;
                }
                if (done) return turn < 0 ? (0, null) : (turn, string.IsNullOrEmpty(phase) ? null : phase);
                if (eof) break;
                state = reader.CurrentState;
                var consumed = (int)reader.BytesConsumed;
                len -= consumed;
                Buffer.BlockCopy(buf, consumed, buf, 0, len);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return (0, null);
    }

    static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
