using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Places a palette counter on a map: resolve the drop, build the piece from its slot, execute a logged AddPiece, seed its ledgers.</summary>
static class PaletteDrop
{
    public static PieceSlotDef? Find(ModuleSession s, string? key) =>
        key == null ? null : s.Model.Slots.FirstOrDefault(x => PaletteModel.Key(x) == key);

    /// <summary>The slot carried by a drag payload, or null when the data is not a palette drag of this module.</summary>
    public static PieceSlotDef? FromData(ModuleSession s, IDataObject? data) =>
        data?.GetDataPresent(PaletteModel.DragFormat) == true ? Find(s, data.GetData(PaletteModel.DragFormat) as string) : null;

    public static (bool Placed, string Message) Place(ModuleSession s, MapDef map, PieceSlotDef slot, PointD mapPoint, bool snap)
    {
        if (s.Game is not { } game) return (false, "No game is running");
        if (PaletteModel.ResolveDrop(game.State, map, mapPoint, s.Images, snap) is not { } t) return (false, "Drop onto a board");
        var (type, state) = PieceFactory.FromSlot(s.Model, slot, t.MapId, t.Pos);
        var id = "p:" + Guid.NewGuid().ToString("N");
        game.Execute(CommandLog.Prepare(new AddPiece(id, type, state, t.MapId, t.Pos, t.StackOnto), game.State, s.Model));
        s.SeedLedgers();
        return (true, $"Placed {PaletteModel.PieceName(slot)}");
    }
}
