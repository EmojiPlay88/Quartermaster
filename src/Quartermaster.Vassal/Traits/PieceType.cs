// Ported from VASSAL BasicPiece.java, UsePrototype.java, Embellishment.java, Labeler.java, Marker.java, SendToLocation.java,
// Immobilized.java, PropertySheet.java, PlaceMarker.java, Replace.java, Clone.java, Delete.java, Hideable.java, Obscurable.java,
// DynamicProperty.java, FreeRotator.java, Footprint.java, MovementMarkable.java and NonRectangular.java (mySetType readers) (LGPL-2.1).
using System.Globalization;

namespace Quartermaster.Vassal;

/// <summary>A piece's decorator chain, ordered outermost first; the last entry is always a <see cref="BasicPieceDef"/>.</summary>
public sealed record PieceType(IReadOnlyList<TraitDef> Traits)
{
    private static readonly BasicPieceDef Fallback = new("piece");

    public BasicPieceDef Basic => Traits.Count > 0 && Traits[^1] is BasicPieceDef b ? b : Fallback;

    public T? Get<T>() where T : TraitDef => Traits.OfType<T>().FirstOrDefault();
}

public static class PieceTypeParser
{
    private const char Tab = '\t';

    public static PieceType Parse(string type, DiagnosticLog log, string? context)
    {
        var traits = new List<TraitDef>();
        var cur = type ?? "";
        var sawBasic = false;
        for (var depth = 0; depth < 1000; depth++)
        {
            var d = new SequenceDecoder(cur, Tab);
            var layer = d.NextToken();
            var id = IdOf(layer);
            if (id == "piece")
            {
                traits.Add(Create(id, layer, log, context));
                sawBasic = true;
                break;
            }
            traits.Add(Create(id, layer, log, context));
            if (!d.HasMoreTokens) break;
            cur = d.NextToken();
        }
        if (!sawBasic)
        {
            log.Add(DiagnosticSeverity.Warning, "trait", "Piece type has no BasicPiece layer; synthesized an empty one.", context);
            traits.Add(new BasicPieceDef("piece"));
        }
        return new PieceType(traits);
    }

    /// <summary>Splits a state chain the same way as a type chain, padding with "" up to <paramref name="traitCount"/> entries.</summary>
    public static IReadOnlyList<string> SplitState(string state, int traitCount)
    {
        var result = new List<string>();
        var cur = state ?? "";
        var more = true;
        for (var i = 0; i < traitCount; i++)
        {
            if (!more) { result.Add(""); continue; }
            if (i == traitCount - 1) { result.Add(cur); break; }
            var d = new SequenceDecoder(cur, Tab);
            result.Add(d.NextToken());
            if (d.HasMoreTokens) cur = d.NextToken(); else more = false;
        }
        return result;
    }

    private static string IdOf(string layer)
    {
        var i = layer.IndexOf(';');
        return i < 0 ? layer : layer.Substring(0, i);
    }

    /// <summary>The fields after "id;", or null when the layer has none (so every field takes its default).</summary>
    private static SequenceDecoder Fields(string layer, string id, char delim = ';')
        => new(layer.Length > id.Length ? layer.Substring(id.Length + 1) : null, delim);

    private static TraitDef Create(string id, string raw, DiagnosticLog log, string? context)
    {
        try
        {
            switch (id)
            {
                case "piece": return ReadBasic(raw);
                case "prototype": return new PrototypeRef(raw) { PrototypeName = Fields(raw, id).NextToken("") };
                case "emb2": return ReadLayer(raw);
                case "label": return ReadLabel(raw);
                case "mark": return new MarkerDef(raw) { Keys = KeyList(Fields(raw, id, ',')) };
                case "sendto": return ReadSendTo(raw);
                case "immob": return ReadImmob(raw);
                case "propertysheet": return ReadPropertySheet(raw);
                case "placemark":
                {
                    var (name, spec, slot, ox, oy, above) = ReadPlace(Fields(raw, id));
                    return new PlaceMarkerDef(raw) { CommandName = name, MarkerSpec = spec, MarkerSlotPath = slot, OffsetX = ox, OffsetY = oy, AboveTarget = above };
                }
                case "replace":
                {
                    var (name, spec, slot, _, _, _) = ReadPlace(Fields(raw, id));
                    return new ReplaceDef(raw) { CommandName = name, MarkerSpec = spec, MarkerSlotPath = slot };
                }
                case "clone": return new CloneDef(raw) { CommandName = Fields(raw, id).NextToken("") };
                case "delete": return new DeleteDef(raw) { CommandName = Fields(raw, id).NextToken("") };
                case "hide": return ReadHide(raw);
                case "obs": return ReadObs(raw);
                case "PROP": return ReadProp(raw);
                case "rotate": return ReadRotate(raw);
                case "footprint": return ReadFootprint(raw);
                case "markmoved":
                {
                    var st = Fields(raw, id);
                    return new MovementMarkableDef(raw) { Image = st.NextToken(""), XOffset = st.NextInt(0), YOffset = st.NextInt(0) };
                }
                case "nonRect2":
                {
                    var st = Fields(raw, id);
                    var scale = st.NextDouble(1.0);
                    return new NonRectangularDef(raw) { Scale = scale, ShapeSpec = st.GetRemaining() };
                }
                case "return": return new InertTrait(raw);
            }
        }
        catch (Exception ex)
        {
            log.Add(DiagnosticSeverity.Warning, "trait", $"Could not read trait '{id}': {ex.Message}", context);
            return new UnknownTrait(id, raw);
        }

        log.AddOnce("trait:unknown:" + id, DiagnosticSeverity.Warning, "trait", $"Unsupported trait '{id}' ignored.", context);
        return new UnknownTrait(id, raw);
    }

    private static string[] KeyList(SequenceDecoder st)
    {
        var l = new List<string>();
        while (st.HasMoreTokens) l.Add(st.NextToken());
        return l.ToArray();
    }

    private static BasicPieceDef ReadBasic(string raw)
    {
        var st = Fields(raw, "piece");
        return new BasicPieceDef(raw)
        {
            CloneKey = st.NextToken(""),
            DeleteKey = st.NextToken(""),
            Image = st.NextToken(""),
            Name = st.NextToken(""),
        };
    }

    private static LayerDef ReadLayer(string raw)
    {
        var st = Fields(raw, "emb2");
        var activateCommand = st.NextToken("");
        st.NextInt(0); // activateModifiers
        var activateKey = st.NextToken("A");
        var upCommand = st.NextToken("");
        st.NextInt(0);
        st.NextToken("");
        var downCommand = st.NextToken("");
        st.NextInt(0);
        st.NextToken("");
        var resetCommand = st.NextToken("");
        st.NextToken(""); // resetKey
        var resetLevel = int.TryParse(st.NextToken("1"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rl) ? rl : 1;
        var drawUnder = st.NextBoolean(false);
        var xOff = st.NextInt(0);
        var yOff = st.NextInt(0);
        var images = st.NextStringArray(1);
        var names = st.NextStringArray(images.Length);
        var loop = st.NextBoolean(true);
        var name = st.NextToken("");
        st.NextToken(""); // rndKey
        st.NextToken(""); // rndText
        var follow = st.NextBoolean(false);
        var propName = st.NextToken("");
        var firstLevel = st.NextInt(1);
        var version = st.NextInt(0);
        var alwaysActive = st.NextBoolean(true);
        if (version == 0) alwaysActive = activateKey.Length == 0; // legacy (BASE_VERSION) conversion
        return new LayerDef(raw)
        {
            Images = images,
            LevelNames = names,
            OffsetX = xOff,
            OffsetY = yOff,
            Loop = loop,
            AlwaysActive = alwaysActive,
            DrawUnderneath = drawUnder,
            ActivateCommand = activateCommand,
            UpCommand = upCommand,
            DownCommand = downCommand,
            ResetCommand = resetCommand,
            ResetLevel = resetLevel,
            FollowProperty = follow,
            PropertyName = propName,
            FirstLevelValue = firstLevel,
            Name = name,
        };
    }

    private static char Ch(SequenceDecoder st, char def)
    {
        var s = st.NextToken("");
        return s.Length > 0 ? s[0] : def;
    }

    private static LabelDef ReadLabel(string raw)
    {
        var st = Fields(raw, "label");
        st.NextToken(""); // labelKey
        var menu = st.NextToken("Change Label");
        var size = st.NextInt(10);
        var bg = RgbColor.Parse(st.NextToken(""));
        var fgTok = st.HasMoreTokens ? st.NextToken("") : null;
        var fg = fgTok == null ? new RgbColor(0, 0, 0) : RgbColor.Parse(fgTok);
        var vpos = Ch(st, 't');
        var voff = st.NextInt(0);
        var hpos = Ch(st, 'c');
        var hoff = st.NextInt(0);
        var vjust = Ch(st, 'b');
        var hjust = Ch(st, 'c');
        var format = st.NextToken("$pieceName$ ($label$)").Replace("$PieceName$", "$pieceName$");
        var family = st.NextToken("Dialog");
        var style = st.NextInt(0);
        st.NextInt(0); // rotateDegrees
        var propName = st.NextToken("TextLabel");
        return new LabelDef(raw)
        {
            MenuCommand = menu,
            FontSize = size,
            FontStyle = style,
            FontFamily = family,
            Foreground = fg,
            Background = bg,
            VerticalPos = vpos,
            HorizontalPos = hpos,
            VerticalOffset = voff,
            HorizontalOffset = hoff,
            VerticalJustification = vjust,
            HorizontalJustification = hjust,
            LabelFormat = format,
            Name = propName,
        };
    }

    private static SendToDef ReadSendTo(string raw)
    {
        var st = Fields(raw, "sendto");
        var cmd = st.NextToken("");
        st.NextToken(""); // key
        var map = st.NextToken("");
        var board = st.NextToken("");
        var x = st.NextToken("0");
        var y = st.NextToken("0");
        st.NextToken(""); // backCommandName
        st.NextToken(""); // backKey
        st.NextToken("0"); // xIndex
        st.NextToken("0"); // yIndex
        st.NextToken("1"); // xOffset
        st.NextToken("1"); // yOffset
        var desc = st.NextToken("");
        var dest = st.NextToken("L");
        var zone = st.NextToken("");
        var region = st.NextToken("");
        st.NextToken(""); // propertyFilter
        var grid = st.NextToken("");
        return new SendToDef(raw)
        {
            CommandName = cmd,
            MapName = map,
            BoardName = board,
            X = x,
            Y = y,
            Description = desc,
            ZoneName = zone,
            RegionName = region,
            GridLocation = grid,
            DestinationType = dest switch
            {
                "G" => SendToDestination.GridLocation,
                "Z" => SendToDestination.Zone,
                "R" => SendToDestination.Region,
                "A" => SendToDestination.Counter,
                "C" => SendToDestination.CounterCycle,
                "N" => SendToDestination.CounterNearest,
                _ => SendToDestination.Location,
            },
        };
    }

    private static ImmobileDef ReadImmob(string raw)
    {
        var st = Fields(raw, "immob");
        var selection = st.NextToken("");
        var movement = st.NextToken("");
        var neverSelect = selection.Contains('n');
        return new ImmobileDef(raw)
        {
            ShiftToSelect = selection.Contains('i'),
            AltToSelect = selection.Contains('c'),
            SelectionLocked = neverSelect,
            MoveLocked = neverSelect || (movement.Length > 0 && movement[0] == 'V'),
        };
    }

    private static PropertySheetDef ReadPropertySheet(string raw)
    {
        var st = Fields(raw, "propertysheet");
        var def = st.NextToken("");
        var menu = st.NextToken("");
        var rows = new List<(string, int)>();
        var dd = new SequenceDecoder(def, '~');
        while (dd.HasMoreTokens)
        {
            var item = dd.NextToken();
            if (item.Length == 0) continue;
            rows.Add((item.Substring(1), item[0] - '0'));
        }
        return new PropertySheetDef(raw) { Rows = rows, MenuName = menu };
    }

    private static (string Name, string Spec, string Slot, int Ox, int Oy, bool Above) ReadPlace(SequenceDecoder st)
    {
        var name = st.NextToken("");
        st.NextToken(""); // key
        var spec = st.NextToken("");
        if (spec == "null") spec = "";
        st.NextToken("null"); // markerText
        var ox = int.TryParse(st.NextToken("0"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x) ? x : 0;
        var oy = int.TryParse(st.NextToken("0"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y) ? y : 0;
        st.NextBoolean(false); // matchRotation
        st.NextToken(""); // afterBurnerKey
        st.NextToken(""); // description
        st.NextToken(""); // gpId
        st.NextInt(0); // placement
        var above = st.NextBoolean(false);
        return (name, spec, spec.StartsWith('+') ? "" : spec, ox, oy, above);
    }

    private static HideableDef ReadHide(string raw)
    {
        var st = Fields(raw, "hide");
        st.NextToken(""); // hideKey
        var cmd = st.NextToken("Invisible");
        st.NextToken(""); // bgColor
        st.NextToken(""); // access
        var t = st.NextDouble(0.3);
        return new HideableDef(raw) { CommandName = cmd, Transparency = Math.Clamp(t, 0.0, 1.0) };
    }

    private static ObscurableDef ReadObs(string raw)
    {
        var st = Fields(raw, "obs");
        st.NextToken(""); // keyCommand
        st.NextToken(""); // imageName
        var hide = st.NextToken("Mask");
        var style = 'I';
        var arg = "";
        if (st.HasMoreTokens)
        {
            var s = st.NextToken("I");
            if (s.Length > 0) { style = s[0]; arg = s.Substring(1); }
        }
        var mask = st.NextToken("?");
        return new ObscurableDef(raw) { CommandName = hide, MaskImage = mask, DisplayStyle = style, DisplayArg = arg };
    }

    private static DynamicPropertyDef ReadProp(string raw)
    {
        var st = Fields(raw, "PROP");
        var key = st.NextToken("name");
        var c = new SequenceDecoder(st.NextToken(""), ',');
        var numeric = c.NextBoolean(false);
        var min = c.NextInt(0);
        var max = c.NextInt(100);
        var wrap = c.NextBoolean(false);

        var cmds = new List<(string, string, string)>();
        var list = new SequenceDecoder(st.NextToken(""), ',');
        while (list.HasMoreTokens)
        {
            var one = new SequenceDecoder(list.NextToken(), ':');
            var menu = one.NextToken("");
            one.NextToken(""); // key
            var change = new SequenceDecoder(one.NextToken(""), ',');
            var kind = change.NextToken("P");
            var expr = change.NextToken("");
            cmds.Add((menu, kind, expr));
        }
        return new DynamicPropertyDef(raw) { Key = key, Numeric = numeric, Min = min, Max = max, Wrap = wrap, Commands = cmds };
    }

    private static RotatorDef ReadRotate(string raw)
    {
        var st = Fields(raw, "rotate");
        var angles = int.TryParse(st.NextToken("1"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
        string cw = "", ccw = "";
        if (angles == 1)
        {
            st.NextToken(""); // setAngleKey
            if (st.HasMoreTokens) st.NextToken(""); // setAngleText
        }
        else
        {
            st.NextToken(""); // rotateCWKey
            st.NextToken(""); // rotateCCWKey
            cw = st.NextToken("");
            ccw = st.NextToken("");
        }
        st.NextToken(""); // rotateRNDKey
        st.NextToken(""); // rotateRNDText
        var name = st.NextToken("");
        return new RotatorDef(raw) { ValidAngles = angles, RotateCwCommand = cw, RotateCcwCommand = ccw, Name = name };
    }

    private static FootprintDef ReadFootprint(string raw)
    {
        var st = Fields(raw, "footprint");
        st.NextToken(""); // trailKey
        var cmd = st.NextToken("Movement Trail");
        var visible = st.NextBoolean(false);
        return new FootprintDef(raw) { CommandName = cmd, InitiallyVisible = visible };
    }
}
