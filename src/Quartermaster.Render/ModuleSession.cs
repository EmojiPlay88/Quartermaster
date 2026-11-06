using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>An open module (read-only) plus its caches and the current game. UI-free.</summary>
public sealed class ModuleSession : IDisposable
{
    private ModuleSession(VmodArchive archive, ModuleModel model, DiagnosticLog log, string cacheDir)
    {
        Archive = archive;
        Model = model;
        Log = log;
        CacheDir = cacheDir;
        Images = new ImageSource(archive, cacheDir, log);
        Tiler = new BoardTiler(archive, cacheDir, log);
    }

    public VmodArchive Archive { get; }
    public ModuleModel Model { get; }
    public DiagnosticLog Log { get; }
    public ImageSource Images { get; }
    public BoardTiler Tiler { get; }
    public GameSession? Game { get; private set; }
    public string CacheDir { get; }

    /// <summary>Raised after <see cref="Game"/> is replaced (new game, setup or loaded save).</summary>
    public event Action? GameReplaced;

    public static string DefaultCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster", "cache");

    public static ModuleSession Open(string vmodPath) => Open(vmodPath, DefaultCacheRoot);

    public static ModuleSession Open(string vmodPath, string cacheRoot)
    {
        var archive = VmodArchive.Open(vmodPath);
        try
        {
            var log = new DiagnosticLog();
            var model = ModuleModelBuilder.Build(archive, log);
            var dir = Path.Combine(cacheRoot, archive.Sha256);
            Directory.CreateDirectory(dir);
            return new ModuleSession(archive, model, log, dir);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public void StartNewGame() =>
        Replace(NewGameBuilder.FromSetupStacks(Model, Log));

    public void LoadSetup(SetupDef s)
    {
        if (s.FileEntry == null) { StartNewGame(); return; }
        if (!Archive.HasEntry(s.FileEntry))
            throw new InvalidDataException($"Setup '{s.Name}' refers to '{s.FileEntry}', which is not in the module.");
        using var entry = Archive.OpenEntry(s.FileEntry);
        var save = SaveFileReader.Read(entry);
        Replace(SaveImporter.Import(save, Model, Log).State);
    }

    public void LoadNative(string path)
    {
        var loaded = NativeSave.Read(path);
        if (!string.Equals(loaded.ModuleSha256, Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This save was made with a different module and cannot be loaded here.");
        Game = loaded;
        GameReplaced?.Invoke();
    }

    public void SaveNative(string path)
    {
        if (Game == null) throw new InvalidOperationException("There is no game to save.");
        NativeSave.Write(path, Game);
    }

    private void Replace(GameState state)
    {
        Game = new GameSession(state, Archive.Sha256);
        GameReplaced?.Invoke();
    }

    public void Dispose() => Archive.Dispose();
}
