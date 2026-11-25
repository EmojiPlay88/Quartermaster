using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>The result of one <see cref="PackHarness.Run"/>. <c>Missing</c> lists the variables that could not be filled; <c>DiceUsed</c> counts dice served from the pinned list.</summary>
public sealed record HarnessRun(bool Applied, bool Refused, string? Error, string? Result, string Working, IReadOnlyList<string> Missing, int DiceUsed);

/// <summary>
/// The stable API for rules packs tested outside this repository: open a module and a pack, start a game, run formulas, advance phases and
/// read values and the log, without any window. Members other than <see cref="Session"/> keep their names and meaning across releases.
/// </summary>
/// <remarks>
/// The harness plays every side with the full game state (no side is held), so <see cref="Run"/>, <see cref="Value"/>, <see cref="Log"/> and
/// <see cref="Pending"/> show everything, hidden information included. It gives a test no way around the hidden-information rules for a
/// player: to check what a side sees, use <see cref="ViewFor"/>, which returns <c>PlayerView.For</c> (the same filter a guest or hot-seat
/// player gets).
/// </remarks>
public sealed class PackHarness : IDisposable
{
    private readonly ModuleSession _session;
    private readonly PhaseController _phases;
    private readonly string? _ownedCache;

    private PackHarness(ModuleSession session, string? ownedCache)
    {
        _session = session;
        _ownedCache = ownedCache;
        _phases = new PhaseController(session, (cmd, st) => CommandLog.Prepare(cmd, st, session));
    }

    /// <summary>Opens the module and the rules pack. <paramref name="cacheDir"/> null uses a temp folder deleted on <see cref="Dispose"/>.</summary>
    public static PackHarness Open(string vmodPath, string rulesPath, string? cacheDir = null)
    {
        var owned = cacheDir == null ? Path.Combine(Path.GetTempPath(), "qm-harness-" + Guid.NewGuid().ToString("N")) : null;
        var dir = cacheDir ?? owned!;
        ModuleSession? session = null;
        try
        {
            Directory.CreateDirectory(dir);
            session = ModuleSession.Open(vmodPath, dir, null);
            session.OpenRules(rulesPath);
            return new PackHarness(session, owned);
        }
        catch
        {
            session?.Dispose();
            DeleteQuietly(owned);
            throw;
        }
    }

    /// <summary>Starts a game from the setup named <paramref name="setup"/> (case-insensitive), or from the module's setup stacks when null.</summary>
    /// <exception cref="InvalidOperationException">No setup has that name.</exception>
    public PackHarness NewGame(string? setup = null, Difficulty difficulty = Difficulty.FullyAuto)
    {
        if (setup == null) { _session.StartNewGame(difficulty); return this; }
        var def = _session.Model.Setups.FirstOrDefault(x => string.Equals(x.Name, setup, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No setup named \"{setup}\"");
        _session.LoadSetup(def, difficulty);
        return this;
    }

    /// <summary>
    /// Runs a formula (auto values on) and applies it when it produced a result. A failed, refused or incomplete run changes nothing.
    /// </summary>
    /// <param name="pieceName">The unit the formula runs on: a unique piece name.</param>
    /// <param name="inputs">Typed values by variable name.</param>
    /// <param name="dice">Dice served in order to every roll; running out throws <see cref="InvalidOperationException"/>. Null rolls with the session roller.</param>
    public HarnessRun Run(string formulaId, string? pieceName = null, IReadOnlyDictionary<string, string>? inputs = null, IReadOnlyList<int>? dice = null)
    {
        var game = _session.Game ?? throw new InvalidOperationException("No game is open; call NewGame first");
        var runner = _session.Runner ?? throw new InvalidOperationException("No rules pack is open");
        var pieceId = pieceName == null ? null : PieceByName(pieceName).Id;
        var used = 0;
        var ranOut = false;
        var formulaName = _session.Rules?.ById<FormulaDef>(formulaId)?.Name ?? formulaId;
        Func<string, int, int, IReadOnlyList<int>>? serve = null;
        if (dice != null)
        {
            serve = (_, count, _) =>
            {
                if (used + count > dice.Count)
                {
                    ranOut = true;   // the runner turns a throw into a trace error, so the harness rethrows after the run
                    throw new InvalidOperationException($"The harness ran out of dice (formula \"{formulaName}\")");
                }
                var taken = dice.Skip(used).Take(count).ToList();
                used += count;
                return taken;
            };
        }
        var none = new Dictionary<string, string>();
        var request = new RunRequest(formulaId, pieceId, inputs ?? none, none, new Dictionary<string, IReadOnlyList<int>>(), null, true, serve);
        var trace = runner.Run(game.State, request);
        if (ranOut) throw new InvalidOperationException($"The harness ran out of dice (formula \"{formulaName}\")");
        var missing = trace.Variables.Where(v => v.MissingReason != null).Select(v => v.Variable).ToList();
        var applied = _phases.ApplyFormula(null, pieceId, trace, false);
        return new HarnessRun(applied, trace.Refused, trace.Error, trace.Result?.Display(), trace.Working ?? "", missing, used);
    }

    /// <summary>Presses Next Phase (starting the first phase when the game has none). Returns "Pass to {side} player" when the acting side changed, else null.</summary>
    public string? Advance() => _phases.Advance();

    /// <summary>The name of the current phase; null before the first phase starts or when the pack has none.</summary>
    public string? Phase =>
        _session.Game?.State.Position is { } pos && _session.Rules is { } pack ? pack.Phases.FirstOrDefault(p => p.Id == pos.PhaseId)?.Name : null;

    /// <summary>Runs waiting for input (every side's, in full).</summary>
    public IReadOnlyList<PendingRun> Pending => _session.Game?.State.Pending ?? (IReadOnlyList<PendingRun>)Array.Empty<PendingRun>();

    /// <summary>
    /// The effective value of a property. A stored property reads its stored value, or its Default when nothing was written (as the rules
    /// read it); a computed property is evaluated now and is null when it does not apply.
    /// </summary>
    /// <param name="holderOrPieceName">A holder's name or <c>Global</c> for the global holder (both ignore case), else a unique piece name (ordinal: exact case).</param>
    /// <exception cref="InvalidOperationException">No such holder or piece (or the piece name is not unique), or no such property.</exception>
    public double? Value(string holderOrPieceName, string propertyName)
    {
        var game = _session.Game ?? throw new InvalidOperationException("No game is open; call NewGame first");
        var pack = _session.Rules ?? throw new InvalidOperationException("No rules pack is open");
        string key;
        if (string.Equals(holderOrPieceName, "Global", StringComparison.OrdinalIgnoreCase)) key = LedgerKey.Holder(HolderDef.GlobalId);
        else if (pack.Holders.FirstOrDefault(h => h.Id != HolderDef.GlobalId && string.Equals(h.Name, holderOrPieceName, StringComparison.OrdinalIgnoreCase)) is { } holder)
            key = LedgerKey.Holder(holder.Id);
        else if (game.State.Pieces.Values.Any(p => PieceInfo.Name(p) == holderOrPieceName)) key = LedgerKey.Piece(PieceByName(holderOrPieceName).Id);
        else throw new InvalidOperationException($"No holder or piece named \"{holderOrPieceName}\"");
        var def = pack.Properties.FirstOrDefault(p => string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No property named \"{propertyName}\"");
        if (PropertyFacts.IsComputed(def)) return DisplayValues.Get(game.State, pack, _session.Model, key, def);
        return Ledger.Get(game.State, key, def.Id) ?? def.Default;
    }

    /// <summary>The full host log (every side's lines, nothing masked).</summary>
    public IReadOnlyList<LogEntry> Log => _session.Game?.State.Log ?? (IReadOnlyList<LogEntry>)Array.Empty<LogEntry>();

    /// <summary>What the given sides see: <c>PlayerView.For</c> of the current state. Use it to check masks; the rest of the harness is full-state.</summary>
    public GameState ViewFor(params string[] sideIds)
    {
        var game = _session.Game ?? throw new InvalidOperationException("No game is open; call NewGame first");
        return PlayerView.For(game.State, _session.Rules, _session.Model, new HashSet<string>(sideIds ?? Array.Empty<string>()));
    }

    /// <summary>The underlying session, for tests that need more. Not stable: it may change between releases.</summary>
    public ModuleSession Session => _session;

    private PieceInstance PieceByName(string name)
    {
        var found = _session.Game!.State.Pieces.Values.Where(p => PieceInfo.Name(p) == name).ToList();
        if (found.Count == 0) throw new InvalidOperationException($"No piece named \"{name}\"");
        if (found.Count > 1) throw new InvalidOperationException($"{found.Count} pieces are named \"{name}\"; use a unique name");
        return found[0];
    }

    public void Dispose()
    {
        _session.Dispose();
        DeleteQuietly(_ownedCache);
    }

    private static void DeleteQuietly(string? dir)
    {
        if (dir == null) return;
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
