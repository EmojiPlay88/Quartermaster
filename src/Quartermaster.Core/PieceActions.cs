// Ported from VASSAL SendToLocation.java, Clone.java, Delete.java, Embellishment.java, FreeRotator.java, Hideable.java, Obscurable.java,
// DynamicProperty.java (with IncrementProperty/PropertyChangerConfigurer), PlaceMarker.java, Replace.java, Footprint.java, PropertySheet.java
// and Immobilized.java (key-command menus and the state changes they make) (LGPL-2.1).
using System.Collections.Concurrent;
using System.Globalization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// One right-click menu entry of a piece. <see cref="Build"/> turns it into commands; prompt actions (Group "prompt") use
/// <see cref="BuildWithInput"/> instead, and Group "sheet" actions are handled by the UI through ReadSheet/WriteSheet.
/// </summary>
public sealed record PieceAction(string Label, string Group, Func<GameState, IReadOnlyList<GameCommand>> Build)
{
    public Func<GameState, string, IReadOnlyList<GameCommand>>? BuildWithInput { get; init; }
    public bool Enabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    /// <summary>Prompt text for Group "prompt" actions.</summary>
    public string? PromptText { get; init; }
}

public static class PieceActions
{
    private const string LocalPlayer = "local";
    private const char SheetDelimiter = '~';
    private const char SheetLineDelimiter = '|';
    private static readonly IReadOnlyList<GameCommand> None = Array.Empty<GameCommand>();
    private static readonly ConcurrentDictionary<string, PieceType> TypeCache = new();

    /// <summary>Parses a type string (cached). Never throws.</summary>
    private static PieceType TypeOf(string typeString)
    {
        if (TypeCache.TryGetValue(typeString, out var t)) return t;
        if (TypeCache.Count > 4096) TypeCache.Clear();
        t = PieceTypeParser.Parse(typeString, new DiagnosticLog(), null);
        TypeCache[typeString] = t;
        return t;
    }

    private static string StateAt(GameState s, string pieceId, int i)
        => s.Pieces.TryGetValue(pieceId, out var p) && i >= 0 && i < p.TraitStates.Count ? p.TraitStates[i] : "";

    /// <summary>False when an Immobilized trait locks the piece against dragging.</summary>
    public static bool CanMove(PieceInstance p) => !TypeOf(p.TypeString).Traits.OfType<ImmobileDef>().Any(i => i.MoveLocked);

    /// <summary>Map-level actions (Map.java "Mark all pieces unmoved").</summary>
    public static IReadOnlyList<PieceAction> ForMap(MapDef m)
        => new[] { new PieceAction("Mark all unmoved", "map", _ => new GameCommand[] { new ClearMoved(m.Id) }) };

    public static IReadOnlyList<PieceAction> For(PieceInstance p, ModuleModel model, GameState s, Func<string> newId)
    {
        var result = new List<PieceAction>();
        try
        {
            var traits = TypeOf(p.TypeString).Traits;
            for (var i = 0; i < traits.Count; i++)
            {
                try { AddFor(result, traits[i], i, p, model, s, newId); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    result.Add(new PieceAction(traits[i].Id, "error", _ => None) { Enabled = false, DisabledReason = "Trait could not be read: " + ex.Message });
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // never throw on bad data; the piece just has no actions
            _ = ex;
        }
        return result;
    }

    private static void AddFor(List<PieceAction> list, TraitDef trait, int i, PieceInstance p, ModuleModel model, GameState cur, Func<string> newId)
    {
        var id = p.Id;
        GameCommand[] Set(GameState s, Func<string, string> change) =>
            s.Pieces.ContainsKey(id) ? new GameCommand[] { new SetTraitState(id, i, change(StateAt(s, id, i))) } : Array.Empty<GameCommand>();

        switch (trait)
        {
            case SendToDef d when d.CommandName.Length > 0:
                list.Add(SendTo(d, p, model));
                break;

            case CloneDef d when d.CommandName.Length > 0:
                list.Add(new PieceAction(d.CommandName, "piece", s =>
                {
                    if (!s.Pieces.TryGetValue(id, out var src)) return None;
                    var count = TypeOf(src.TypeString).Traits.Count;
                    var states = Enumerable.Range(0, count).Select(k => k < src.TraitStates.Count ? src.TraitStates[k] : "").ToList();
                    var state = states.All(x => x.Length == 0) ? "" : PieceTypeParser.EncodeState(states);
                    return new GameCommand[] { new AddPiece(newId(), src.TypeString, state, src.MapId, src.Pos, src.StackId) };
                }));
                break;

            case DeleteDef d when d.CommandName.Length > 0:
                list.Add(new PieceAction(d.CommandName, "piece", s => s.Pieces.ContainsKey(id) ? new GameCommand[] { new RemovePiece(id) } : None));
                break;

            case LayerDef d:
                AddLayer(list, d, StateAt(cur, id, i), Set);
                break;

            case RotatorDef d when d.ValidAngles > 1:
                if (d.RotateCwCommand.Length > 0)
                    list.Add(new PieceAction(d.RotateCwCommand, "state", s => Set(s, st => TraitState.RotationStep(d, st, +1))));
                if (d.RotateCcwCommand.Length > 0)
                    list.Add(new PieceAction(d.RotateCcwCommand, "state", s => Set(s, st => TraitState.RotationStep(d, st, -1))));
                break;

            case HideableDef d when d.CommandName.Length > 0:
                list.Add(new PieceAction(d.CommandName, "state", s => Set(s, st => TraitState.HiddenToggle(d, st, LocalPlayer))));
                break;

            case ObscurableDef d when d.CommandName.Length > 0:
                list.Add(new PieceAction(d.CommandName, "state", s => Set(s, st => TraitState.ObscuredToggle(d, st, LocalPlayer))));
                break;

            case DynamicPropertyDef d:
                AddProperty(list, d, id, Set);
                break;

            case PlaceMarkerDef d when d.CommandName.Length > 0:
                list.Add(Place(d.CommandName, d.MarkerSpec, d.MarkerSlotPath, new PointD(d.OffsetX, d.OffsetY), false, p, model, newId));
                break;

            case ReplaceDef d when d.CommandName.Length > 0:
                list.Add(Place(d.CommandName, d.MarkerSpec, d.MarkerSlotPath, new PointD(0, 0), true, p, model, newId));
                break;

            case FootprintDef d when d.CommandName.Length > 0:
                list.Add(new PieceAction(d.CommandName, "trail", s => s.Pieces.ContainsKey(id) ? new GameCommand[] { new ToggleTrail(id) } : None));
                break;

            case PropertySheetDef d when d.MenuName.Length > 0:
                list.Add(new PieceAction(d.MenuName, "sheet", _ => None));
                break;
        }
    }

    // ---- Embellishment ----

    private static void AddLayer(List<PieceAction> list, LayerDef d, string current, Func<GameState, Func<string, string>, GameCommand[]> set)
    {
        var count = Math.Max(Math.Max(d.Images.Count, d.LevelNames.Count), 1);
        if (!d.AlwaysActive && d.ActivateCommand.Length > 0)
            list.Add(new PieceAction(d.ActivateCommand, "state", s => set(s, st => TraitState.LayerToggleActive(d, st))));
        if (d.FollowProperty) return;

        // The Up/Down entries stay visible but are disabled at the ends of a non-looping layer (Embellishment.myGetKeyCommands).
        if (count > 1 && d.UpCommand.Length > 0)
            list.Add(StepAction(d.UpCommand, +1));
        if (count > 1 && d.DownCommand.Length > 0)
            list.Add(StepAction(d.DownCommand, -1));
        if (d.ResetCommand.Length > 0)
            list.Add(new PieceAction(d.ResetCommand, "state", s => set(s, st => TraitState.LayerReset(d, st))));

        PieceAction StepAction(string label, int delta)
        {
            var level = Math.Abs(TraitState.LayerLevel(d, current).Level);
            var atEnd = !d.Loop && (delta > 0 ? level >= count : level <= 1);
            return new PieceAction(label, "state", s => set(s, st => TraitState.LayerStep(d, st, delta)))
            {
                Enabled = !atEnd,
                DisabledReason = atEnd ? (delta > 0 ? "Already at the top level." : "Already at the bottom level.") : null,
            };
        }
    }

    // ---- DynamicProperty ----

    private static void AddProperty(List<PieceAction> list, DynamicPropertyDef d, string id, Func<GameState, Func<string, string>, GameCommand[]> set)
    {
        foreach (var (menu, kind, expr) in d.Commands)
        {
            if (menu.Length == 0) continue;
            switch (kind)
            {
                case "I":
                    if (int.TryParse(expr.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var incr))
                        list.Add(new PieceAction(menu, "state", s => set(s, st => TraitState.DynamicIncrement(d, st, incr))));
                    else
                        list.Add(Disabled(menu, "state", $"Increment '{expr}' is not a plain number."));
                    break;
                case "P":
                    if (expr.Contains('$'))
                        list.Add(Disabled(menu, "state", $"New value '{expr}' refers to properties, which are not supported."));
                    else
                        list.Add(new PieceAction(menu, "state", s => set(s, _ => TraitState.DynamicSet(d, expr))));
                    break;
                default: // "R" prompt, "E" enumerated prompt (valid values are not kept by the reader, so free text is accepted)
                    list.Add(new PieceAction(menu, "prompt", _ => None)
                    {
                        PromptText = expr,
                        BuildWithInput = (s, value) =>
                        {
                            value ??= "";
                            if (d.Numeric && (!int.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < d.Min || n > d.Max))
                                return None;
                            return set(s, _ => TraitState.DynamicSet(d, d.Numeric ? value.Trim() : value));
                        },
                    });
                    break;
            }
        }
    }

    private static PieceAction Disabled(string label, string group, string reason)
        => new(label, group, _ => None) { Enabled = false, DisabledReason = reason };

    // ---- SendToLocation ----

    private static PieceAction SendTo(SendToDef d, PieceInstance p, ModuleModel model)
    {
        var dest = ResolveSendTo(d, p, model, out var mapId, out var reason);
        if (dest is not { } pt) return Disabled(d.CommandName, "move", reason);
        var id = p.Id;
        return new PieceAction(d.CommandName, "move", s => s.Pieces.ContainsKey(id)
            ? new GameCommand[] { new MovePieces(new[] { id }, mapId, pt, null) }
            : None);
    }

    /// <summary>SendToLocation.getSendLocation: the destination in map coordinates, or null with a diagnostic.</summary>
    private static PointD? ResolveSendTo(SendToDef d, PieceInstance p, ModuleModel model, out string mapId, out string reason)
    {
        reason = "";
        var map = (d.MapName.Length > 0 ? model.Maps.FirstOrDefault(m => m.Name == d.MapName || m.Id == d.MapName) : null)
                  ?? model.Maps.FirstOrDefault(m => m.Id == p.MapId);
        mapId = map?.Id ?? p.MapId;
        if (map == null) { reason = $"Map '{d.MapName}' not found."; return null; }

        var board = d.BoardName.Length > 0 ? map.Boards.FirstOrDefault(b => b.Name == d.BoardName) : null;
        switch (d.DestinationType)
        {
            case SendToDestination.Location:
            {
                if (!TryInt(d.X, out var x) || !TryInt(d.Y, out var y))
                {
                    reason = $"Position '{d.X}, {d.Y}' is not a plain number (property references are not supported).";
                    return null;
                }
                var off = board?.Offset ?? new PointD(0, 0);
                return new PointD(x + off.X, y + off.Y);
            }
            case SendToDestination.GridLocation:
            {
                if (board == null) { reason = $"Board '{d.BoardName}' not found on map '{map.Name}'."; return null; }
                if (board.Grid == null) { reason = $"Board '{board.Name}' has no grid."; return null; }
                var g = board.Grid.LocationToPoint(d.GridLocation);
                if (g == null) { reason = $"Grid location '{d.GridLocation}' not found."; return null; }
                return new PointD(g.Value.X + board.Offset.X, g.Value.Y + board.Offset.Y);
            }
            case SendToDestination.Zone:
            {
                foreach (var b in map.Boards)
                {
                    if (b.Grid is ZonedGrid zg && zg.ZoneCenter(d.ZoneName) is { } c)
                        return new PointD(c.X + b.Offset.X, c.Y + b.Offset.Y);
                }
                reason = $"Zone '{d.ZoneName}' not found on map '{map.Name}'.";
                return null;
            }
            default:
                reason = $"Destination type {d.DestinationType} is not supported.";
                return null;
        }
    }

    private static bool TryInt(string s, out int v)
        => int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    // ---- PlaceMarker / Replace ----

    private static PieceAction Place(string label, string spec, string slotPath, PointD offset, bool replace, PieceInstance p, ModuleModel model, Func<string> newId)
    {
        PieceSlotDef? slot = null;
        if (spec.Length == 0) return Disabled(label, "piece", "No piece is configured.");
        if (!spec.StartsWith('+'))
        {
            slot = model.SlotByPath(slotPath) ?? model.SlotByGpid(slotPath);
            if (slot == null) return Disabled(label, "piece", $"Piece slot '{slotPath}' not found.");
        }
        else if (PieceFactory.FromInlineSpec(model, spec, p.MapId, p.Pos) == null)
        {
            return Disabled(label, "piece", "The marker piece definition is malformed.");
        }

        var id = p.Id;
        return new PieceAction(label, "piece", s =>
        {
            if (!s.Pieces.TryGetValue(id, out var src)) return None;
            var pos = new PointD(src.Pos.X + offset.X, src.Pos.Y + offset.Y);
            var built = slot != null
                ? PieceFactory.FromSlot(model, slot, src.MapId, pos)
                : PieceFactory.FromInlineSpec(model, spec, src.MapId, pos);
            if (built is not { } b) return None;
            var onto = replace || (offset.X == 0 && offset.Y == 0) ? src.StackId : null;
            var add = new AddPiece(newId(), b.Type, b.State, src.MapId, pos, onto);
            return replace ? new GameCommand[] { add, new RemovePiece(id) } : new GameCommand[] { add };
        });
    }

    // ---- PropertySheet ----

    /// <summary>The sheet's rows with their current values (PropertySheet.updateFieldsFromState). Empty when the trait index is not a property sheet.</summary>
    public static IReadOnlyList<(string Name, string Value)> ReadSheet(PieceInstance p, int traitIndex)
    {
        var traits = TypeOf(p.TypeString).Traits;
        if (traitIndex < 0 || traitIndex >= traits.Count || traits[traitIndex] is not PropertySheetDef def)
            return Array.Empty<(string, string)>();
        var state = traitIndex < p.TraitStates.Count ? p.TraitStates[traitIndex] : "";
        var d = new SequenceDecoder(state, SheetDelimiter);
        var rows = new List<(string, string)>();
        foreach (var (name, _) in def.Rows)
            rows.Add((name, d.NextToken("").Replace(SheetLineDelimiter, '\n')));
        return rows;
    }

    /// <summary>The single SetTraitState that saves the sheet (PropertySheet.updateStateFromFields): values joined with '~', newlines stored as '|'.</summary>
    public static GameCommand WriteSheet(PieceInstance p, int traitIndex, IReadOnlyList<string> values)
    {
        var tokens = values.Select(v => (v ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', SheetLineDelimiter));
        return new SetTraitState(p.Id, traitIndex, SequenceEncoder.Encode(tokens, SheetDelimiter));
    }
}
