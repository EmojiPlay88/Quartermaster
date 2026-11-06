// Ported from VASSAL SetupStack.java (position, board offset, grid location) and PieceCloner/BasicPiece.java (piece state layout) (LGPL-2.1).
using System.Globalization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Builds a fresh <see cref="GameState"/> from a module's SetupStacks.</summary>
public static class NewGameBuilder
{
    private const string AnyBoard = "<any>";

    public static GameState FromSetupStacks(ModuleModel model, DiagnosticLog log)
    {
        var state = new GameState();
        var seq = 0;
        foreach (var map in model.Maps)
        {
            state.MapOrder.Add(map.Id);
            foreach (var stack in map.SetupStacks)
            {
                var pos = StackPosition(map, stack, log);
                string? stackId = null;
                foreach (var slot in stack.Pieces)
                {
                    var id = "n:" + (++seq).ToString(CultureInfo.InvariantCulture);
                    var (type, st) = BuildPiece(model, slot, map.Id, pos);
                    state.Apply(new AddPiece(id, type, st, map.Id, pos, stackId));
                    stackId ??= state.Pieces[id].StackId;
                }
            }
        }
        return state;
    }

    private static PointD StackPosition(MapDef map, SetupStackDef stack, DiagnosticLog log)
    {
        BoardDef? owner = null;
        if (stack.OwningBoard.Length > 0 && stack.OwningBoard != AnyBoard)
        {
            owner = map.Boards.FirstOrDefault(b => b.Name == stack.OwningBoard);
            if (owner == null)
                log.AddOnce($"newgame:board:{map.Id}:{stack.OwningBoard}", DiagnosticSeverity.Warning, "newgame",
                    $"Setup stack '{stack.Name}' names unknown board '{stack.OwningBoard}'; the map edge is used as origin.", stack.Name);
        }

        var p = new PointD(stack.X, stack.Y);
        var grid = (owner ?? map.Boards.FirstOrDefault())?.Grid;
        if (stack.UseGridLocation && stack.Location.Length > 0 && grid != null)
        {
            if (grid.LocationToPoint(stack.Location) is { } g) p = g;
            else
                log.AddOnce($"newgame:loc:{map.Id}:{stack.Name}", DiagnosticSeverity.Warning, "newgame",
                    $"Setup stack '{stack.Name}' has unresolvable grid location '{stack.Location}'; its x/y position is used.", stack.Name);
        }

        // Owning board: offset by the board origin; otherwise by the map's edge buffer (SetupStack.setup).
        var off = owner?.Offset ?? new PointD(map.EdgeWidth, map.EdgeHeight);
        return new PointD(p.X + off.X, p.Y + off.Y);
    }

    /// <summary>Expanded type plus the matching state chain whose BasicPiece layer carries map and position.</summary>
    private static (string Type, string State) BuildPiece(ModuleModel model, PieceSlotDef slot, string mapId, PointD pos)
    {
        var type = model.Expander.ExpandTypeAndState(slot.TypeString, slot.StateString, out var expandedState);
        var count = PieceTypeParser.Parse(type, new DiagnosticLog(), null).Traits.Count;
        var states = PieceTypeParser.SplitState(expandedState, count).ToList();

        // BasicPiece state: mapId;x;y;gpid;propCount;key;value...
        var old = new List<string>();
        var d = new SequenceDecoder(states[^1], ';');
        while (d.HasMoreTokens) old.Add(d.NextToken());
        var tokens = new List<string>
        {
            mapId,
            Math.Round(pos.X, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
            Math.Round(pos.Y, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
            slot.Gpid.Length > 0 ? slot.Gpid : old.Count > 3 ? old[3] : "",
        };
        if (old.Count > 4) tokens.AddRange(old.Skip(4)); else tokens.Add("0");
        states[^1] = SequenceEncoder.Encode(tokens, ';');
        return (type, PieceTypeParser.EncodeState(states));
    }
}
