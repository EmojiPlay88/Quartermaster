using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Online;

/// <summary>Certificate trust prompts (trust on first use) and where trusted fingerprints are kept.</summary>
public static class RelayTrust
{
    /// <summary>Trusted relay certificates (per host:port) under the app data root; the file WinForms uses.</summary>
    public static PinStore Pins() => new(Path.Combine(AppDataRoot.Path, "relay-pins.json"));

    /// <summary>
    /// Asks whether to trust an unknown or changed self-signed certificate (public-CA certificates never get here). Runs on a worker thread
    /// and blocks until the question is answered on the UI thread. Called on the UI thread itself it would deadlock, so it returns false.
    /// </summary>
    public static bool Ask(Window owner, IDialogs dialogs, CertificateCheck c)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                System.Diagnostics.Trace.WriteLine("relay certificate of " + c.HostPort + " asked on the UI thread: refused");
                return false;
            }
            var (title, message, changed) = OnlineLobbyModel.CertificatePrompt(c);
            return Dispatcher.UIThread.InvokeAsync(() => dialogs.AskYesNoAsync(owner, message, title, defaultNo: changed)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("relay certificate prompt failed: " + ex.Message);
            return false;
        }
    }
}
