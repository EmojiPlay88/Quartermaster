using System.ComponentModel;
using System.Diagnostics;

namespace Quartermaster.Render.Update;

/// <summary>Hands a downloaded system package (.deb, .rpm) to the desktop's package installer; the package manager installs it, not Quartermaster.</summary>
public static class PackageHandOff
{
    /// <summary>
    /// Runs <c>xdg-open &lt;packagePath&gt;</c> (the path as one argument, no shell) through <paramref name="start"/>. False when it could not
    /// be started; the reason is traced.
    /// </summary>
    public static bool Open(string packagePath, Func<ProcessStartInfo, Process?> start)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        ArgumentNullException.ThrowIfNull(start);
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(packagePath);
        try
        {
            start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            Trace.WriteLine("update: could not open the package installer: " + ex.Message);
            return false;
        }
    }
}
