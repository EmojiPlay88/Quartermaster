using System.Diagnostics;

namespace Quartermaster.Updater;

public static class ExitCodes
{
    public const int Ok = 0, BadArgs = 1, PidTimeout = 2, RolledBack = 3, SwapFailed = 4, RollbackFailed = 5;
    /// <summary>Nothing was touched: another copy runs from the app folder, or a folder has no usable quartermaster-files.txt.</summary>
    public const int Refused = 6;
}

public sealed class UpdateOptions
{
    public required string AppDir { get; init; }
    public required string StagedDir { get; init; }
    public required int Pid { get; init; }
    public required string Version { get; init; }
    public required string OldVersion { get; init; }
    public required string MarkerPath { get; init; }
    public TimeSpan MarkerTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ExitTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public string? BackupDir { get; init; }
    public string? JournalPath { get; init; }
    public string? LogPath { get; init; }
    /// <summary>The exe to restart, relative to <see cref="AppDir"/>; the 0.1.0 contract (and WinForms) restarts Quartermaster.App.exe.</summary>
    public string Exe { get; init; } = "Quartermaster.App.exe";
    /// <summary>Optional unit path (whole-unit swaps); only parsed and stored here.</summary>
    public string? Unit { get; init; }

    public string ResolvedLogPath => LogPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MarkerPath))!, "helper.log");

    public static UpdateOptions? Parse(string[] args)
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) return null;
            m[args[i]] = args[i + 1];
        }
        foreach (var k in new[] { "--app", "--staged", "--pid", "--version", "--old", "--marker" })
            if (!m.ContainsKey(k)) return null;
        if (!int.TryParse(m["--pid"], out var pid)) return null;
        var mt = 30.0; var et = 60.0;
        if (m.TryGetValue("--marker-timeout", out var a) && !double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out mt)) return null;
        if (m.TryGetValue("--exit-timeout", out var b) && !double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out et)) return null;
        var folder = Path.GetDirectoryName(m["--marker"]) ?? "";
        return new UpdateOptions
        {
            AppDir = m["--app"], StagedDir = m["--staged"], Pid = pid, Version = m["--version"], OldVersion = m["--old"],
            MarkerPath = m["--marker"], MarkerTimeout = TimeSpan.FromSeconds(mt), ExitTimeout = TimeSpan.FromSeconds(et),
            JournalPath = Path.Combine(folder, "swap-journal.json"), LogPath = Path.Combine(folder, "helper.log"),
            Exe = m.TryGetValue("--exe", out var exe) ? exe : "Quartermaster.App.exe",
            Unit = m.TryGetValue("--unit", out var unit) ? unit : null,
        };
    }

}

public interface IProcessHost
{
    bool IsAlive(int pid);
    int Start(string exe, string args, string workDir);
    void Kill(int pid);
    /// <summary>Running processes with this name and their main module path (null when it cannot be read).</summary>
    IEnumerable<(int Pid, string? Path)> ProcessesNamed(string processName);
}

public sealed class RealProcessHost : IProcessHost
{
    public bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public int Start(string exe, string args, string workDir)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = workDir })
                ?? throw new InvalidOperationException("Process.Start returned null");
        return p.Id;
    }

    public void Kill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(true); p.WaitForExit(5000); }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    public IEnumerable<(int Pid, string? Path)> ProcessesNamed(string processName)
    {
        Process[] all;
        try { all = Process.GetProcessesByName(processName); }
        catch (InvalidOperationException) { return []; }
        var list = new List<(int, string?)>();
        foreach (var p in all)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            list.Add((p.Id, path));
            p.Dispose();
        }
        return list;
    }
}

public sealed class UpdateRunner(UpdateOptions o, IProcessHost host, Action<string> log)
{
    public static string ChooseBackupDir(string appDir, string oldVersion, string markerFolder)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(appDir).TrimEnd('\\', '/'));
        if (parent != null && Directory.Exists(parent))
        {
            try
            {
                var probe = Path.Combine(parent, ".qm-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return Path.Combine(parent, "Quartermaster.backup-" + oldVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(markerFolder, "backup-" + oldVersion);
    }

    public int Run()
    {
        var markerFolder = Path.GetDirectoryName(Path.GetFullPath(o.MarkerPath))!;
        var journalPath = o.JournalPath ?? Path.Combine(markerFolder, "swap-journal.json");
        var backup = o.BackupDir ?? ChooseBackupDir(o.AppDir, o.OldVersion, markerFolder);
        var appExe = Path.Combine(o.AppDir, o.Exe);
        string[] exePaths = [.. new[] { appExe, Path.Combine(o.AppDir, "Quartermaster.exe"), Path.Combine(o.AppDir, "Quartermaster.App.exe") }.Distinct()];
        var swapper = new Swapper(o.AppDir, o.StagedDir, backup, journalPath) { Version = o.Version };

        log($"Waiting up to {o.ExitTimeout.TotalSeconds:0}s for app pid {o.Pid} to exit");
        var deadline = DateTime.UtcNow + o.ExitTimeout;
        while (host.IsAlive(o.Pid))
        {
            if (DateTime.UtcNow >= deadline) { log("App did not exit; aborting, nothing changed"); return ExitCodes.PidTimeout; }
            Thread.Sleep(100);
        }

        try { File.Delete(o.MarkerPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        if (OtherCopiesRunning(host, exePaths, o.Pid))
        {
            log("Another Quartermaster runs from the app folder; refusing, nothing changed");
            return Refused(appExe);
        }

        try
        {
            var outcome = swapper.Resume();
            log($"Journal resume outcome: {outcome}");
            if (outcome == ResumeOutcome.RolledBack) return Failed(swapper, appExe, rollback: false);
            if (outcome is ResumeOutcome.None or ResumeOutcome.Stale) { log("Applying update"); swapper.Apply(); }
        }
        catch (ManifestMissingException ex)
        {
            log("Refusing, nothing changed: " + ex.Message);
            return Refused(appExe);
        }
        catch (Exception ex)
        {
            log("Swap failed: " + ex.Message);
            try { swapper.Rollback(); }
            catch (Exception ex2)
            {
                log("Rollback failed, journal kept for recovery: " + ex2.Message);
                Launch(appExe, "--update-failed " + o.Version);
                return ExitCodes.RollbackFailed;
            }
            Launch(appExe, "--update-failed " + o.Version);
            swapper.DeleteJournal();
            return ExitCodes.SwapFailed;
        }

        var pid = Launch(appExe, "--updated-from " + o.OldVersion);
        log($"Started new app pid {pid}; waiting up to {o.MarkerTimeout.TotalSeconds:0}s for marker");
        if (pid > 0 && Startup.WaitForMarker(o.MarkerPath, () => host.IsAlive(pid), o.MarkerTimeout))
        {
            swapper.DeleteJournal();
            log("New version confirmed");
            return ExitCodes.Ok;
        }

        log("New version did not confirm startup; rolling back");
        if (pid > 0 && host.IsAlive(pid)) host.Kill(pid);
        return Failed(swapper, appExe, rollback: true);
    }

    int Failed(Swapper swapper, string appExe, bool rollback)
    {
        if (rollback)
        {
            try { swapper.Rollback(); }
            catch (Exception ex)
            {
                log("Rollback failed, journal kept for recovery: " + ex.Message);
                Launch(appExe, "--update-failed " + o.Version);
                return ExitCodes.RollbackFailed;
            }
        }
        Launch(appExe, "--update-failed " + o.Version);
        swapper.DeleteJournal();
        return ExitCodes.RolledBack;
    }

    /// <summary>True when a process other than <paramref name="selfPid"/> runs from any of <paramref name="exePaths"/> (paths compared OrdinalIgnoreCase on Windows/macOS, Ordinal on Linux; unreadable paths ignored).</summary>
    public static bool OtherCopiesRunning(IProcessHost host, IReadOnlyList<string> exePaths, int selfPid) =>
        OtherCopiesRunning(host, exePaths, selfPid, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal static bool OtherCopiesRunning(IProcessHost host, IReadOnlyList<string> exePaths, int selfPid, StringComparison comparison)
    {
        foreach (var exe in exePaths)
        {
            var want = Path.GetFullPath(exe);
            if (host.ProcessesNamed(Path.GetFileNameWithoutExtension(exe))
                .Any(p => p.Pid != selfPid && p.Path != null && string.Equals(Path.GetFullPath(p.Path), want, comparison)))
                return true;
        }
        return false;
    }

    int Refused(string appExe)
    {
        Launch(appExe, "--update-refused " + o.Version);
        return ExitCodes.Refused;
    }

    int Launch(string exe, string args)
    {
        try { return host.Start(exe, args, o.AppDir); }
        catch (Exception ex) { log($"Could not start {exe}: {ex.Message}"); return -1; }
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        var o = UpdateOptions.Parse(args);
        if (o == null)
        {
            Console.Error.WriteLine("Usage: Quartermaster.Updater --app <dir> --staged <dir> --pid <n> --version <v> --old <v> --marker <path> [--marker-timeout <s>] [--exit-timeout <s>] [--exe <rel>] [--unit <path>]");
            return ExitCodes.BadArgs;
        }
        var logPath = o.ResolvedLogPath;
        void Log(string m)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, $"{DateTime.Now:s} {m}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { return new UpdateRunner(o, new RealProcessHost(), Log).Run(); }
        catch (Exception ex) { Log("Fatal: " + ex); return ExitCodes.SwapFailed; }
    }
}
