using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>"Save map as picture": the background whole-map render and the dialogs around it.</summary>
static class MapExport
{
    static bool _running;

    /// <summary>Smoke/helper overload: snapshots the canvas map's game and renders it.</summary>
    public static string? RunWhole(ModuleSession s, MapCanvas canvas, double zoom, string path, IProgress<double>? progress, CancellationToken ct)
        => RunWhole(s, canvas.Map, canvas.Tiles, s.View?.DeepClone(), zoom, path, progress, ct);

    /// <summary>
    /// Renders the whole map from a snapshot of the game and writes the PNG atomically. Safe off the UI thread. Returns null on success,
    /// "Cancelled" when cancelled, otherwise an error text; it never throws.
    /// </summary>
    public static string? RunWhole(ModuleSession s, MapDef map, IReadOnlyDictionary<string, TileSet> tiles, GameState? snapshot, double zoom, string path,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (snapshot == null) return "There is no game in progress.";
        try
        {
            // A private renderer: the canvas renderer's tile cache belongs to the UI thread.
            var renderer = new MapRenderer(s.Images, new PieceRenderer(s.Images));
            using var bmp = MapImageExporter.Render(map, tiles, snapshot, s.Model, renderer, zoom, MapImageExporter.DefaultMaxLongSide, false, progress, ct);
            ct.ThrowIfCancellationRequested();
            MapImageExporter.SavePng(bmp, path);
            return null;
        }
        catch (OperationCanceledException) { return "Cancelled"; }
        catch (OutOfMemoryException)
        {
            return "Not enough memory to render the map at this size. Try a smaller zoom.";
        }
        catch (Exception ex)
        {
            Program.AppLog("map export failed: " + ex);
            return ex.Message;
        }
    }

    /// <summary>The user-facing flow, started from the map window.</summary>
    public static async Task ShowAsync(MapWindow owner, ModuleSession s, MapCanvas canvas, Action<string> status)
    {
        if (_running) { status("An export is already running"); return; }
        _running = true;
        try { await ShowCoreAsync(owner, s, canvas, status); }
        finally { _running = false; }
    }

    static async Task ShowCoreAsync(MapWindow owner, ModuleSession s, MapCanvas canvas, Action<string> status)
    {
        if (s.Game == null) { MessageBox.Show(owner, "There is no game in progress.", "Save map as picture", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var choice = new ExportChoiceDialog(canvas.Map.Name);
        if (choice.ShowDialog(owner) != DialogResult.OK) return;
        using var save = new SaveFileDialog { Filter = "PNG picture (*.png)|*.png", DefaultExt = "png", FileName = Sanitize(canvas.Map.Name) + ".png", Title = "Save map as picture" };
        if (save.ShowDialog(owner) != DialogResult.OK) return;
        var path = save.FileName;

        if (choice.Zoom is not { } zoom)
        {
            try
            {
                using var bmp = canvas.Snapshot();
                MapImageExporter.SavePng(bmp, path);
                status("Saved " + path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Program.AppLog("map export failed: " + ex);
                MessageBox.Show(owner, "The picture could not be saved: " + ex.Message, "Save map as picture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        var snapshot = (s.View ?? s.Game.State).DeepClone();   // what this player may see
        var tiles = canvas.Tiles;
        var map = canvas.Map;
        using var cts = new CancellationTokenSource();
        using var dlg = new ExportProgressDialog(cts);
        var progress = new Progress<double>(p => { if (!dlg.IsDisposed) dlg.SetProgress(p); });
        FormClosedEventHandler onClosed = (_, _) => { try { cts.Cancel(); } catch (ObjectDisposedException) { } };
        owner.FormClosed += onClosed;
        dlg.Show(owner);
        string? error;
        try { error = await Task.Run(() => RunWhole(s, map, tiles, snapshot, zoom, path, progress, cts.Token)); }
        finally { owner.FormClosed -= onClosed; if (!dlg.IsDisposed) dlg.Close(); }
        if (owner.IsDisposed) return;
        if (error == null) status("Saved " + path);
        else if (error == "Cancelled") status("Export cancelled");
        else MessageBox.Show(owner, "The picture could not be saved: " + error, "Save map as picture", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "map" : clean;
    }
}

/// <summary>Current view, or the whole map at 25, 50 or 100%.</summary>
sealed class ExportChoiceDialog : Form
{
    readonly RadioButton _view = new() { Text = "Current view", Checked = true, AutoSize = true };
    readonly RadioButton _whole = new() { Text = "Whole map at", AutoSize = true };
    readonly ComboBox _zoom = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Enabled = false };

    public ExportChoiceDialog(string mapName)
    {
        Text = "Save map as picture";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(300, 130);
        _zoom.Items.AddRange(["25%", "50%", "100%"]);
        _zoom.SelectedIndex = 0;
        _whole.CheckedChanged += (_, _) => _zoom.Enabled = _whole.Checked;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(130, 92), Size = new Size(75, 23) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(212, 92), Size = new Size(75, 23) };
        Controls.Add(new Label { Text = mapName, AutoEllipsis = true, Location = new Point(12, 10), Size = new Size(276, 16) });
        _view.Location = new Point(14, 34);
        _whole.Location = new Point(14, 60);
        _zoom.Location = new Point(110, 57);
        Controls.AddRange([_view, _whole, _zoom, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Null for the current view, else the zoom factor.</summary>
    public double? Zoom => _whole.Checked ? _zoom.SelectedIndex switch { 0 => 0.25, 1 => 0.5, _ => 1.0 } : null;
}

sealed class ExportProgressDialog : Form
{
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Location = new Point(12, 36), Size = new Size(276, 18) };

    public ExportProgressDialog(CancellationTokenSource cts)
    {
        Text = "Saving map as picture";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(300, 100);
        var cancel = new Button { Text = "Cancel", Location = new Point(212, 66), Size = new Size(75, 23) };
        cancel.Click += (_, _) => { cancel.Enabled = false; cts.Cancel(); };
        Controls.Add(new Label { Text = "Rendering...", Location = new Point(12, 12), AutoSize = true });
        Controls.AddRange([_bar, cancel]);
    }

    public void SetProgress(double p) => _bar.Value = (int)Math.Clamp(Math.Round(p * 100), 0, 100);
}
