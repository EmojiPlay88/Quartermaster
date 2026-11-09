using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Outcome of a whole-map export. <see cref="Message"/> is the user-facing text; <see cref="Detail"/> (failures only) is the text after "The picture could not be saved: ".</summary>
public sealed record ExportResult(bool Ok, bool Cancelled, string Message, string? Detail = null);

/// <summary>UI-free "save map as picture" rules and background render, shared by WinForms and Desktop.</summary>
public static class MapExportFlow
{
    public const string AlreadyRunningText = "An export is already running";
    public const string NoGameText = "There is no game in progress.";
    public const string CancelledText = "Export cancelled";
    public const string OutOfMemoryText = "Not enough memory to render the map at this size. Try a smaller zoom.";
    public const string FailurePrefix = "The picture could not be saved: ";

    public static string SavedText(string path) => "Saved " + path;

    public static string SanitizeFileName(string mapName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(mapName.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "map" : clean;
    }

    /// <summary>The combo's 25 / 50 / 100 % choices.</summary>
    public static double ZoomForChoice(int index) => index switch { 0 => 0.25, 1 => 0.5, _ => 1.0 };

    static ExportResult Failed(string detail) => new(false, false, FailurePrefix + detail, detail);

    /// <summary>
    /// Renders the whole map from a snapshot of what this player may see and writes the PNG atomically. Safe off the UI thread; never throws.
    /// Tiles default to the session's tile cache (built on demand).
    /// </summary>
    public static async Task<ExportResult> RunWholeAsync(ModuleSession s, string mapId, double zoom, string path, IProgress<double>? p, CancellationToken ct,
        IReadOnlyDictionary<string, TileSet>? tiles = null, Action<string>? log = null)
    {
        if (s.Game == null) return new ExportResult(false, false, NoGameText, NoGameText);
        try
        {
            var snapshot = (s.View ?? s.Game.State).DeepClone();   // what this player may see; taken before any await, on the caller's thread
            var map = s.Model.Maps.FirstOrDefault(m => m.Id == mapId) ?? throw new InvalidOperationException("Unknown map: " + mapId);
            if (tiles == null)
            {
                var built = new Dictionary<string, TileSet>();
                foreach (var b in map.Boards)
                    built[BoardTiler.TileKey(b)] = await s.Tiler.EnsureTilesAsync(b, null, ct).ConfigureAwait(false);
                tiles = built;
            }
            return await Task.Run(() => Run(s, map, tiles, snapshot, zoom, path, p, ct, log), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new ExportResult(false, true, CancelledText); }
        catch (OutOfMemoryException) { return Failed(OutOfMemoryText); }
        catch (Exception ex)
        {
            log?.Invoke("map export failed: " + ex);
            return Failed(ex.Message);
        }
    }

    /// <summary>The synchronous core over a prepared snapshot.</summary>
    public static ExportResult Run(ModuleSession s, MapDef map, IReadOnlyDictionary<string, TileSet> tiles, GameState? snapshot, double zoom, string path,
        IProgress<double>? progress, CancellationToken ct, Action<string>? log = null)
    {
        if (snapshot == null) return new ExportResult(false, false, NoGameText, NoGameText);
        try
        {
            // A private renderer: a canvas renderer's tile cache belongs to the UI thread.
            var renderer = new MapRenderer(s.Images, new PieceRenderer(s.Images));
            using var bmp = MapImageExporter.Render(map, tiles, snapshot, s.Model, renderer, zoom, MapImageExporter.DefaultMaxLongSide, false, progress, ct);
            ct.ThrowIfCancellationRequested();
            MapImageExporter.SavePng(bmp, path);
            return new ExportResult(true, false, SavedText(path));
        }
        catch (OperationCanceledException) { return new ExportResult(false, true, CancelledText); }
        catch (OutOfMemoryException) { return Failed(OutOfMemoryText); }
        catch (Exception ex)
        {
            log?.Invoke("map export failed: " + ex);
            return Failed(ex.Message);
        }
    }
}
