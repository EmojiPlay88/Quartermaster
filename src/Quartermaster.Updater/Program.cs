using System.Diagnostics;

namespace Quartermaster.Updater;

public static class ExitCodes
{
    public const int Ok = 0, BadArgs = 1, PidTimeout = 2, RolledBack = 3, SwapFailed = 4;
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
        };
    }

}

public interface IProcessHost
{
    bool IsAlive(int pid);
    int Start(string exe, string args, string workDir);
    void Kill(int pid);
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
        var appExe = Path.Combine(o.AppDir, "Quartermaster.App.exe");
        var swapper = new Swapper(o.AppDir, o.StagedDir, backup, journalPath);

        log($"Waiting up to {o.ExitTimeout.TotalSeconds:0}s for app pid {o.Pid} to exit");
        var deadline = DateTime.UtcNow + o.ExitTimeout;
        while (host.IsAlive(o.Pid))
        {
            if (DateTime.UtcNow >= deadline) { log("App did not exit; aborting, nothing changed"); return ExitCodes.PidTimeout; }
            Thread.Sleep(100);
        }

        try { File.Delete(o.MarkerPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }

        try
        {
            var outcome = swapper.Resume();
            log($"Journal resume outcome: {outcome}");
            if (outcome == ResumeOutcome.RolledBack) return Failed(swapper, appExe, rollback: false);
            if (outcome == ResumeOutcome.None) { log("Applying update"); swapper.Apply(); }
        }
        catch (Exception ex)
        {
            log("Swap failed: " + ex.Message);
            try { swapper.Rollback(); } catch (Exception ex2) { log("Rollback failed: " + ex2.Message); }
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
            catch (Exception ex) { log("Rollback failed: " + ex.Message); }
        }
        Launch(appExe, "--update-failed " + o.Version);
        swapper.DeleteJournal();
        return ExitCodes.RolledBack;
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
            Console.Error.WriteLine("Usage: Quartermaster.Updater.exe --app <dir> --staged <dir> --pid <n> --version <v> --old <v> --marker <path> [--marker-timeout <s>] [--exit-timeout <s>]");
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
