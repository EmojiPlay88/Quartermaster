namespace Quartermaster.Vassal;

/// <summary>Generic node of the buildFile.xml component tree.</summary>
public sealed class ModuleNode
{
    private readonly Dictionary<string, string> _attributes;
    private readonly List<ModuleNode> _children = new();

    public ModuleNode(string className, Dictionary<string, string> attributes, ModuleNode? parent)
    {
        ClassName = className;
        _attributes = attributes;
        Parent = parent;
        var dot = className.LastIndexOf('.');
        ShortName = dot >= 0 ? className[(dot + 1)..] : className;
    }

    public string ClassName { get; }
    public string ShortName { get; }
    public IReadOnlyDictionary<string, string> Attributes => _attributes;
    public string Text { get; internal set; } = "";
    public IReadOnlyList<ModuleNode> Children => _children;
    public ModuleNode? Parent { get; }

    internal void AddChild(ModuleNode child) => _children.Add(child);

    public string Attr(string name, string def = "") => _attributes.TryGetValue(name, out var v) ? v : def;

    /// <summary>All descendants (depth-first, document order) whose <see cref="ShortName"/> matches.</summary>
    public IEnumerable<ModuleNode> Descendants(string shortName)
    {
        foreach (var c in _children)
        {
            if (c.ShortName == shortName) yield return c;
            foreach (var d in c.Descendants(shortName)) yield return d;
        }
    }
}
