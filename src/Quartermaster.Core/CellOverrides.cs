using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// The one table-read choke point for a piece being evaluated: a property overriding the table's column (<see cref="CellOverrideFacts"/>)
/// replaces the printed cell when the located row is the piece's own name and the piece has a value for it. Otherwise the cell.
/// </summary>
internal static class CellOverrides
{
    /// <summary>The cells (piece id, table id, column) whose computed override is being evaluated on this thread: a read of one of them
    /// inside that evaluation is the printed cell, not a cycle. Other cells' overrides still apply.</summary>
    [ThreadStatic] private static HashSet<(string Piece, string Table, int Col)>? _resolving;

    /// <summary>
    /// The value read at (<paramref name="row"/>, <paramref name="col"/>): the first overriding property the piece has a value for
    /// (<see cref="PropertyValues.Get(GameState, RulesPack, ModuleModel?, string, PropertyDef)"/> non-null: stored, or computed and applying;
    /// never its Default), else the printed cell. <c>By</c> is the property used (null for the cell); <c>Printed</c> is always the cell.
    /// In a player view a value that may be hidden (a non-Public property of a piece of a side the viewer does not hold, or a masked
    /// placeholder) is an <see cref="EvalError.HiddenData"/> error rather than the cell; a computed value's errors propagate.
    /// </summary>
    internal static (Value Value, PropertyDef? By, Value Printed) Read(TableDef t, int row, int col, PieceInstance? piece, GameState s,
        RulesPack pack, ModuleModel? model, AggregateFilters? filters = null, Lazy<Func<PieceInstance, string?>>? sides = null)
    {
        var printed = TableLookup.CellAt(t, row, col);
        if (piece == null || row < 0 || row >= t.RowKeys.Count) return (printed, null, printed);
        var props = CellOverrideFacts.For(pack, t.Id, t.Kind == TableKind.TwoD && col >= 0 && col < t.ColumnKeys.Count ? t.ColumnKeys[col] : null);
        if (props.Count == 0 || !SameRow(t.RowKeys[row], PieceInfo.Name(piece))) return (printed, null, printed);
        var cell = (piece.Id, t.Id, col);
        if (_resolving?.Contains(cell) == true) return (printed, null, printed);   // an override's own expression reads its printed cell
        var key = LedgerKey.Piece(piece.Id);
        foreach (var p in props)
        {
            double? v;
            if (PropertyFacts.IsComputed(p))
            {
                _resolving ??= new();
                _resolving.Add(cell);
                try { v = PropertyValues.Get(s, pack, model, key, p, filters ?? new AggregateFilters(s, pack, model)); }
                finally { _resolving.Remove(cell); }
            }
            else v = PropertyValues.Get(s, pack, model, key, p, null);
            if (v != null) return (Value.Of(v.Value), p, printed);
            if (HiddenInView(s, pack, piece, p, sides))
                throw new EvalError($"{p.Name} of {PieceInfo.Name(piece)} {PieceAggregates.HiddenText}") { HiddenData = true };
        }
        return (printed, null, printed);
    }

    /// <summary>The located row key is the piece's name, compared as <see cref="TableLookup.Locate"/> compares row keys.</summary>
    private static bool SameRow(string rowKey, string name)
    {
        if (string.Equals(TableLookup.Normalise(rowKey), TableLookup.Normalise(name), StringComparison.OrdinalIgnoreCase)) return true;
        var k = TableLookup.ParseKey(rowKey);
        return k.Kind == TableKeyKind.Number && TableLookup.Matches(k, Value.Of(name));
    }

    /// <summary>As a Picked-piece read decides it: a placeholder, or a non-Public property of a piece of a side the viewer does not hold.</summary>
    private static bool HiddenInView(GameState s, RulesPack pack, PieceInstance piece, PropertyDef p, Lazy<Func<PieceInstance, string?>>? sides)
    {
        if (!s.IsPlayerView) return false;
        if (PlayerView.IsPlaceholder(piece)) return true;
        if (PropertyFacts.IsPublic(pack, p)) return false;
        if (Ownership.InUnitFilter) return true;   // a side's own unit filter: the side map is not known yet
        var side = (sides ?? PieceFilter.SideMap(s, pack)).Value(piece);
        return side != null && s.ViewSides?.Contains(side) != true;
    }
}
