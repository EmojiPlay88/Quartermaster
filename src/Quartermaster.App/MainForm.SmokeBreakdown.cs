using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Smoke for the ledger breakdown tooltip (--screenshot-breakdown): popup with a hovered ledger line, captured from the screen.</summary>
sealed partial class MainForm
{
    void SmokeBreakdown()
    {
        if (_phases == null || _session?.Game is not { } game || _session.Rules == null) { SmokeFail("breakdown smoke needs a game with a rules pack"); return; }
        CloseFormulaDialogs();
        if (_phases.View().BeforeStart) { _phases.Start(); Application.DoEvents(); }
        var piece = game.State.Pieces.Values.FirstOrDefault(p => p.StackId != null && game.State.Stacks.ContainsKey(p.StackId));
        if (piece == null) { SmokeFail("no piece in a stack"); return; }
        var key = LedgerKey.Piece(piece.Id);
        game.Execute(new LedgerDelta(key, "fuel", Ledger.Get(game.State, key, "fuel") ?? 0, 40, "manual", null, null, "starting load", null));
        game.Execute(new LedgerDelta(key, "fuel", 40, 31, "manual", null, null, "forced march", null));
        game.Execute(new LedgerDelta(key, "water", 10, 7, "manual", null, null, "heat", null));
        Application.DoEvents();
        var stack = game.State.Stacks[piece.StackId!];
        var pieces = new PieceRenderer(_session.Images);
        var popup = CounterDetailPopup.Create(_session, game.State, stack, pieces, DeviceDpi / 96f);
        try
        {
            popup.ShowAt(new Point(Left + 120, Top + 160));
            Application.DoEvents();
            var hovered = popup.SmokeHoverFirstLine();
            for (var i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(120); }
            var r = Rectangle.Inflate(popup.Bounds, 40, 40);
            r.Width += 420; r.Height += 180;
            r.Intersect(Screen.FromControl(this).Bounds);
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            var path = _opts.ScreenshotBreakdown!;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Program.AppLog($"smoke: breakdown screenshot saved to {path} (interactive {popup.Interactive}, hovered {hovered})");
            if (!hovered) Program.ExitCode = 1;
        }
        finally { popup.Close(); popup.Dispose(); }
    }
}
