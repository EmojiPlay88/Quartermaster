using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Quartermaster.Render.Update;

/// <summary>
/// Downloads, verifies and stages an update under <c>updatesRoot\&lt;version&gt;\</c> (updatesRoot = <c>AppDataRoot.Sub("Updates")</c>), then
/// hands over to the helper. Nothing here touches the app folder except the writability probe.
/// </summary>
public static class UpdateInstaller
{
    public const string AppExe = "Quartermaster.App.exe";
    public const string HelperExe = "Quartermaster.Updater.exe";
    /// <summary>The release's file list (written at publish time). The helper only moves files it names; a folder without one can't self-update.</summary>
    public const string ManifestName = "quartermaster-files.txt";
    public const string NoManifestText = "This copy of Quartermaster can't update itself; download the new version from the release page.";
    const int MaxShaBytes = 4096;
    const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Downloads the zip and its <c>.sha256</c>, checks the size and SHA-256 (first token of the <c>.sha256</c>, hex, any case) before
    /// unpacking, unpacks into <c>&lt;root&gt;\staged</c> refusing absolute or <c>..</c> paths, and checks <c>Quartermaster.App.exe</c> is at the top
    /// with a FileVersion equal to the release version. Anything wrong throws <see cref="UpdateRefused"/>; on any failure or cancellation the
    /// whole version folder is deleted, so no partial download or stage is left.
    /// </summary>
    public static async Task<StagedUpdate> Download(IUpdateFeed f, UpdateInfo u, string updatesRoot, IProgress<double>? progress, CancellationToken ct)
    {
        if (u.ZipSize <= 0) throw new UpdateRefused("The release does not say how large the download is; it was not installed.");
        var root = Path.GetFullPath(Path.Combine(updatesRoot, AppVersion.Format(u.Version)));
        var staged = Path.Combine(root, "staged");
        try { DeleteDir(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateRefused($"Could not clear the old download folder {root}: {ex.Message}");
        }
        Directory.CreateDirectory(root);
        try
        {
            var expected = await ReadSha(f, u.ShaUrl, ct);
            var zipPath = Path.Combine(root, UpdateChecker.ZipName(u.Version));
            var part = zipPath + ".part";
            await Fetch(f, u.ZipUrl, part, u.ZipSize, expected, progress, ct);
            File.Move(part, zipPath);
            ct.ThrowIfCancellationRequested();
            Extract(zipPath, staged);
            CheckExe(staged, u.Version);
            if (!File.Exists(Path.Combine(staged, ManifestName)))
                throw new UpdateRefused($"The update has no file list ({ManifestName}); it was not installed.");
            return new StagedUpdate(u.Version, staged, root);
        }
        catch
        {
            try { DeleteDir(root); } catch (Exception) { /* best effort; CleanupOld retries later */ }
            throw;
        }
    }

    static async Task<byte[]> ReadSha(IUpdateFeed f, Uri url, CancellationToken ct)
    {
        var buf = new byte[MaxShaBytes];
        var total = 0;
        await using (var s = await f.Open(url, ct))
        {
            int n;
            while (total < buf.Length && (n = await s.ReadAsync(buf.AsMemory(total), ct)) > 0) total += n;
        }
        var text = Encoding.ASCII.GetString(buf, 0, total);
        var token = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new UpdateRefused("The release's .sha256 file does not hold a SHA-256 hash; the update was not installed.");
        return Convert.FromHexString(token);
    }

    static async Task Fetch(IUpdateFeed f, Uri url, string target, long size, byte[] expected, IProgress<double>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        var lastReported = -1.0;
        await using (var src = await f.Open(url, ct))
        await using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += n;
                if (total > size) throw SizeMismatch(size, total);
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                var fraction = (double)total / size;
                if (fraction - lastReported >= 0.01 || total == size)
                {
                    lastReported = fraction;
                    progress?.Report(fraction);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        if (total != size) throw SizeMismatch(size, total);
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
            throw new UpdateRefused("The download's SHA-256 does not match the release's .sha256 (corrupt or tampered download); the update was not installed.");
    }

    static UpdateRefused SizeMismatch(long expected, long got) =>
        new($"The download's size does not match the release ({got:N0} bytes{(got > expected ? " or more" : "")}, expected {expected:N0}); the update was not installed.");

    static void Extract(string zipPath, string staged)
    {
        var stagedFull = Path.GetFullPath(staged);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // Check every entry before writing anything.
            var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDir)>();
            long unpacked = 0;
            foreach (var e in zip.Entries)
            {
                var target = SafeTarget(stagedFull, e.FullName);
                var isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                unpacked += e.Length;
                if (unpacked > MaxUnpackedBytes) throw new UpdateRefused("The update unpacks to more than 4 GB; it was not installed.");
                plan.Add((e, target, isDir));
            }
            Directory.CreateDirectory(stagedFull);
            foreach (var (e, target, isDir) in plan)
            {
                if (isDir) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) throw new UpdateRefused($"The update lists '{e.FullName}' twice; it was not installed.");
                e.ExtractToFile(target, overwrite: false);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateRefused("The download is not a valid zip file: " + ex.Message);
        }
    }

    /// <summary>The full path for a zip entry inside <paramref name="stagedFull"/>; refuses absolute, drive, <c>..</c> and stream (<c>:</c>) paths.</summary>
    static string SafeTarget(string stagedFull, string name)
    {
        UpdateRefused Unsafe() => new($"The update contains an unsafe file path ('{name}'); it was not installed.");
        if (name.Length == 0 || name.Contains('\0') || name.Contains(':') || Path.IsPathRooted(name) || name[0] is '/' or '\\') throw Unsafe();
        var parts = name.Split('/', '\\');
        if (parts.Any(p => p == "..")) throw Unsafe();
        var full = Path.GetFullPath(Path.Combine(stagedFull, name));
        if (!full.StartsWith(stagedFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Unsafe();
        return full;
    }

    static void CheckExe(string staged, Version expected)
    {
        var exe = Path.Combine(staged, AppExe);
        if (!File.Exists(exe)) throw new UpdateRefused($"The update does not contain {AppExe}; it was not installed.");
        var got = FileVersion(exe);
        var want = AppVersion.Format(expected);
        if (got == null)
            throw new UpdateRefused($"{AppExe} in the update has no file version; it was not installed.");
        if (AppVersion.Format(got) != want)
            throw new UpdateRefused($"{AppExe} in the update is version {AppVersion.Format(got)}, but the release is {want}; it was not installed.");
    }

    /// <summary>The Win32 FileVersion of an executable (Major.Minor.Build), or null when it has none.</summary>
    internal static Version? FileVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            if (string.IsNullOrWhiteSpace(fvi.FileVersion)) return null;
            return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// True when the app folder can take the swap: a file can be created, renamed and deleted in the folder itself. The parent folder is not
    /// checked (the helper keeps the backup under the updates folder when it cannot write beside the app). False for Program Files, missing or locked folders.
    /// </summary>
    public static bool AppFolderWritable(string appDir)
    {
        string? probe = null, renamed = null;
        try
        {
            var full = Path.GetFullPath(appDir);
            var id = Guid.NewGuid().ToString("N");
            probe = Path.Combine(full, ".qm-write-probe-" + id);
            renamed = probe + ".renamed";
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Move(probe, renamed);
            probe = null;
            File.Delete(renamed);
            renamed = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        finally
        {
            foreach (var left in new[] { probe, renamed })
                if (left != null)
                    try { File.Delete(left); } catch (Exception) { /* best effort */ }
        }
    }

    /// <summary>The file the new app writes once it started after an update; the helper waits for it (and rolls back without it).</summary>
    public static string MarkerPath(string updatesRoot, Version v) => Path.Combine(updatesRoot, "started-" + AppVersion.Format(v));

    /// <summary>
    /// Copies <c>Quartermaster.Updater.exe</c> (self-contained single file next to the app exe) to <c>&lt;s.Root&gt;\helper\</c> and starts it with
    /// <c>--app &lt;appDir&gt; --staged &lt;dir&gt; --pid &lt;pid&gt; --version &lt;new&gt; --old &lt;current&gt; --marker &lt;path&gt;</c>. A stale marker is deleted first.
    /// </summary>
    public static void LaunchHelper(StagedUpdate s, string appDir, int pid) =>
        LaunchHelper(s, appDir, pid, psi => Process.Start(psi) ?? throw new UpdateRefused("Could not start the updater helper."));

    internal static void LaunchHelper(StagedUpdate s, string appDir, int pid, Func<ProcessStartInfo, Process?> start)
    {
        var source = Path.Combine(appDir, HelperExe);
        if (!File.Exists(source)) throw new UpdateRefused("The updater helper is missing; download the new version from the release page.");
        if (!CanSelfUpdate(appDir)) throw new UpdateRefused(NoManifestText);
        var root = Path.GetFullPath(s.Root);
        var helperDir = Path.Combine(root, "helper");
        Directory.CreateDirectory(helperDir);
        var helper = Path.Combine(helperDir, HelperExe);
        File.Copy(source, helper, overwrite: true);

        var marker = MarkerPath(Path.GetDirectoryName(root)!, s.Version);
        if (File.Exists(marker)) File.Delete(marker);

        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = helperDir };
        foreach (var a in new[]
                 {
                     "--app", appDir, "--staged", s.StagedDir, "--pid", pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--version", AppVersion.Format(s.Version), "--old", AppVersion.Text, "--marker", marker,
                 })
            psi.ArgumentList.Add(a);
        start(psi)?.Dispose();
    }

    /// <summary>True when the app folder carries the release's file list (<see cref="ManifestName"/>), so the helper knows which files are Quartermaster's.</summary>
    public static bool CanSelfUpdate(string appDir) => File.Exists(Path.Combine(appDir, ManifestName));

    /// <summary>
    /// After a confirmed start of <paramref name="current"/> (spec Part C step 6), best effort: <see cref="CleanupOld"/>, then the current
    /// version's downloaded zip and <c>staged</c> folder (its helper, which may still be running, is left), then every
    /// <c>Quartermaster.backup-&lt;v&gt;</c> beside <paramref name="appDir"/> and every <c>backup-&lt;v&gt;</c> in the updates folder except the
    /// most recent (highest version) of each. Only names that match those patterns exactly are touched.
    /// </summary>
    public static void CleanupAfterUpdate(string updatesRoot, string? appDir, Version current)
    {
        CleanupOld(updatesRoot, current);
        var cur = Path.Combine(updatesRoot, AppVersion.Format(current));
        var zip = Path.Combine(cur, UpdateChecker.ZipName(current));
        TryDelete(() => DeleteDir(Path.Combine(cur, "staged")));
        TryDelete(() => File.Delete(zip));
        TryDelete(() => File.Delete(zip + ".part"));
        PruneBackups(updatesRoot, "backup-");
        if (appDir != null && Path.GetDirectoryName(Path.GetFullPath(Path.TrimEndingDirectorySeparator(appDir))) is { } parent)
            PruneBackups(parent, "Quartermaster.backup-");
    }

    /// <summary>Deletes folders in <paramref name="dir"/> named exactly <paramref name="prefix"/> + a version, except the highest version.</summary>
    static void PruneBackups(string dir, string prefix)
    {
        List<(string Path, Version V)> found;
        try
        {
            if (!Directory.Exists(dir)) return;
            found = Directory.GetDirectories(dir)
                .Select(d => (Path: d, Name: Path.GetFileName(d)))
                .Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal) && IsVersionName(x.Name[prefix.Length..]))
                .Select(x => (x.Path, Version.Parse(x.Name[prefix.Length..])))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var (path, _) in found.OrderByDescending(x => x.V).Skip(1))
            TryDelete(() => DeleteDir(path));
    }

    static void TryDelete(Action a)
    {
        try { a(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Best effort: deletes <c>&lt;version&gt;</c> folders and <c>started-&lt;version&gt;</c> markers of every version but <paramref name="current"/>; other names are left alone.</summary>
    public static void CleanupOld(string updatesRoot, Version current)
    {
        var keep = AppVersion.Format(current);
        IEnumerable<string> dirs, files;
        try
        {
            if (!Directory.Exists(updatesRoot)) return;
            dirs = Directory.GetDirectories(updatesRoot);
            files = Directory.GetFiles(updatesRoot, "started-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (IsVersionName(name) && name != keep)
                try { DeleteDir(d); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f)["started-".Length..];
            if (IsVersionName(name) && name != keep)
                try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static bool IsVersionName(string name) => Version.TryParse(name, out var v) && v.Build >= 0 && v.Revision < 0 && AppVersion.Format(v) == name;

    static void DeleteDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        // Read-only files would block the delete; never follow a link out of the folder.
        foreach (var file in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }
}
