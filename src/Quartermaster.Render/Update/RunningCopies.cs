using System.ComponentModel;
using System.Diagnostics;

namespace Quartermaster.Render.Update;

/// <summary>Other Quartermaster processes started from the same exe (a second window may hold a game the update would break).</summary>
public static class RunningCopies
{
    public const string CloseOthersText = "Close every other Quartermaster window first.";

    /// <summary>
    /// True when a process other than <paramref name="selfPid"/> runs from <paramref name="exePath"/> (main module paths compared
    /// case-insensitively). <paramref name="lister"/> yields (pid, main module path or null when it cannot be read); the default lists the
    /// processes named like the exe and ignores access errors.
    /// </summary>
    public static bool OthersRunning(string exePath, int selfPid, Func<string, IEnumerable<(int Pid, string? Path)>>? lister = null) =>
        OthersRunning(new[] { exePath }, selfPid, lister);

    /// <summary>
    /// As <see cref="OthersRunning(string, int, Func{string, IEnumerable{ValueTuple{int, string}}}?)"/> for any of several exe paths (an app
    /// folder may hold the Desktop app and the classic one): each distinct process name is listed once.
    /// </summary>
    public static bool OthersRunning(IReadOnlyList<string> exePaths, int selfPid, Func<string, IEnumerable<(int Pid, string? Path)>>? lister = null)
    {
        ArgumentNullException.ThrowIfNull(exePaths);
        var want = new HashSet<string>(exePaths.Select(p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);
        foreach (var name in exePaths.Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var (pid, path) in (lister ?? List)(name!))
        {
            if (pid == selfPid || string.IsNullOrEmpty(path)) continue;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (want.Contains(full)) return true;
        }
        return false;
    }

    static IEnumerable<(int Pid, string? Path)> List(string processName)
    {
        Process[] all;
        try { all = Process.GetProcessesByName(processName); }
        catch (InvalidOperationException) { return []; }
        var list = new List<(int, string?)>();
        foreach (var p in all)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            list.Add((p.Id, path));
            p.Dispose();
        }
        return list;
    }
}
