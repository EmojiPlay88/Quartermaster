// Ported from VASSAL PieceCloner.java / BasicPiece.java (prototype expansion on slot instantiation, piece state layout) (LGPL-2.1).
using System.Globalization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Builds piece type and state strings for new pieces made from palette slots or inline marker specs.</summary>
public static class PieceFactory
{
    /// <summary>Expanded type plus the matching state chain whose BasicPiece layer carries map and position.</summary>
    public static (string Type, string State) FromSlot(ModuleModel model, PieceSlotDef slot, string mapId, PointD pos)
        => FromTypeAndState(model, slot.TypeString, slot.StateString, slot.Gpid, mapId, pos);

    /// <summary>
    /// Builds a piece from an inline PlaceMarker/Replace spec of the form "+/id/type/state". Null when the spec is malformed.
    /// </summary>
    public static (string Type, string State)? FromInlineSpec(ModuleModel model, string spec, string mapId, PointD pos)
    {
        var t = SequenceEncoder.Decode(spec?.Trim(), '/');
        if (t.Count < 3 || t[0] != "+") return null;
        return FromTypeAndState(model, t[2], t.Count > 3 ? t[3] : "", "", mapId, pos);
    }

    public static (string Type, string State) FromTypeAndState(ModuleModel model, string typeString, string stateString, string gpid, string mapId, PointD pos)
    {
        var type = model.Expander.ExpandTypeAndState(typeString, stateString, out var expandedState);
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
            gpid.Length > 0 ? gpid : old.Count > 3 ? old[3] : "",
        };
        if (old.Count > 4) tokens.AddRange(old.Skip(4)); else tokens.Add("0");
        states[^1] = SequenceEncoder.Encode(tokens, ';');
        return (type, PieceTypeParser.EncodeState(states));
    }
}
