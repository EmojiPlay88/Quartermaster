using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

public sealed class PieceInstance
{
    public string Id { get; set; } = "";
    /// <summary>Type string with prototypes already expanded (VASSAL PieceCloner semantics); never contains "prototype" traits unless one could not be resolved.</summary>
    public string TypeString { get; set; } = "";
    /// <summary>Per-trait state aligned 1:1 to the (prototype-expanded) <see cref="TypeString"/> trait chain.</summary>
    public List<string> TraitStates { get; set; } = new();
    public string? StackId { get; set; }
    public string MapId { get; set; } = "";
    public PointD Pos { get; set; }
    public List<PointD> Trail { get; set; } = new();
    public bool TrailVisible { get; set; }
    public bool Moved { get; set; }
}

public sealed class StackInstance
{
    public string Id { get; set; } = "";
    public string MapId { get; set; } = "";
    public PointD Pos { get; set; }
    /// <summary>Bottom to top.</summary>
    public List<string> PieceIds { get; set; } = new();
    public bool Expanded { get; set; }
}

public sealed class GameState
{
    /// <summary>Insertion order is the deterministic draw order.</summary>
    public OrderedDictionary<string, PieceInstance> Pieces { get; set; } = new();
    public OrderedDictionary<string, StackInstance> Stacks { get; set; } = new();
    public List<string> MapOrder { get; set; } = new();
    public long NextStackSeq { get; set; }
    /// <summary>Ledger key (see LedgerKey) -> property id -> value.</summary>
    public OrderedDictionary<string, OrderedDictionary<string, double>> Ledgers { get; set; } = new();
    public List<LedgerEntry> LedgerHistory { get; set; } = new();
    public List<DiceEntry> DiceHistory { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public string Notes { get; set; } = "";
    /// <summary>Empty = solo (one implicit player holding every side).</summary>
    public List<PlayerDef> Players { get; set; } = new();
    /// <summary>Identifies this game across saves (GUID "N" format); empty only in bare test states.</summary>
    public string GameId { get; set; } = "";
    public TurnState Turn { get; set; } = new(1, null);
    /// <summary>Null = before turn 1.</summary>
    public PhasePosition? Position { get; set; }
    public Difficulty Difficulty { get; set; } = Difficulty.SemiAuto;
    public List<int> Ticked { get; set; } = new();
    public List<int> Acknowledged { get; set; } = new();
    public List<PendingRun> Pending { get; set; } = new();
    /// <summary>Alert ids hidden until the turn number changes.</summary>
    public List<string> HiddenAlerts { get; set; } = new();

    public IEnumerable<StackInstance> StacksOn(string mapId) =>
        Stacks.Values.Where(s => s.MapId == mapId);

    public GameState DeepClone() =>
        JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(this, QuartermasterJson.Options), QuartermasterJson.Options)!;

    /// <summary>Deterministic reducer. Never throws on unknown ids.</summary>
    /// <summary>
    /// Highest trait-state index <see cref="SetTraitState"/> may pad up to. An index past both this and the piece's current states is ignored,
    /// so a hostile save or turn file cannot grow a piece without bound.
    /// </summary>
    public const int MaxTraitStates = 256;

    /// <summary>Applies one command. Never throws on unknown or missing (null) ids: such commands are ignored.</summary>
    public void Apply(GameCommand c)
    {
        switch (c)
        {
            case AddPiece a: DoAdd(a); break;
            case MovePieces m: DoMove(m); break;
            case RemovePiece r:
                if (r.PieceId != null && Pieces.TryGetValue(r.PieceId, out var rp))
                {
                    Log.Add(LogBuilder.Delete(this, r, rp.MapId));
                    DetachFromStack(rp);
                    Pieces.Remove(r.PieceId);
                    Ledgers.Remove(LedgerKey.Piece(r.PieceId));
                    PruneEmpty();
                }
                break;
            case SetTraitState t:
                if (t.PieceId != null && Pieces.TryGetValue(t.PieceId, out var tp) && t.StateIndex >= 0
                    && (t.StateIndex < tp.TraitStates.Count || t.StateIndex < MaxTraitStates))
                {
                    while (tp.TraitStates.Count <= t.StateIndex) tp.TraitStates.Add("");
                    tp.TraitStates[t.StateIndex] = t.NewState ?? "";
                }
                break;
            case SetStackExpanded e:
                if (e.StackId != null && Stacks.TryGetValue(e.StackId, out var es)) es.Expanded = e.Expanded;
                break;
            case ClearMoved cm:
                foreach (var p in Pieces.Values)
                    if (p.MapId == cm.MapId) p.Moved = false;
                break;
            case LedgerDelta ld: DoLedgerDelta(ld); break;
            case SeedLedger sl: DoSeed(sl); break;
            case RollDice rd:
                DiceHistory.Add(new DiceEntry(DiceHistory.Count + 1, rd.Purpose ?? "", rd.Count, rd.Sides, (rd.Results ?? Array.Empty<int>()).ToList()));
                Log.Add(LogBuilder.Dice(this, rd));
                break;
            case SetNotes sn:
                Notes = sn.Text ?? "";
                Log.Add(LogBuilder.Notes(this, sn));
                break;
            case SetTurn st:
                if (st.Turn != Turn.Turn) HiddenAlerts = new();
                Turn = new TurnState(st.Turn, st.Phase);
                Log.Add(LogBuilder.Turn(this, st));
                break;
            case AdvancePhase ap: DoAdvance(ap); break;
            case SetPlayers sp:
                Players = Core.Players.Clean(sp.Players);
                Log.Add(LogBuilder.Line(this, LogKind.System, LogBuilder.PlayersText(Players), sp.Who, sp.At));
                break;
            case SetDifficulty sd:
                Difficulty = sd.Difficulty;
                Log.Add(LogBuilder.Line(this, LogKind.System, $"Difficulty changed to {DifficultyText.Display(sd.Difficulty)}", sd.Who, sd.At));
                break;
            case TickChecklist tc:
                Ticked ??= new();
                if (tc.Ticked) { if (!Ticked.Contains(tc.Index)) Ticked.Add(tc.Index); }
                else Ticked.RemoveAll(i => i == tc.Index);
                Log.Add(LogBuilder.Line(this, LogKind.Turn, (tc.Ticked ? "Done: " : "Not done: ") + tc.Item, tc.Who, tc.At));
                break;
            case AcknowledgeWarning aw:
                Acknowledged ??= new();
                if (!Acknowledged.Contains(aw.Index)) Acknowledged.Add(aw.Index);
                Log.Add(LogBuilder.Line(this, LogKind.Turn, "Acknowledged: " + aw.Message, aw.Who, aw.At));
                break;
            case QueueRuns qr:
                Pending ??= new();
                foreach (var r in qr.Runs ?? Array.Empty<PendingRun>())
                    if (r != null && r.Units != null) Pending.Add(r);
                break;
            case ResolveRun rr: DoResolve(rr); break;
            case AddLogLine al:
                Log.Add(LogBuilder.Line(this, al.Kind ?? LogKind.System, al.Text ?? "", al.Who, al.At, al.PieceId, al.SideId, al.FormulaId));
                break;
            case HideAlert ha:
                HiddenAlerts ??= new();
                if (!string.IsNullOrEmpty(ha.AlertId) && !HiddenAlerts.Contains(ha.AlertId))
                {
                    HiddenAlerts.Add(ha.AlertId);
                    Log.Add(LogBuilder.Line(this, LogKind.System, "Alert hidden until next turn: " + (string.IsNullOrWhiteSpace(ha.Name) ? ha.AlertId : ha.Name),
                        ha.Who, ha.At));
                }
                break;
            case SteppedBack sb: Log.Add(LogBuilder.SteppedBack(this, sb)); break;
            case ToggleTrail tt:
                if (tt.PieceId != null && Pieces.TryGetValue(tt.PieceId, out var trp)) trp.TrailVisible = !trp.TrailVisible;
                break;
        }
    }

    void BatchSummary(PendingRun r, IReadOnlyList<PendingUnit> units, string? who, DateTimeOffset? at)
    {
        if (units.Count > 1)
            Log.Add(LogBuilder.Line(this, LogKind.Calc, $"{r.FormulaName}: {units.Count} units, {units.Count(u => u?.Applied == true)} applied", who, at,
                formulaId: LogKind.NoWorking));
    }

    void DoAdvance(AdvancePhase a)
    {
        if (a.To == null) return;
        foreach (var r in Pending ?? new())
        {
            if (r?.Units == null) continue;
            var open = r.Units.Where(u => u != null && !u.Applied).ToList();
            if (open.Count > 0)
            {
                var labels = string.Join(", ", open.Take(3).Select(u => u.Label));
                if (open.Count > 3) labels += $" +{open.Count - 3} more";
                Log.Add(LogBuilder.Line(this, LogKind.Calc, $"skipped: {r.FormulaName}, {labels}", a.Who, a.At, formulaId: LogKind.NoWorking));
            }
            BatchSummary(r, r.Units, a.Who, a.At);
        }
        if (a.To.Turn != Turn.Turn) HiddenAlerts = new();
        Position = a.To;
        Turn = new TurnState(a.To.Turn, a.ToName);
        Ticked = new(); Acknowledged = new(); Pending = new();
        if (!string.IsNullOrEmpty(a.Reason))
            Log.Add(LogBuilder.Line(this, LogKind.System, a.Reason, a.Who, a.At));
        else
            Log.Add(LogBuilder.Line(this, LogKind.Turn,
                a.From == null ? $"Turn {a.To.Turn} · {a.ToName}" : $"Turn {a.To.Turn} · {a.FromName} → {a.ToName}", a.Who, a.At));
    }

    void DoResolve(ResolveRun rr)
    {
        Pending ??= new();
        var idx = Pending.FindIndex(r => r != null && r.Id == rr.RunId);
        if (idx < 0) return;
        var run = Pending[idx];
        var ids = new HashSet<string?>(rr.PieceIds ?? Array.Empty<string?>());
        var units = (run.Units ?? Array.Empty<PendingUnit>()).Where(u => u != null).Select(u => !u.Applied && ids.Contains(u.PieceId) ? u with { Applied = true } : u).ToList();
        if (units.All(u => u.Applied))
        {
            Pending.RemoveAt(idx);
            BatchSummary(run, units, rr.Who, rr.At);
        }
        else Pending[idx] = run with { Units = units };
    }

    void SetLedger(string key, string propertyId, double value)
    {
        if (!Ledgers.TryGetValue(key, out var inner)) Ledgers[key] = inner = new();
        inner[propertyId] = value;
    }

    void DoLedgerDelta(LedgerDelta d)
    {
        if (d.Key == null || d.PropertyId == null || !double.IsFinite(d.After)) return;
        SetLedger(d.Key, d.PropertyId, d.After);
        LedgerHistory.Add(new LedgerEntry(LedgerHistory.Count + 1, d.Key, d.PropertyId, double.IsFinite(d.Before) ? d.Before : 0,
            d.After, d.Source ?? "manual", d.FormulaId, d.RuleRefId, d.Note, d.Working, Turn.Turn, Turn.Phase));
        Log.Add(LogBuilder.Ledger(this, d));
    }

    void DoSeed(SeedLedger sl)
    {
        if (sl.Seeds == null) return;
        foreach (var s in sl.Seeds)
        {
            if (s == null || s.Key == null || s.PropertyId == null || !double.IsFinite(s.Value)) continue;
            if (Ledgers.TryGetValue(s.Key, out var inner) && inner.ContainsKey(s.PropertyId)) continue;
            SetLedger(s.Key, s.PropertyId, s.Value);
            LedgerHistory.Add(new LedgerEntry(LedgerHistory.Count + 1, s.Key, s.PropertyId, 0, s.Value, "seed", null, null, null, null, Turn.Turn, Turn.Phase));
        }
    }

    void DoAdd(AddPiece a)
    {
        if (a.PieceId == null || a.TypeString == null || a.MapId == null || Pieces.ContainsKey(a.PieceId)) return;
        var p = new PieceInstance
        {
            Id = a.PieceId, TypeString = a.TypeString, MapId = a.MapId, Pos = a.Pos,
            TraitStates = DecodeState(a.TypeString, a.StateString ?? ""),
        };
        Pieces[a.PieceId] = p;
        Place(new[] { p }, a.MapId, a.Pos, a.StackOnto);
        Log.Add(LogBuilder.Place(this, a));
    }

    /// <summary>Splits a piece state string into per-trait states as the reducer does (one entry with the raw text when the type cannot be read).</summary>
    public static List<string> DecodeState(string typeString, string stateString)
    {
        if (string.IsNullOrEmpty(stateString)) return new();
        try
        {
            var count = PieceTypeParser.Parse(typeString, new DiagnosticLog(), null).Traits.Count;
            return PieceTypeParser.SplitState(stateString, count).ToList();
        }
        catch (Exception)
        {
            return new() { stateString };
        }
    }

    void DoMove(MovePieces m)
    {
        if (m.MapId == null) return;
        var moved = new List<PieceInstance>();
        foreach (var id in m.PieceIds ?? Array.Empty<string>())
            if (id != null && Pieces.TryGetValue(id, out var p) && !moved.Contains(p)) moved.Add(p);
        if (moved.Count == 0) return;
        foreach (var p in moved) DetachFromStack(p);
        Place(moved, m.MapId, m.Pos, m.StackOnto);
        foreach (var p in moved) { p.Moved = true; p.Trail.Add(p.Pos); }
        PruneEmpty();
        Log.Add(LogBuilder.Move(this, m, moved.Count, moved[0].Id));
    }

    void Place(IReadOnlyList<PieceInstance> ps, string mapId, PointD pos, string? onto)
    {
        StackInstance st;
        if (onto != null && Stacks.TryGetValue(onto, out var existing) && existing.MapId == mapId)
            st = existing;
        else
        {
            st = new StackInstance { Id = "s:" + ps[0].Id + ":" + NextStackSeq++, MapId = mapId, Pos = pos };
            Stacks[st.Id] = st;
        }
        foreach (var p in ps)
        {
            p.StackId = st.Id; p.MapId = mapId; p.Pos = st.Pos;
            st.PieceIds.Add(p.Id);
        }
    }

    void DetachFromStack(PieceInstance p)
    {
        if (p.StackId != null && Stacks.TryGetValue(p.StackId, out var s)) s.PieceIds.Remove(p.Id);
        p.StackId = null;
    }

    void PruneEmpty()
    {
        var empty = Stacks.Where(kv => kv.Value.PieceIds.Count == 0).Select(kv => kv.Key).ToList();
        foreach (var k in empty) Stacks.Remove(k);
    }
}

/// <summary>JSON converter that preserves OrderedDictionary order.</summary>
sealed class OrderedDictionaryConverter<T> : JsonConverter<OrderedDictionary<string, T>>
{
    public override OrderedDictionary<string, T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected object.");
        var d = new OrderedDictionary<string, T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            d[key] = JsonSerializer.Deserialize<T>(ref reader, options)!;
        }
        return d;
    }

    public override void Write(Utf8JsonWriter writer, OrderedDictionary<string, T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WritePropertyName(kv.Key);
            JsonSerializer.Serialize(writer, kv.Value, options);
        }
        writer.WriteEndObject();
    }
}

sealed class OrderedDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(OrderedDictionary<,>) && t.GetGenericArguments()[0] == typeof(string);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(OrderedDictionaryConverter<>).MakeGenericType(t.GetGenericArguments()[1]))!;
}

public static class QuartermasterJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new OrderedDictionaryConverterFactory() },
    };
}
