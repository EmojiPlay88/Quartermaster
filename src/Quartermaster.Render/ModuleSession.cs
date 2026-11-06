using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>An open module (read-only) plus its caches, the current game and the active rules pack. UI-free.</summary>
public sealed class ModuleSession : IDisposable
{
    private readonly string? _rulesRoot;
    private Func<int, int, IReadOnlyList<int>> _roller = DefaultRoller;

    private static IReadOnlyList<int> DefaultRoller(int n, int sides) =>
        Enumerable.Range(0, n).Select(_ => Random.Shared.Next(1, sides + 1)).ToList();

    private ModuleSession(VmodArchive archive, ModuleModel model, DiagnosticLog log, string cacheDir, string? rulesRoot)
    {
        _rulesRoot = rulesRoot;
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

    public RulesPack? Rules { get; private set; }
    public string? RulesPath { get; private set; }
    public bool RulesDirty { get; private set; }
    public FormulaRunner? Runner { get; private set; }

    /// <summary>Raised after the rules pack changed (new, opened, edited or saved).</summary>
    public event Action? RulesChanged;

    /// <summary>Raised after <see cref="Game"/> is replaced (new game, setup or loaded save).</summary>
    public event Action? GameReplaced;

    /// <summary>Replaces the dice roller used by <see cref="Runner"/> (tests).</summary>
    public void SetRoller(Func<int, int, IReadOnlyList<int>> roller)
    {
        _roller = roller;
        if (Rules != null) Runner = new FormulaRunner(Rules, Model, _roller);
    }

    public static string DefaultCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster", "cache");

    public static ModuleSession Open(string vmodPath) => Open(vmodPath, DefaultCacheRoot, RulesMemory.DefaultRoot);

    /// <param name="rulesRoot">Where the remembered-rules index lives; null disables remembering.</param>
    public static ModuleSession Open(string vmodPath, string cacheRoot, string? rulesRoot = null)
    {
        var archive = VmodArchive.Open(vmodPath);
        try
        {
            var log = new DiagnosticLog();
            var model = ModuleModelBuilder.Build(archive, log);
            var dir = Path.Combine(cacheRoot, archive.Sha256);
            Directory.CreateDirectory(dir);
            var session = new ModuleSession(archive, model, log, dir, rulesRoot);
            session.AutoOpenRemembered();
            return session;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public void StartNewGame()
    {
        var state = NewGameBuilder.FromSetupStacks(Model, Log);
        Replace(state, $"New game: {state.Pieces.Count} pieces");
    }

    public void LoadSetup(SetupDef s)
    {
        if (s.FileEntry == null) { StartNewGame(); return; }
        if (!Archive.HasEntry(s.FileEntry))
            throw new InvalidDataException($"Setup '{s.Name}' refers to '{s.FileEntry}', which is not in the module.");
        using var entry = Archive.OpenEntry(s.FileEntry);
        var save = SaveFileReader.Read(entry);
        var state = SaveImporter.Import(save, Model, Log).State;
        Replace(state, $"Setup \"{s.Name}\" loaded: {state.Pieces.Count} pieces");
    }

    public void LoadNative(string path)
    {
        var loaded = NativeSave.Read(path);
        if (!string.Equals(loaded.ModuleSha256, Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This save was made with a different module and cannot be loaded here.");
        Game = loaded;
        WarnIfSaveRulesDiffer(loaded);
        SeedLedgers();
        GameReplaced?.Invoke();
    }

    public void SaveNative(string path)
    {
        if (Game == null) throw new InvalidOperationException("There is no game to save.");
        if (Rules != null) Game.RulesSha256 = SafeHash(Rules) ?? Game.RulesSha256;
        NativeSave.Write(path, Game);
    }

    // ---- rules ----

    public void NewRules(string name) =>
        Commit(RulesPackIo.CreateEmpty(name, Archive.Sha256), null, dirty: true);

    /// <summary>Loads a pack. Throws (e.g. InvalidDataException) without changing the session when it cannot be read.</summary>
    public void OpenRules(string path)
    {
        var pack = RulesPackIo.Load(path);
        Commit(pack, path, dirty: false);
        if (!string.Equals(pack.ModuleSha256, Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            Log.AddOnce("rules-module-mismatch:" + path, DiagnosticSeverity.Warning, "rules", "Rules pack was made for a different module", path);
        Remember(path);
    }

    public void SaveRules(string? path = null)
    {
        var pack = Rules ?? throw new InvalidOperationException("There are no rules to save.");
        var target = path ?? RulesPath ?? throw new InvalidOperationException("Choose where to save the rules pack.");
        RulesPackIo.Save(pack, target);
        var hash = RulesPackIo.ComputeSha256(pack);
        RulesPath = target;
        RulesDirty = false;
        if (Game != null) Game.RulesSha256 = hash;
        Remember(target);
        RulesChanged?.Invoke();
    }

    /// <summary>Applies the edit first; nothing changes (and no event fires) if it throws.</summary>
    public void UpdateRules(Func<RulesPack, RulesPack> edit)
    {
        var current = Rules ?? throw new InvalidOperationException("There are no rules to edit.");
        var edited = edit(current);
        var next = edited with
        {
            Id = string.IsNullOrEmpty(edited.Id) ? current.Id : edited.Id,
            ModuleSha256 = string.IsNullOrEmpty(edited.ModuleSha256) ? current.ModuleSha256 : edited.ModuleSha256,
        };
        var runner = new FormulaRunner(next, Model, _roller);
        Rules = next;
        Runner = runner;
        RulesDirty = true;
        SeedLedgers();
        RulesChanged?.Invoke();
    }

    private void Commit(RulesPack pack, string? path, bool dirty)
    {
        var runner = new FormulaRunner(pack, Model, _roller);
        Rules = pack;
        RulesPath = path;
        RulesDirty = dirty;
        Runner = runner;
        if (Game != null)
        {
            Game.RulesSha256 = SafeHash(pack);
            SeedLedgers();
        }
        RulesChanged?.Invoke();
    }

    private void Remember(string path)
    {
        if (_rulesRoot != null) RulesMemory.Remember(_rulesRoot, Archive.Sha256, path);
    }

    private void AutoOpenRemembered()
    {
        if (_rulesRoot == null) return;
        var path = RulesMemory.LastFor(_rulesRoot, Archive.Sha256);
        if (path == null) return;
        try
        {
            if (File.Exists(path) || Directory.Exists(path)) OpenRules(path);
        }
        catch (Exception ex)
        {
            Log.Add(DiagnosticSeverity.Warning, "rules", $"Could not open the remembered rules pack: {ex.Message}", path);
        }
    }

    private void WarnIfSaveRulesDiffer(GameSession loaded)
    {
        if (Rules == null || loaded.RulesSha256 == null) return;
        var hash = SafeHash(Rules);
        if (hash != null && !string.Equals(hash, loaded.RulesSha256, StringComparison.OrdinalIgnoreCase))
            Log.Add(DiagnosticSeverity.Warning, "rules", "Save was made with a different version of the rules pack");
    }

    private static string? SafeHash(RulesPack pack)
    {
        try { return RulesPackIo.ComputeSha256(pack); }
        catch (Exception) { return null; }
    }

    /// <summary>Executes a SeedLedger for sheet-bound properties not yet in the ledger. Never throws.</summary>
    private void SeedLedgers()
    {
        if (Game == null || Rules == null) return;
        try
        {
            var seed = LedgerSeeder.BuildSeeds(Game.State, Rules, _ => true, Model, Log);
            if (seed != null) Game.Execute(seed);
        }
        catch (Exception ex)
        {
            Log.Add(DiagnosticSeverity.Warning, "rules", $"Could not seed starting values: {ex.Message}");
        }
    }

    /// <summary>Starts a game from a freshly built state. The builders log one "placed" row per piece; replace those with a single summary entry.</summary>
    private void Replace(GameState state, string summary)
    {
        state.Log.Clear();
        state.Log.Add(new LogEntry(1, LogKind.System, summary, "local", null, null, DateTimeOffset.UtcNow));
        Game = new GameSession(state, Archive.Sha256);
        if (Rules != null) Game.RulesSha256 = SafeHash(Rules);
        SeedLedgers();
        GameReplaced?.Invoke();
    }

    public void Dispose() => Archive.Dispose();
}
