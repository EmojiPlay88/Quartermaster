using Quartermaster.Desktop.Workbench;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// Tools > Rules Workbench (WinForms MainForm.ShowWorkbench): one workbench window per session, owned by the main window and not modal,
/// its bounds remembered per module under "bench:workbench". It closes with the session; its pending edits are committed before any
/// rules-pack or game save and before the close questions.
/// </summary>
public partial class MainWindow
{
    WorkbenchWindow? _workbenchWin;
    IOcrRecognizer? _ocrEngine;

    /// <summary>Makes the app's OCR engine (Windows OCR or Tesseract, see <see cref="OcrEngines"/>); tests replace it.</summary>
    internal static Func<Action<string>, IOcrRecognizer> CreateOcr = log => OcrEngines.Create(log: log);

    /// <summary>The one OCR engine of the app: made on the first Workbench open, disposed when the main window closes (app exit),
    /// after the session teardown has closed the workbench.</summary>
    IOcrRecognizer AppOcr()
    {
        if (_ocrEngine != null) return _ocrEngine;
        var engine = _ocrEngine = CreateOcr(m => _session?.Log.Add(DiagnosticSeverity.Info, "ocr", m, ""));
        Closed += (_, _) => (engine as IDisposable)?.Dispose();
        return engine;
    }

    /// <summary>The open workbench (null while closed).</summary>
    internal WorkbenchWindow? WorkbenchTool => _workbenchWin;

    /// <summary>Tools > Rules Workbench and Ctrl/Cmd+Shift+W: needs a module; refused (status bar) while a received turn replays.</summary>
    public void ShowWorkbench()
    {
        if (_session is not { } s) return;
        if (s.IsReplaying) { SetStatus(ModuleSession.ReplayingText); return; }   // no rules edits while a received turn replays
        Open(ref _workbenchWin, "bench:workbench",
            () => new WorkbenchWindow(s, _dialogs, () => RulesFlowAsync(f => f.NewAsync()), () => RulesFlowAsync(f => f.OpenAsync()), AppOcr()));
    }

    /// <summary>Opens (or activates) the workbench and runs the action on it (a no-op without a module).</summary>
    internal void ShowWorkbenchItem(Action<WorkbenchWindow> act)
    {
        ShowWorkbench();
        if (_workbenchWin is not { } w) return;
        w.Activate();
        act(w);
    }

    bool WorkbenchCommand()
    {
        if (_session == null) return false;
        Guard(ShowWorkbench);
        return true;
    }

    /// <summary>What is being typed in the workbench reaches the pack (before saves, flows and close questions).</summary>
    void FlushWorkbench() => _workbenchWin?.CommitPending();

    /// <summary>Before the session goes: the workbench closes.</summary>
    void DestroyWorkbench()
    {
        var w = _workbenchWin;
        if (w == null) return;
        try { w.Close(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing the workbench: " + ex.Message); }
        _workbenchWin = null;
    }
}
