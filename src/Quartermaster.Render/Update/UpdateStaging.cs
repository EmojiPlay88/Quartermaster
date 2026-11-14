using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace Quartermaster.Render.Update;

/// <summary>
/// Turns a downloaded (already hash-checked) asset into a staged update, per <see cref="PackageKind"/>: unpacks zip and tar.gz with the safe-path
/// rules, keeps Unix mode bits, and checks the package marker. Throws <see cref="UpdateRefused"/>; the caller deletes the version folder.
/// </summary>
internal static class UpdateStaging
{
    public const string AppImageName = "Quartermaster.AppImage";
    public const string MacBundle = "Quartermaster.app";
    const long MaxUnpackedBytes = 4L * 1024 * 1024 * 1024;
    const UnixFileMode PermissionBits = (UnixFileMode)0x1FF;

    /// <summary>The file the asset is downloaded to: inside <c>staged</c> for an AppImage, else in the version folder.</summary>
    public static string DownloadPath(UpdateInfo u, string root, string staged) =>
        u.Kind == PackageKind.LinuxAppImage
            ? Path.Combine(staged, AppImageName)
            : Path.Combine(root, UpdateChecker.AssetName(u.Version, u.Kind));

    /// <summary>Stages <paramref name="file"/> (as saved at <see cref="DownloadPath"/>) and returns <c>StagedUpdate.StagedDir</c>.</summary>
    public static string Stage(UpdateInfo u, string file, string staged)
    {
        switch (u.Kind)
        {
            case PackageKind.WinZip or PackageKind.WinSetup:
                ExtractZip(file, staged);
                if (PackageFile.Read(staged) is { } marker) { CheckMarker(marker, u); UpdateInstaller.RequireAppExe(staged); }
                else UpdateInstaller.CheckExe(staged, u.Version);
                RequireManifest(staged);
                return staged;
            case PackageKind.MacApp:
                ExtractZip(file, staged);
                var macos = Path.Combine(staged, MacBundle, "Contents", "MacOS");
                CheckMarker(PackageFile.Read(macos), u);
                RequireManifest(macos);
                var bundle = Path.Combine(staged, MacBundle);
                if (IsMacOS()) SignBundle(bundle);
                return bundle;
            case PackageKind.LinuxTar:
                ExtractTar(file, staged);
                CheckMarker(PackageFile.Read(staged), u);
                RequireManifest(staged);
                return staged;
            case PackageKind.LinuxAppImage:
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, (UnixFileMode)0x1ED);
                return file;
            default:
                return file;
        }
    }

    /// <summary>Test seam: whether this is macOS (only there is the staged bundle re-signed).</summary>
    internal static Func<bool> IsMacOS = OperatingSystem.IsMacOS;

    /// <summary>Test seam: runs a tool (no shell) and returns its exit code and standard error.</summary>
    internal static Func<ProcessStartInfo, (int ExitCode, string StdErr)> RunTool = RunProcess;

    const string Codesign = "/usr/bin/codesign";
    static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Re-seals the staged bundle: the update zip has no extended attributes, so the xattr signatures of the non-Mach-O files in Contents/MacOS
    /// are gone. Ad-hoc signs every regular file in Contents/MacOS except the main executable, then the bundle, then verifies it.
    /// Same file rule as build/macos/make-app-bundle.sh; keep the two in step.
    /// </summary>
    static void SignBundle(string bundle)
    {
        var macos = Path.Combine(bundle, "Contents", "MacOS");
        var mainExe = Path.Combine(macos, "Quartermaster");
        var files = Directory.EnumerateFiles(macos, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, mainExe, StringComparison.Ordinal) && !File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count > 0) RunCodesign(["--force", "--sign", "-", .. files]);
        RunCodesign(["--force", "--sign", "-", bundle]);
        RunCodesign(["--verify", "--strict", bundle]);
    }

    static void RunCodesign(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Codesign) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var (code, err) = RunTool(psi);
        if (code == 0) return;
        var first = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {code}";
        throw new UpdateRefused($"Could not sign the update for this Mac (codesign: {first}); it was not installed.");
    }

    /// <summary>Runs the tool with a timeout (killed when it runs over); a tool that cannot start counts as a failure.</summary>
    static (int ExitCode, string StdErr) RunProcess(ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "could not start " + psi.FileName);
            var stderr = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(ToolTimeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return (-1, $"timed out after {ToolTimeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            Task.WaitAll(stderr, stdout);
            return (p.ExitCode, stderr.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    static void RequireManifest(string dir)
    {
        if (!File.Exists(Path.Combine(dir, UpdateInstaller.ManifestName)))
            throw new UpdateRefused($"The update has no file list ({UpdateInstaller.ManifestName}); it was not installed.");
    }

    static void CheckMarker(PackageInfo? got, UpdateInfo u)
    {
        if (got == null) throw new UpdateRefused($"The update has no package marker ({PackageFile.FileName}); it was not installed.");
        var want = AppVersion.Format(u.Version);
        if (AppVersion.Format(got.Version) != want)
            throw new UpdateRefused($"The update is version {AppVersion.Format(got.Version)}, but the release is {want}; it was not installed.");
        var win = u.Kind is PackageKind.WinZip or PackageKind.WinSetup;
        var sameKind = win ? got.Kind is PackageKind.WinZip or PackageKind.WinSetup : got.Kind == u.Kind;
        if (!sameKind)
            throw new UpdateRefused($"The update is a {PackageFile.KindText(got.Kind)} package, but this copy is {PackageFile.KindText(u.Kind)}; it was not installed.");
    }

    static void ExtractZip(string zipPath, string staged)
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
                if (unpacked > MaxUnpackedBytes) throw TooBig();
                plan.Add((e, target, isDir));
            }
            Directory.CreateDirectory(stagedFull);
            foreach (var (e, target, isDir) in plan)
            {
                if (isDir) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) throw Twice(e.FullName);
                e.ExtractToFile(target, overwrite: false);
                var mode = (UnixFileMode)((e.ExternalAttributes >> 16) & 0x1FF);
                if (mode != 0 && !OperatingSystem.IsWindows()) File.SetUnixFileMode(target, mode);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateRefused("The download is not a valid zip file: " + ex.Message);
        }
    }

    static void ExtractTar(string path, string staged)
    {
        var stagedFull = Path.GetFullPath(staged);
        try
        {
            // Pass 1: check every entry before writing anything.
            long unpacked = 0;
            foreach (var e in Entries(path))
            {
                Classify(stagedFull, e);
                unpacked += e.Length;
                if (unpacked > MaxUnpackedBytes) throw TooBig();
            }
            Directory.CreateDirectory(stagedFull);
            // Pass 2: write.
            foreach (var e in Entries(path))
            {
                var (target, isDir) = Classify(stagedFull, e);
                if (target == null) continue;
                if (isDir) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) throw Twice(e.Name);
                using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    e.DataStream?.CopyTo(dst);
                var mode = e.Mode & PermissionBits;
                if (mode != 0 && !OperatingSystem.IsWindows()) File.SetUnixFileMode(target, mode);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new UpdateRefused("The download is not a valid tar.gz file: " + ex.Message);
        }
    }

    /// <summary>The gzip'd tar's entries, streamed; each entry's data is readable only until the next one is requested.</summary>
    static IEnumerable<TarEntry> Entries(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry(copyData: false) is { } e) yield return e;
    }

    /// <summary>Target path (null for entries to skip, like the archive root or pax headers) and whether it is a directory; refuses links, special files and unsafe paths.</summary>
    static (string? Target, bool IsDir) Classify(string stagedFull, TarEntry e)
    {
        switch (e.EntryType)
        {
            case TarEntryType.GlobalExtendedAttributes:
                return (null, false);
            case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory:
                break;
            default:
                throw new UpdateRefused($"The update contains a link or special file ('{e.Name}'); it was not installed.");
        }
        var name = e.Name;
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (name is "." or "") return (null, false);
        var isDir = e.EntryType == TarEntryType.Directory;
        return (SafeTarget(stagedFull, name), isDir);
    }

    static UpdateRefused TooBig() => new("The update unpacks to more than 4 GB; it was not installed.");
    static UpdateRefused Twice(string name) => new($"The update lists '{name}' twice; it was not installed.");

    /// <summary>The full path for an archive entry inside <paramref name="stagedFull"/>; refuses absolute, drive, <c>..</c> and stream (<c>:</c>) paths.</summary>
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
}
