using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>One module the launcher knows. <see cref="Sha256"/> is null until the module was hashed; Size and LastWriteUtc are what it was hashed at.</summary>
public sealed record ModuleEntry(string Path, string? Sha256, long Size, DateTime LastWriteUtc, string Title, string Version, DateTimeOffset? LastPlayed);

/// <summary>An entry plus whether its file is gone.</summary>
public sealed record ModuleRow(ModuleEntry Entry, bool Missing);

/// <summary>
/// The launcher's module list, persisted in <see cref="Settings.Modules"/>. Modules are identified by SHA-256; the hash is cached with the
/// file's size and last-write time and recomputed (off the UI thread) only when either changes. Never throws on missing or unreadable files.
/// </summary>
public sealed class ModuleLibrary(SettingsStore store)
{
    /// <summary>Computes a module's SHA-256; replaceable for tests.</summary>
    public Func<string, string> Hasher { get; set; } = VmodArchive.ComputeSha256;

    /// <summary>All modules, most recently played first, then by title.</summary>
    public IReadOnlyList<ModuleRow> Rows
    {
        get
        {
            ModuleEntry[] list;
            list = store.Current.Modules.ToArray();
            return list.OrderByDescending(e => e.LastPlayed ?? DateTimeOffset.MinValue)
                .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ModuleRow(e, !SafeExists(e.Path))).ToList();
        }
    }

    static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    static string Full(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void Mutate(Func<List<ModuleEntry>, List<ModuleEntry>> change)
        => store.Update(cur => cur with { Modules = change(cur.Modules.ToList()) });

    /// <summary>Adds a module (no-op when already listed). Reads its title and version; hashing happens in <see cref="RefreshAsync"/>.</summary>
    public void Add(string path)
    {
        path = Full(path);
        if (store.Current.Modules.Any(m => Same(m.Path, path))) return;
        var info = ModuleInfo.Read(path);
        long size = 0;
        var written = DateTime.MinValue;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) { size = fi.Length; written = fi.LastWriteTimeUtc; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        Mutate(l => { if (!l.Any(m => Same(m.Path, path))) l.Add(new ModuleEntry(path, null, size, written, info.Title, info.Version, null)); return l; });
    }

    /// <summary>Takes the module off the list; the file is left alone.</summary>
    public void Remove(string path) => Mutate(l => { l.RemoveAll(m => Same(m.Path, path)); return l; });

    public void MarkPlayed(string path, DateTimeOffset when) =>
        Mutate(l =>
        {
            for (var i = 0; i < l.Count; i++) if (Same(l[i].Path, path)) l[i] = l[i] with { LastPlayed = when };
            return l;
        });

    /// <summary>
    /// Re-hashes the modules whose file size or last-write time differ from what was recorded (and those never hashed), on a background
    /// thread, reporting each visited row. Missing files are left as they are.
    /// </summary>
    public async Task RefreshAsync(IProgress<ModuleRow>? progress, CancellationToken ct)
    {
        ModuleEntry[] snapshot;
        snapshot = store.Current.Modules.ToArray();
        var results = await Task.Run(() =>
        {
            var done = new List<(ModuleEntry Old, ModuleEntry New)>();
            foreach (var e in snapshot)
            {
                ct.ThrowIfCancellationRequested();
                var now = Check(e);
                if (now != e) done.Add((e, now));
                progress?.Report(new ModuleRow(now, !SafeExists(now.Path)));
            }
            return done;
        }, ct).ConfigureAwait(false);
        if (results.Count == 0) return;
        ct.ThrowIfCancellationRequested(); // a cancelled refresh never writes settings
        Mutate(l =>
        {
            foreach (var (old, now) in results)
            {
                var i = l.FindIndex(m => Same(m.Path, old.Path));
                if (i >= 0) l[i] = now with { LastPlayed = l[i].LastPlayed };
            }
            return l;
        });
    }

    ModuleEntry Check(ModuleEntry e)
    {
        try
        {
            var fi = new FileInfo(e.Path);
            if (!fi.Exists) return e;
            var written = fi.LastWriteTimeUtc;
            if (e.Sha256 != null && e.Size == fi.Length && e.LastWriteUtc == written) return e;
            var sha = Hasher(e.Path);
            var info = ModuleInfo.Read(e.Path);
            return e with { Sha256 = sha, Size = fi.Length, LastWriteUtc = written, Title = info.Title, Version = info.Version };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return e; }
    }

    /// <summary>The rules pack remembered for this module (by SHA), or null.</summary>
    public string? RulesPackFor(ModuleEntry entry) =>
        entry.Sha256 is { Length: > 0 } sha ? RulesMemory.LastFor(store.Root, sha) : null;

    /// <summary>Remembers (or, with null, leaves unchanged) the rules pack for this module. A null path is ignored: <see cref="RulesMemory"/> only stores.</summary>
    public void SetRulesPack(ModuleEntry entry, string? path)
    {
        if (entry.Sha256 is { Length: > 0 } sha && !string.IsNullOrWhiteSpace(path)) RulesMemory.Remember(store.Root, sha, path);
    }
}
