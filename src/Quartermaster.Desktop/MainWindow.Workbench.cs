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
    OcrLanguageService? _ocrLanguages;

    /// <summary>Makes the app's OCR engine (Windows OCR or Tesseract, see <see cref="OcrEngines"/>) over the app's language downloads; tests replace it.</summary>
    internal static Func<Action<string>, OcrLanguageService, IOcrRecognizer> CreateOcr = (log, downloads) => OcrEngines.Create(log: log, downloads: downloads);

    /// <summary>Makes the app's OCR language download service; tests replace it.</summary>
    internal static Func<Action<string>, OcrLanguageService> CreateOcrLanguages = log => new OcrLanguageService(log: log);

    /// <summary>
    /// The one OCR language download service of the app (Settings > OCR languages and the recognizer): made on first use, disposed when the main
    /// window closes. Its hash cache is warmed on a worker at once, so the first status read on the UI side does not hash multi-MB files.
    /// </summary>
    internal OcrLanguageService OcrLanguages
    {
        get
        {
            if (_ocrLanguages != null) return _ocrLanguages;
            var ui = Avalonia.Threading.Dispatcher.UIThread;   // taken here: the log is called on download workers
            var service = _ocrLanguages = CreateOcrLanguages(m =>
            {
                System.Diagnostics.Trace.WriteLine(m);
                ui.Post(() => _session?.Log.Add(DiagnosticSeverity.Info, "ocr", m, ""));
            });
            Closed += (_, _) => service.Dispose();
            _ = Task.Run(() =>
            {
                try { service.InstalledCodes(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("ocr languages: " + ex.Message); }
            });
            return service;
        }
    }

    /// <summary>The one OCR engine of the app: made on the first Workbench open, disposed when the main window closes (app exit),
    /// after the session teardown has closed the workbench.</summary>
    IOcrRecognizer AppOcr()
    {
        if (_ocrEngine != null) return _ocrEngine;
        var engine = _ocrEngine = CreateOcr(m => _session?.Log.Add(DiagnosticSeverity.Info, "ocr", m, ""), OcrLanguages);
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
            () => new WorkbenchWindow(s, _dialogs, () => RulesFlowAsync(f => f.NewAsync()), () => RulesFlowAsync(f => f.OpenAsync()), AppOcr(),
                () => ShowSettingsAsync(Preferences.OcrLanguagesPage.PageKey)));
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

    /// <summary>
    /// Before a received turn replays (the rules stay as they are meanwhile): the unsaved-rules question first (Yes saves, No keeps the edits
    /// in the session unsaved, Cancel keeps the workbench and cancels the open), then the workbench closes. True when none is open.
    /// </summary>
    internal async Task<bool> CloseWorkbenchForReplayAsync()
    {
        if (_workbenchWin == null) return true;
        if (!await ConfirmRulesSavedAsync()) return false;
        DestroyWorkbench();
        return true;
    }

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
