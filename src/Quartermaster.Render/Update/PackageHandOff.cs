using System.ComponentModel;
using System.Diagnostics;

namespace Quartermaster.Render.Update;

/// <summary>Hands a downloaded system package (.deb, .rpm) to the desktop's package installer; the package manager installs it, not Quartermaster.</summary>
public static class PackageHandOff
{
    /// <summary>Variables the AppImage runtime sets for its app; processes Quartermaster starts drop them so they are not taken for a running copy.</summary>
    public static readonly IReadOnlyList<string> AppImageVariables = ["APPIMAGE", "APPDIR", "ARGV0", "OWD"];

    /// <summary>How long <see cref="Open"/> waits for <c>xdg-open</c> to exit (a non-zero exit code within it is a failure).</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs <c>xdg-open &lt;packagePath&gt;</c> (the path as one argument, no shell, without <see cref="AppImageVariables"/>) through
    /// <paramref name="start"/> and waits up to <see cref="ExitWait"/> for it. False when it could not be started or exited with a non-zero
    /// code in that time (no handler for the package); the reason is traced. Still running after the wait, or no process to wait for, is success.
    /// </summary>
    public static bool Open(string packagePath, Func<ProcessStartInfo, Process?> start)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        ArgumentNullException.ThrowIfNull(start);
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(packagePath);
        foreach (var v in AppImageVariables) psi.Environment.Remove(v);
        Process? p;
        try { p = start(psi); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            Trace.WriteLine("update: could not open the package installer: " + ex.Message);
            return false;
        }
        if (p == null) return true;
        using (p)
        {
            try
            {
                if (!p.WaitForExit(ExitWait)) return true;
                if (p.ExitCode == 0) return true;
                Trace.WriteLine($"update: xdg-open exited with code {p.ExitCode}");
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                Trace.WriteLine("update: could not wait for the package installer: " + ex.Message);
                return true;
            }
        }
    }
}
