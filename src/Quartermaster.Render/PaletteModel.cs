using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>One folder of the counter palette: its sub-folders and the slots directly inside it.</summary>
public sealed record PaletteNode(string Title, IReadOnlyList<PaletteNode> Children, IReadOnlyList<PieceSlotDef> Slots);

/// <summary>UI-free logic of the game-master counter palette: folder tree, search and drop target.</summary>
public static class PaletteModel
{
    public const string DragFormat = "Quartermaster.SlotPath";
    public const string OtherFolder = "Other";
    public const int MaxSearchResults = 500;

    private sealed class Folder(string title)
    {
        public string Title { get; } = title;
        public List<Folder> Children { get; } = new();
        public List<PieceSlotDef> Slots { get; } = new();
        public PaletteNode ToNode() => new(Title, Children.Select(c => c.ToNode()).ToList(), Slots);
    }

    /// <summary>Folder tree from each slot's NavPath, in module order. Slots without a path go under "Other" (last).</summary>
    public static PaletteNode Build(ModuleModel m)
    {
        var root = new Folder(m.Name);
        Folder? other = null;
        foreach (var slot in m.Slots)
        {
            var f = root;
            if (slot.NavPath.Count == 0) f = other ??= new Folder(OtherFolder);
            else
                foreach (var seg in slot.NavPath)
                {
                    var next = f.Children.FirstOrDefault(c => c.Title == seg);
                    if (next == null) f.Children.Add(next = new Folder(seg));
                    f = next;
                }
            f.Slots.Add(slot);
        }
        if (other != null) root.Children.Add(other);
        return root.ToNode();
    }

    public static IReadOnlyList<PieceSlotDef> Search(ModuleModel m, string text) => Search(m.Slots, text);

    /// <summary>Case-insensitive substring match on the BasicPiece name and the entry name; at most <see cref="MaxSearchResults"/>.</summary>
    public static IReadOnlyList<PieceSlotDef> Search(IEnumerable<PieceSlotDef> slots, string text)
    {
        var q = text?.Trim() ?? "";
        var hits = new List<PieceSlotDef>();
        if (q.Length == 0) return hits;
        foreach (var s in slots)
        {
            if (s.EntryName.Contains(q, StringComparison.OrdinalIgnoreCase) || PieceName(s).Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(s);
                if (hits.Count >= MaxSearchResults) break;
            }
        }
        return hits;
    }

    public static string PieceName(PieceSlotDef s) => PieceInfo.Name(new PieceInstance { TypeString = s.TypeString });

    /// <summary>Identity of a slot for drag and drop and thumbnail caching.</summary>
    public static string Key(PieceSlotDef s) => s.EntryName + "|" + s.Gpid;

    /// <summary>Where a counter dropped at <paramref name="mapPoint"/> lands, or null when the point is outside every board.</summary>
    public static (string MapId, PointD Pos, string? StackOnto)? ResolveDrop(GameState s, MapDef map, PointD mapPoint, ImageSource images, bool snap)
    {
        if (!OnBoard(map, mapPoint)) return null;
        var t = DropResolver.Resolve(s, map, mapPoint, Array.Empty<string>(), images, snap);
        return (map.Id, t.Pos, t.StackOnto);
    }

    private static bool OnBoard(MapDef map, PointD p) =>
        map.Boards.Any(b => p.X >= b.Offset.X && p.Y >= b.Offset.Y && p.X < b.Offset.X + b.Width && p.Y < b.Offset.Y + b.Height);
}
