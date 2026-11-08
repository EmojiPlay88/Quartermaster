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

    /// <summary>The module's slot with this <see cref="Key"/>, or null.</summary>
    public static PieceSlotDef? Find(ModuleModel m, string? key) =>
        key == null ? null : m.Slots.FirstOrDefault(x => Key(x) == key);

    /// <summary>
    /// Places a palette counter on a map (the drop path of every UI): resolve the drop, build the piece from its slot, execute a logged
    /// AddPiece and seed its ledgers. Returns whether it was placed and the status-bar message.
    /// </summary>
    /// <param name="actions">The main window's action port (it executes the AddPiece and seeds); null = execute directly.</param>
    public static (bool Placed, string Message) Place(ModuleSession s, MapDef map, PieceSlotDef slot, PointD mapPoint, bool snap, IActionPort? actions = null)
    {
        if (s.Game is not { } game) return (false, "No game is running");
        if (ResolveDrop(game.State, map, mapPoint, s.Images, snap) is not { } t) return (false, "Drop onto a board");
        var (type, state) = PieceFactory.FromSlot(s.Model, slot, t.MapId, t.Pos);
        var id = "p:" + Guid.NewGuid().ToString("N");
        var add = new AddPiece(id, type, state, t.MapId, t.Pos, t.StackOnto);
        if (actions != null) actions.PieceCommand(add);
        else
        {
            game.Execute(CommandLog.Prepare(add, game.State, s));
            s.SeedLedgers();
        }
        return (true, $"Placed {PieceName(slot)}");
    }

    private static bool OnBoard(MapDef map, PointD p) =>
        map.Boards.Any(b => p.X >= b.Offset.X && p.Y >= b.Offset.Y && p.X < b.Offset.X + b.Width && p.Y < b.Offset.Y + b.Height);
}
