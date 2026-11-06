using Quartermaster.Core;

namespace Quartermaster.App;

/// <summary>Tools menu windows: piece inventory and game notes (one of each), plus their smoke checks.</summary>
sealed partial class MainForm
{
    void ShowInventory()
    {
        if (_session == null) return;
        var s = _session;
        OpenChild(_docs, "inventory", () => new InventoryWindow(s, CentreOnPiece));
    }

    void ShowNotes()
    {
        if (_session == null) return;
        var s = _session;
        OpenChild(_docs, "notes", () => new NotesWindow(s));
    }

    /// <summary>Smoke: opens the inventory (optionally saving a screenshot) and the notes window; sets notes through the window and expects one "note" log entry.</summary>
    void SmokeInventoryNotes()
    {
        if (_opts.ScreenshotInventory != null)
        {
            ShowInventory();
            if (_docs.TryGetValue("inventory", out var f) && f is InventoryWindow iw && !iw.IsDisposed)
            {
                Program.AppLog($"smoke: inventory opened with {iw.RowCountForSmoke} group(s)");
                iw.SaveScreenshot(_opts.ScreenshotInventory);
            }
            else { Program.AppLog("smoke: inventory window did not open."); Program.ExitCode = 1; }
        }
        if (_opts.ScreenshotNotes == null) return;
        ShowNotes();
        if (_docs.TryGetValue("notes", out var n) && n is NotesWindow nw && !nw.IsDisposed && _session?.Game is { } game)
        {
            var notes = game.State.Log.Count(e => e.Kind == LogKind.Note);
            const string text = "Smoke notes: supply convoy arrives turn 3.";
            var got = nw.SetTextForSmoke(text);
            var again = nw.SetTextForSmoke(text); // unchanged text must not log again
            var added = game.State.Log.Count(e => e.Kind == LogKind.Note) - notes;
            var ok = got == text && again == text && game.State.Notes == text && added == 1;
            Program.AppLog($"smoke: notes {(ok ? "ok" : "FAILED")} ({added} note entr(y/ies) added)");
            if (!ok) Program.ExitCode = 1;
            nw.SaveScreenshot(_opts.ScreenshotNotes);
        }
        else { Program.AppLog("smoke: notes window did not open."); Program.ExitCode = 1; }
    }
}
