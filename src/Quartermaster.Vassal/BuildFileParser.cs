using System.Text;
using System.Xml;

namespace Quartermaster.Vassal;

public static class BuildFileParser
{
    /// <summary>Parses a VASSAL buildFile into a generic tree. Throws <see cref="XmlException"/> on malformed XML or any DTD.</summary>
    public static ModuleNode Parse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
        };
        using var reader = XmlReader.Create(xml, settings);
        ModuleNode? root = null, cur = null;
        var texts = new Stack<StringBuilder>();
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                    var empty = reader.IsEmptyElement;
                    if (reader.HasAttributes)
                        while (reader.MoveToNextAttribute()) attrs[reader.Name] = reader.Value;
                    reader.MoveToElement();
                    var node = new ModuleNode(reader.Name, attrs, cur);
                    if (cur != null) cur.AddChild(node); else root ??= node;
                    if (!empty)
                    {
                        cur = node;
                        texts.Push(new StringBuilder());
                    }
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (texts.Count > 0) texts.Peek().Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (cur != null)
                    {
                        cur.Text = texts.Pop().ToString();
                        cur = cur.Parent;
                    }
                    break;
            }
        }
        return root ?? throw new XmlException("buildFile has no root element");
    }
}
