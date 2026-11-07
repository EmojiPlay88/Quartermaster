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
    public GameSession? Game
    {
        get => _game;
        private set
        {
            if (ReferenceEquals(_game, value)) return;
            if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnGameRewound; }
            _game = value;
            if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnGameRewound; }
            ResetView();
        }
    }
    private GameSession? _game;
    private sealed record ViewCache(GameState? View);
    private volatile ViewCache? _cache;   // null = invalid; published as one immutable reference
    private IReadOnlySet<string>? _viewSides;

    private void OnGameChanged(GameCommand _) => InvalidateView();
    private void OnGameRewound() => InvalidateView();

    private void AfterGameReplaced()
    {
        // Session-owned so subscriber order never matters: a multi-player game starts public-only until a player takes over.
        if (_game != null) _viewSides = Players.IsSolo(_game.State) ? null : new HashSet<string>();
        ResetView();
        GameReplaced?.Invoke();
        ViewChanged?.Invoke();
    }

    private void ResetView() => _cache = null;

    private void InvalidateView()
    {
        ResetView();
        ViewChanged?.Invoke();
    }

    /// <summary>Raised once per view invalidation, on the thread that raised the change (game change, rewind, rules change, game replaced, sides changed). UIs debounce.</summary>
    public event Action? ViewChanged;

    /// <summary>The sides whose information is shown; null = everything (solo, or all sides held).</summary>
    public IReadOnlySet<string>? ViewSides => _viewSides;

    public void SetViewSides(IReadOnlySet<string>? sides)
    {
        _viewSides = sides == null ? null : new HashSet<string>(sides);
        InvalidateView();
    }

    /// <summary>What the current viewer may see: the game state itself when <see cref="ViewSides"/> is null, else a cached filtered copy
    /// (computed lazily on first access after an invalidation).</summary>
    public GameState? View
    {
        get
        {
            var game = _game;
            var sides = _viewSides;
            if (game == null) return null;
            if (sides == null) return game.State;
            var c = _cache;
            if (c == null)
            {
                c = new ViewCache(PlayerView.For(game.State, Rules, Model, sides));
                _cache = c;
            }
            return c.View;
        }
    }

    public bool CanSee(string pieceId)
    {
        var v = View;
        return v != null && pieceId != null && v.Pieces.ContainsKey(pieceId);
    }

    /// <summary>
    /// The side a formula applied now is run for (its log lines and rolls are stamped with it): null in solo; else the current phase's
    /// acting side when held, else the first held side in pack order.
    /// </summary>
    public string? ActorSide()
    {
        var sides = _viewSides;
        if (sides == null || sides.Count == 0) return null;
        var pack = Rules;
        if (pack != null && _game?.State is { } st && Sequencer.Current(pack, st) is { } phase
            && Sequencer.ActingSide(pack, phase)?.Id is { Length: > 0 } acting && sides.Contains(acting))
            return acting;
        foreach (var sd in pack?.Sides ?? new List<SideDef>())
            if (sd != null && sd.Id != null && sides.Contains(sd.Id)) return sd.Id;
        return sides.OrderBy(x => x, StringComparer.Ordinal).First();
    }

    /// <summary>Shown in place of a value the current viewer may not see.</summary>
    public const string HiddenText = "hidden";

    /// <summary>Why a formula on another side's unit was refused.</summary>
    public const string OtherSideText = "belongs to a side you don't hold";

    /// <summary>
    /// True when the current viewer may run and apply formulas on this unit: always in solo; for a unit-less run (null); else only for a
    /// visible unit that belongs to a held side or to no side.
    /// </summary>
    public bool CanAct(string? pieceId)
    {
        var sides = _viewSides;
        if (sides == null || pieceId == null) return true;
        if (_game?.State is not { } st || !st.Pieces.TryGetValue(pieceId, out var p) || p == null || !CanSee(pieceId)) return false;
        if (Rules is not { } pack) return true;
        var side = Ownership.SideOfPiece(p, st, pack);
        return side == null || sides.Contains(side);
    }

    /// <summary>True when the current viewer may change this ledger key by hand: always in solo; else a unit they may act on, the global
    /// holder or a holder of a held side or of no side.</summary>
    public bool CanEditLedger(string key)
    {
        var sides = _viewSides;
        if (sides == null) return true;
        if (key == null || !LedgerKey.TryParse(key, out var isPiece, out var id)) return false;
        if (isPiece) return CanAct(id);
        if (id == HolderDef.GlobalId) return true;
        if ((Rules?.Holders ?? new List<HolderDef>()).FirstOrDefault(h => h != null && h.Id == id) is not { } holder) return false;
        return Ownership.SideOfHolder(holder.Id, Rules!) is not { } side || sides.Contains(side);
    }

    /// <summary>Tooltip suffix of totals that cover only what the viewer may see.</summary>
    public const string VisibleOnlyText = "visible units only";

    /// <summary>True when the view leaves out or masks something of the game (a piece, a mask or a ledger value).</summary>
    public bool ViewFiltered
    {
        get
        {
            if (_viewSides == null || _game?.State is not { } full || View is not { } v || ReferenceEquals(v, full)) return false;
            if (v.Pieces.Count != full.Pieces.Count) return true;
            foreach (var (id, p) in v.Pieces)
                if (!full.Pieces.TryGetValue(id, out var f) || f?.TypeString != p?.TypeString) return true;
            static int Values(GameState st) => st.Ledgers.Values.Sum(inner => inner?.Count ?? 0);
            return Values(v) != Values(full);
        }
    }

    /// <summary>The text with " — visible units only" added when <see cref="ViewFiltered"/>.</summary>
    public string VisibleOnly(string text) => ViewFiltered ? text + " — " + VisibleOnlyText : text;

    public bool CanSeeMap(string mapId)
    {
        if (_viewSides == null || Rules == null) return true;
        var owner = Ownership.SideOfMap(mapId, Rules);
        return owner == null || _viewSides.Contains(owner);
    }
    public string CacheDir { get; }

    public RulesPack? Rules { get; private set; }
    public string? RulesPath { get; private set; }
    public bool RulesDirty { get; private set; }
    public FormulaRunner? Runner { get; private set; }

    /// <summary>Raised after the rules pack changed (new, opened, edited or saved).</summary>
    public event Action? RulesChanged;

    /// <summary>Raised (before <see cref="RulesChanged"/>) when a new, opened or edited pack was applied to the open game and re-seeded it.
    /// Not raised by a plain save. Undone actions are not safe to redo after this (spec 7).</summary>
    public event Action? RulesApplied;

    /// <summary>Raised after <see cref="Game"/> is replaced (new game, setup or loaded save).</summary>
    public event Action? GameReplaced;

    /// <summary>Replaces the dice roller used by <see cref="Runner"/> (tests).</summary>
    public void SetRoller(Func<int, int, IReadOnlyList<int>> roller)
    {
        _roller = roller;
        if (Rules != null) Runner = new FormulaRunner(Rules, Model, _roller);
    }

    /// <summary>Rolls n dice with the given sides using the session roller (the same one formulas use).</summary>
    public IReadOnlyList<int> Roll(int n, int sides) => _roller(n, sides);

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

    public void StartNewGame(Difficulty? difficulty = null)
    {
        var state = NewGameBuilder.FromSetupStacks(Model, Log);
        if (difficulty is { } d) state.Difficulty = d;
        Replace(state, $"New game: {state.Pieces.Count} pieces");
    }

    public void LoadSetup(SetupDef s, Difficulty? difficulty = null)
    {
        if (s.FileEntry == null) { StartNewGame(difficulty); return; }
        if (!Archive.HasEntry(s.FileEntry))
            throw new InvalidDataException($"Setup '{s.Name}' refers to '{s.FileEntry}', which is not in the module.");
        using var entry = Archive.OpenEntry(s.FileEntry);
        var save = SaveFileReader.Read(entry);
        var state = SaveImporter.Import(save, Model, Log).State;
        if (difficulty is { } d) state.Difficulty = d;
        Replace(state, $"Setup \"{s.Name}\" loaded: {state.Pieces.Count} pieces");
    }

    public void LoadNative(string path)
    {
        var loaded = NativeSave.Read(path);
        if (!string.Equals(loaded.ModuleSha256, Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This save was made with a different module and cannot be loaded here.");
        Game = loaded;
        if (loaded.LoadNotice is { } notice) Log.Add(DiagnosticSeverity.Warning, "save", notice);
        WarnIfSaveRulesDiffer(loaded);
        SeedLedgers();
        AfterGameReplaced();
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
        ResetView();
        RulesChanged?.Invoke();
        ViewChanged?.Invoke();
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
        ResetView();
        if (Game != null) RulesApplied?.Invoke();
        ResetView();
        RulesChanged?.Invoke();
        ViewChanged?.Invoke();
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
            ResetView();
            RulesApplied?.Invoke();
        }
        ResetView();
        RulesChanged?.Invoke();
        ViewChanged?.Invoke();
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
    public void SeedLedgers()
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
        if (string.IsNullOrEmpty(state.GameId)) state.GameId = Guid.NewGuid().ToString("N");
        Game = new GameSession(state, Archive.Sha256);
        if (Rules != null) Game.RulesSha256 = SafeHash(Rules);
        SeedLedgers();
        Game.Rebase();   // the start seed is part of genesis, not an undoable action
        AfterGameReplaced();
    }

    public bool Disposed { get; private set; }

    public void Dispose() { Disposed = true; Archive.Dispose(); }
}
