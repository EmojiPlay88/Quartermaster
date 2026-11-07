using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Smoke for Undo, Redo and Step Back (--smoke-undo, --screenshot-stepback). Meant for a fully automatic game so no dialogs open.</summary>
sealed partial class MainForm
{
    static string StateJson(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    void SmokeUndo()
    {
        if (_phases == null || _history == null || _session?.Game is not { } game || _session.Rules is not { Phases.Count: > 0 })
        {
            SmokeFail("undo smoke needs a game with a rules pack that has phases");
            return;
        }
        var h = _history;
        var failed = false;
        void Check(string step, bool ok, string detail = "")
        {
            Program.AppLog($"smoke: undo {step} {(ok ? "ok" : "FAILED")}{(detail.Length > 0 ? " (" + detail + ")" : "")}");
            if (!ok) failed = true;
        }
        CloseFormulaDialogs();
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        RefreshHistoryUi();
        Check("edit menu start", _editUndo != null && _editStepBackTo != null, $"'{_editUndo?.Text}' enabled {_editUndo?.Enabled}");

        // Move, Undo, Redo.
        var piece = game.State.Pieces.Values.FirstOrDefault(p => PieceInfo.Name(p) == "Red 1") ?? game.State.Pieces.Values.FirstOrDefault();
        if (piece == null) { SmokeFail("no piece to move"); return; }
        var beforeMove = StateJson(game.State);
        _phases.ExecuteMove(new[] { new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 40, piece.Pos.Y), null) });
        var afterMove = StateJson(game.State);
        Check("move changed state", beforeMove != afterMove);
        RefreshHistoryUi();
        Program.AppLog($"smoke: undo menu captions '{_editUndo?.Text}' / '{_editRedo?.Text}'");
        DoUndo();
        Check("undo restores the state", StateJson(game.State) == beforeMove);
        Check("redo available", h.CanRedo, _editRedo?.Text ?? "");
        DoRedo();
        Check("redo restores the state", StateJson(game.State) == afterMove);

        // Next Phase twice through the shortcut handler.
        for (var i = 0; i < 2; i++)
        {
            var before = game.State.Position;
            var moved = _handlers.TryGetValue("turn.next", out var nx) && nx();
            Application.DoEvents();
            CloseFormulaDialogs();
            Check($"next phase {i + 1}", moved && !Equals(before, game.State.Position), game.State.Position?.PhaseId ?? "none");
        }
        RefreshHistoryUi();
        Check("step back enabled", _editStepBack?.Enabled == true && _editStepBackTo?.Enabled == true);

        if (_opts.ScreenshotStepBack != null) SmokeStepBackShots(h);

        // Step Back to Start of Phase, then to game start.
        if (h.StartOfPhase() is not { } row) Check("start of phase row", false, "none");
        else
        {
            StepBackTo(row);
            Application.DoEvents();
            Check("step back to start of phase", game.Log.Any(x => x is SteppedBack) && h.CanRedo, row.Phase);
        }
        var first = h.Checkpoints().LastOrDefault();
        if (first == null) Check("game start row", false, "none");
        else
        {
            StepBackTo(first);
            Application.DoEvents();
            Check("step back to game start", game.State.Position == null && game.Log.Any(x => x is SteppedBack), first.Phase);
        }
        RefreshHistoryUi();
        if (failed) Program.ExitCode = 1;
    }

    /// <summary>Screenshots of the Step Back list dialog and the open Edit menu (suffix -menu), both drawn off-screen or in place.</summary>
    void SmokeStepBackShots(HistoryController h)
    {
        var path = _opts.ScreenshotStepBack!;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var rows = h.Checkpoints();
        using (var dlg = new StepBackDialog(rows, r => HistoryController.Describe(h.Preview(r)), confirmOnly: false))
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(-20000, -20000);
            dlg.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            dlg.Hide();
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Program.AppLog($"smoke: step back dialog screenshot saved to {path} ({rows.Count} row(s), summary '{dlg.SummaryText.Replace(Environment.NewLine, " / ")}')");
        }
        if (rows.Count > 0)
        {
            using var confirm = new StepBackDialog(new[] { rows[0] }, r => HistoryController.Describe(h.Preview(r)), confirmOnly: true);
            confirm.StartPosition = FormStartPosition.Manual;
            confirm.Location = new Point(-20000, -20000);
            confirm.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(confirm.Width, confirm.Height);
            confirm.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            confirm.Hide();
            bmp.Save(Path.ChangeExtension(path, null) + "-confirm.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        if (_editUndo?.Owner is ToolStripDropDownMenu dd)
        {
            try
            {
                dd.Show(new Point(Left + 60, Top + 80));
                Application.DoEvents();
                using var bmp = new Bitmap(dd.Width, dd.Height);
                dd.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                bmp.Save(Path.ChangeExtension(path, null) + "-menu.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            finally { dd.Close(); }
        }
    }
}
