namespace Quartermaster.Vassal;

public sealed record BoardDef(string Name, string? Image, int Width, int Height, IMapGrid? Grid, PointD Offset);

public sealed record StackMetricsDef(int ExSepX, int ExSepY, int UnexSepX, int UnexSepY, bool Disabled);

public sealed record PieceSlotDef(string EntryName, string Gpid, string TypeString, string StateString, int Width, int Height, IReadOnlyList<string> NavPath);

public sealed record SetupStackDef(string Name, string OwningBoard, bool UseGridLocation, string Location, int X, int Y, IReadOnlyList<PieceSlotDef> Pieces);

public sealed record MapDef(string Id, string Name, bool IsOrgDisplay, IReadOnlyList<string> NavPath, IReadOnlyList<BoardDef> Boards,
    int EdgeWidth, int EdgeHeight, RgbColor Background, StackMetricsDef Stacking, IReadOnlyList<SetupStackDef> SetupStacks, string MarkMoved);

public sealed record ChartDef(string Name, string FileName, IReadOnlyList<string> NavPath);

public enum DocumentKind { Pdf, Html, Text }

public sealed record DocumentDef(string Title, string EntryName, DocumentKind Kind);

public sealed record SetupDef(string Name, string? FileEntry, IReadOnlyList<string> NavPath);

public sealed record DieDef(string Name, IReadOnlyList<string> Faces);

public enum WindowNodeKind { Folder, Map, OrgDisplay, Chart, Document, Setup }

public sealed class WindowTreeNode(string title, WindowNodeKind kind, object? reference, IReadOnlyList<WindowTreeNode> children)
{
    public string Title { get; } = title;
    public WindowNodeKind Kind { get; } = kind;
    public object? Ref { get; } = reference;
    public IReadOnlyList<WindowTreeNode> Children { get; } = children;
}
