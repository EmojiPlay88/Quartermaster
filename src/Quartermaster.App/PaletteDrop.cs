using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>WinForms side of palette drops: reads the OLE drag payload. Placing is shared with Desktop (<see cref="PaletteModel.Place"/>).</summary>
static class PaletteDrop
{
    public static PieceSlotDef? Find(ModuleSession s, string? key) => PaletteModel.Find(s.Model, key);

    /// <summary>The slot carried by a drag payload, or null when the data is not a palette drag of this module.</summary>
    public static PieceSlotDef? FromData(ModuleSession s, IDataObject? data) =>
        data?.GetDataPresent(PaletteModel.DragFormat) == true ? Find(s, data.GetData(PaletteModel.DragFormat) as string) : null;
}
