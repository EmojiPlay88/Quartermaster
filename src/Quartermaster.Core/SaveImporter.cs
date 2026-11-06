// Ported from VASSAL BasicCommandEncoder.java (AddPiece "+/id/type/state"), Stack.java (getState/setState), BasicPiece.java (getState/setState),
// BoardPicker.java (SetBoards encode/decode), PlayerRoster.java (PLAYER command) and GameState.java (begin_save/end_save) (LGPL-2.1).
using Quartermaster.Vassal;

namespace Quartermaster.Core;

public sealed record ImportResult(GameState State, int PiecesImported, int CommandsSkipped);

/// <summary>Turns a decoded VASSAL save into a <see cref="GameState"/>. Never throws on odd input; problems go to the log.</summary>
public static class SaveImporter
{
    private const string AddPrefix = "+/";
    private const string PlayerPrefix = "PLAYER\t";
    private const string BoardPickerId = "BoardPicker";
    private const string LayerMarker = "@@";
    private const string NullMap = "null";

    private sealed record Loose(string Id, string Type, string State, string? Map, PointD Pos);
    private sealed record StackCmd(string? Map, PointD Pos, List<string> Ids);

    public static ImportResult Import(SaveFile save, ModuleModel model, DiagnosticLog log)
    {
        var state = new GameState();
        foreach (var m in model.Maps) state.MapOrder.Add(m.Id);
        var mapIds = new HashSet<string>(model.Maps.Select(m => m.Id), StringComparer.Ordinal);

        var pieces = new Dictionary<string, Loose>(StringComparer.Ordinal);
        var items = new List<object>(); // Loose or StackCmd, in save order
        var skipped = 0;
        var playerLogged = false;

        foreach (var cmd in save.Commands)
        {
            if (string.IsNullOrEmpty(cmd) || cmd == "begin_save" || cmd == "end_save") continue;

            if (cmd.StartsWith(AddPrefix, StringComparison.Ordinal))
            {
                var d = new SequenceDecoder(cmd.Substring(AddPrefix.Length), '/');
                var id = d.NextToken();
                var type = d.NextToken();
                var st = d.NextToken();
                if (type == "stack") items.Add(ReadStack(st));
                else
                {
                    if (id == NullMap || id.Length == 0)
                    {
                        log.Add(DiagnosticSeverity.Warning, "save", "A piece without an id was skipped.");
                        continue;
                    }
                    if (pieces.ContainsKey(id))
                    {
                        log.AddOnce("save:dup:" + id, DiagnosticSeverity.Warning, "save", $"Piece id '{id}' appears more than once; the first was kept.", id);
                        continue;
                    }
                    var piece = ReadPiece(id, type, st, model, log);
                    pieces[id] = piece;
                    items.Add(piece);
                }
            }
            else if (cmd.StartsWith(PlayerPrefix, StringComparison.Ordinal))
            {
                if (!playerLogged)
                {
                    playerLogged = true;
                    log.Add(DiagnosticSeverity.Info, "save", "Player roster (PLAYER) information in the save is ignored.");
                }
            }
            else if (TryBoardPicker(cmd, model, log)) { }
            else if (IsFramingNoise(cmd))
            {
                log.AddOnce("save:framing", DiagnosticSeverity.Info, "save", "Setup-stack markers and empty command separators in the save are ignored.");
            }
            else
            {
                skipped++;
                var prefix = cmd.Length > 12 ? cmd.Substring(0, 12) : cmd;
                log.AddOnce("save:unknown:" + prefix, DiagnosticSeverity.Warning, "save", $"Unknown save command starting '{Printable(prefix)}' skipped.", Printable(prefix));
            }
        }

        // Pieces named by a stack are placed through the stack, wherever their own command sits.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in items.OfType<StackCmd>())
            foreach (var id in st.Ids)
                if (pieces.ContainsKey(id)) claimed.Add(id);

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var offMap = 0;
        var imported = 0;
        foreach (var item in items)
        {
            if (item is Loose p)
            {
                if (claimed.Contains(p.Id)) continue;
                imported++;
                if (p.Map == null) { AddUnplaced(state, p); offMap++; }
                else
                {
                    WarnUnknownMap(p.Map, mapIds, log);
                    state.Apply(new AddPiece(p.Id, p.Type, p.State, p.Map, p.Pos, null));
                }
                placed.Add(p.Id);
            }
            else if (item is StackCmd s)
            {
                string? stackId = null;
                if (s.Map != null) WarnUnknownMap(s.Map, mapIds, log);
                foreach (var id in s.Ids)
                {
                    if (!pieces.TryGetValue(id, out var member))
                    {
                        if (!id.StartsWith(LayerMarker, StringComparison.Ordinal))
                            log.AddOnce("save:missing:" + id, DiagnosticSeverity.Warning, "save", $"A stack names piece '{id}' which is not in the save.", id);
                        continue;
                    }
                    if (!placed.Add(id))
                    {
                        log.AddOnce("save:twice:" + id, DiagnosticSeverity.Warning, "save", $"Piece '{id}' is in more than one stack; the first was kept.", id);
                        continue;
                    }
                    imported++;
                    if (s.Map == null) { AddUnplaced(state, member); offMap++; continue; }
                    state.Apply(new AddPiece(member.Id, member.Type, member.State, s.Map, s.Pos, stackId));
                    stackId ??= state.Pieces[member.Id].StackId;
                }
            }
        }

        if (offMap > 0)
            log.Add(DiagnosticSeverity.Info, "save", $"{offMap} piece(s) in the save are off-map and were kept unplaced.");
        return new ImportResult(state, imported, skipped);
    }

    /// <summary>VASSAL framing that carries no game state: a "SETUP_STACK" marker (GameState) or a lone command separator.</summary>
    private static bool IsFramingNoise(string cmd) =>
        cmd == "\\" || cmd == "SETUP_STACK" || cmd.StartsWith("SETUP_STACK	", StringComparison.Ordinal);

    private static string Printable(string s) => s.Replace("\t", "\\t").Replace("\u001B", "\\e");

    private static void WarnUnknownMap(string map, HashSet<string> known, DiagnosticLog log)
    {
        if (!known.Contains(map))
            log.AddOnce("save:map:" + map, DiagnosticSeverity.Warning, "save", $"The save places pieces on map '{map}', which the module does not define.", map);
    }

    private static void AddUnplaced(GameState state, Loose p)
    {
        var count = PieceTypeParser.Parse(p.Type, new DiagnosticLog(), null).Traits.Count;
        state.Pieces[p.Id] = new PieceInstance
        {
            Id = p.Id,
            TypeString = p.Type,
            MapId = "",
            TraitStates = string.IsNullOrEmpty(p.State) ? new() : PieceTypeParser.SplitState(p.State, count).ToList(),
        };
    }

    /// <summary>Stack state (Stack.java): "mapId;x;y;pieceId;...;@@layer". With a null map there is no position.</summary>
    private static StackCmd ReadStack(string state)
    {
        var d = new SequenceDecoder(state, ';');
        var map = d.NextToken();
        var pos = new PointD(0, 0);
        var onMap = map != NullMap;
        if (onMap) pos = new PointD(d.NextInt(0), d.NextInt(0));
        var ids = new List<string>();
        while (d.HasMoreTokens) ids.Add(d.NextToken());
        return new StackCmd(onMap ? map : null, pos, ids);
    }

    private static Loose ReadPiece(string id, string type, string state, ModuleModel model, DiagnosticLog log)
    {
        var parsed = PieceTypeParser.Parse(type, new DiagnosticLog(), null);
        if (parsed.Traits.Any(t => t is PrototypeRef))
        {
            log.AddOnce("save:expand", DiagnosticSeverity.Info, "save", "Pieces in the save still reference prototypes; they were expanded.");
            type = model.Expander.ExpandTypeAndState(type, state, out state);
            parsed = PieceTypeParser.Parse(type, new DiagnosticLog(), null);
        }
        // BasicPiece state (innermost): "mapId;x;y;gpid;propCount;..."
        var basic = PieceTypeParser.SplitState(state, parsed.Traits.Count)[^1];
        var d = new SequenceDecoder(basic, ';');
        var map = d.NextToken();
        var pos = new PointD(d.NextInt(0), d.NextInt(0));
        return new Loose(id, type, state, map == NullMap || map.Length == 0 ? null : map, pos);
    }

    /// <summary>BoardPicker command: "&lt;mapId&gt;BoardPicker\tname[/rev]\tx\ty...". Informational in piece 1; all of a map's boards are shown.</summary>
    private static bool TryBoardPicker(string cmd, ModuleModel model, DiagnosticLog log)
    {
        var tab = cmd.IndexOf('\t');
        if (tab < 0) return false;
        var head = cmd.Substring(0, tab);
        if (!head.EndsWith(BoardPickerId, StringComparison.Ordinal)) return false;
        var mapId = head.Substring(0, head.Length - BoardPickerId.Length);

        var d = new SequenceDecoder(cmd, '\t');
        d.NextToken();
        var names = new List<string>();
        while (d.HasMoreTokens)
        {
            names.Add(SequenceEncoder.Decode(d.NextToken(), '/').FirstOrDefault() ?? "");
            d.NextToken(); d.NextToken(); // relative x, y
        }
        var map = model.Maps.FirstOrDefault(m => m.Id == mapId);
        var differs = map != null && !map.Boards.Select(b => b.Name).Order().SequenceEqual(names.Order());
        log.AddOnce("save:boards:" + mapId, DiagnosticSeverity.Info, "save",
            differs
                ? $"Save selects boards [{string.Join(", ", names)}] for map '{mapId}', which differs from the module's boards; all of the map's boards are shown."
                : $"Save board selection for map '{mapId}' noted.", mapId);
        return true;
    }
}
