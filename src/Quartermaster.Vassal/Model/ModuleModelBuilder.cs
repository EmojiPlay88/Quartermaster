// Board arrangement ported from VASSAL BoardPicker.java (default setup decode / getDefaultSetup) and Map.java
// (setBoardBoundaries / getLocation): boards sit in a column/row grid, each cell offset by the summed widths of the
// cells to its left and the summed heights of the cells above it, plus the map edge buffer (LGPL-2.1).
using System.Globalization;

namespace Quartermaster.Vassal;

public static class ModuleModelBuilder
{
    private const int MaxCell = 64;

    private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
    {
        "ForwardToKeyBuffer", "ForwardToChatter", "Scroller", "MenuDisplayer", "MapCenterer", "StackExpander", "PieceMover",
        "KeyBufferer", "ImageSaver", "CounterDetailViewer", "SelectionHighlighters", "HighlightLastMoved", "Flare", "Zoomer",
        "ZonedGridHighlighter",
        "GlobalOptions", "Chatter", "KeyNamer", "GlobalTranslatableMessages", "Language", "ColorManager", "FontManager",
        "FontStyle", "BasicCommandEncoder", "Documentation", "AboutScreen", "GamePieceImageDefinitions", "PrototypesContainer",
        "GlobalProperties",
    };

    /// <summary>Containers whose children are walked; their name is pushed onto the NavPath.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "PieceWindow", "ChartWindow", "TabWidget", "PanelWidget", "ListWidget", "BoxWidget", "MapWidget",
    };

    public static ModuleModel Build(VmodArchive archive, DiagnosticLog log)
    {
        var b = new Builder(archive, log);
        return b.Run();
    }

    private sealed class Builder(VmodArchive archive, DiagnosticLog log)
    {
        private readonly List<MapDef> _maps = new();
        private readonly List<PieceSlotDef> _slots = new();
        private readonly Dictionary<string, PieceType> _protos = new(StringComparer.Ordinal);
        private readonly List<ChartDef> _charts = new();
        private readonly List<DocumentDef> _docs = new();
        private readonly List<SetupDef> _setups = new();
        private readonly List<DieDef> _dice = new();
        private readonly List<string> _sides = new();
        private readonly Dictionary<string, int> _unknown = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (SizeD? size, string? image)> _imageCache = new(StringComparer.Ordinal);

        public ModuleModel Run()
        {
            var root = archive.Root;
            foreach (var c in root.Children) Visit(c, []);

            foreach (var name in archive.EntryNames)
            {
                if (name.Contains('/') || !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                _docs.Add(new DocumentDef(name, name, DocumentKind.Text));
            }

            foreach (var (cls, count) in _unknown)
                log.AddOnce("component:" + cls, DiagnosticSeverity.Info, "component",
                    $"{count} unmodelled element(s) of type '{cls}' ignored.", cls);

            var expander = new PrototypeExpander(_protos, log);
            return new ModuleModel(root.Attr("name"), root.Attr("version"), root.Attr("VassalVersion"), _maps, _slots, _protos,
                _charts, _docs, _setups, _dice, _sides, BuildTree(root.Attr("name")), expander);
        }

        private void Unknown(ModuleNode n) => _unknown[n.ClassName] = _unknown.GetValueOrDefault(n.ClassName) + 1;

        private static string WidgetName(ModuleNode n)
        {
            foreach (var key in new[] { "name", "entryName", "text" })
                if (n.Attr(key) is { Length: > 0 } v) return v;
            return n.ShortName;
        }

        private void Visit(ModuleNode n, List<string> nav)
        {
            var sn = n.ShortName;
            if (Plumbing.Contains(sn))
            {
                if (sn is "Documentation" or "PrototypesContainer") foreach (var c in n.Children) Visit(c, nav);
                return;
            }
            if (Containers.Contains(sn))
            {
                var inner = new List<string>(nav) { WidgetName(n) };
                foreach (var c in n.Children) Visit(c, inner);
                return;
            }
            switch (sn)
            {
                case "Map": AddMap(n, nav, false); break;
                case "WidgetMap": AddMap(n, nav, true); break;
                case "PieceSlot" or "CardSlot":
                    if (DecodeSlot(n, nav) is { } slot) _slots.Add(slot);
                    break;
                case "Chart" or "HtmlChart":
                    _charts.Add(new ChartDef(n.Attr("chartName", n.Attr("name")), n.Attr("fileName"), nav.ToArray()));
                    break;
                case "PrototypeDefinition": AddPrototype(n); break;
                case "PredefinedSetup": AddSetup(n, nav); break;
                case "SpecialDiceButton":
                    foreach (var die in n.Children)
                    {
                        if (die.ShortName != "SpecialDie") { Unknown(die); continue; }
                        _dice.Add(new DieDef(die.Attr("name"),
                            die.Children.Where(c => c.ShortName == "SpecialDieFace").Select(c => c.Attr("text")).ToArray()));
                    }
                    break;
                case "PlayerRoster":
                    foreach (var e in n.Children)
                    {
                        if (e.ShortName == "entry") _sides.Add(e.Text.Trim());
                        else Unknown(e);
                    }
                    break;
                case "BrowserPDFFile":
                    if (n.Attr("pdfFile") is { Length: > 0 } pdf)
                        _docs.Add(new DocumentDef(n.Attr("title", pdf), pdf, DocumentKind.Pdf));
                    break;
                case "HelpFile":
                    AddHelpFile(n);
                    break;
                default: Unknown(n); break;
            }
        }

        private void AddHelpFile(ModuleNode n)
        {
            var file = n.Attr("fileName");
            if (file.Length == 0) return;
            if (n.Attr("fileType", "archive") != "archive" && !archive.HasEntry(file))
            {
                log.AddOnce("help:" + file, DiagnosticSeverity.Info, "component", $"Help file '{file}' is not inside the module; skipped.", file);
                return;
            }
            var kind = file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Pdf
                     : file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Text
                     : DocumentKind.Html;
            _docs.Add(new DocumentDef(n.Attr("title", file), file, kind));
        }

        private void AddSetup(ModuleNode n, List<string> nav)
        {
            var name = n.Attr("name");
            if (string.Equals(n.Attr("isMenu"), "true", StringComparison.OrdinalIgnoreCase))
            {
                var inner = new List<string>(nav) { name };
                foreach (var c in n.Children) Visit(c, inner);
                return;
            }
            var useFile = !string.Equals(n.Attr("useFile", "true"), "false", StringComparison.OrdinalIgnoreCase);
            var file = n.Attr("file");
            _setups.Add(new SetupDef(name, useFile && file.Length > 0 ? file : null, nav.ToArray()));
        }

        private void AddPrototype(ModuleNode n)
        {
            var name = n.Attr("name");
            if (!SplitPiece(n.Text, out var type, out _))
            {
                log.AddOnce("proto-bad:" + name, DiagnosticSeverity.Warning, "component", $"Prototype '{name}' has malformed definition; skipped.", name);
                return;
            }
            _protos[name] = PieceTypeParser.Parse(type, log, "prototype " + name);
        }

        /// <summary>Splits an AddPiece command "+/id/type/state" (VASSAL PieceSlot / PrototypeDefinition text).</summary>
        private static bool SplitPiece(string text, out string type, out string state)
        {
            type = state = "";
            var t = SequenceEncoder.Decode(text?.Trim(), '/');
            if (t.Count < 3 || t[0] != "+") return false;
            type = t[2];
            state = t.Count > 3 ? t[3] : "";
            return true;
        }

        private PieceSlotDef? DecodeSlot(ModuleNode n, IReadOnlyList<string> nav)
        {
            var name = n.Attr("entryName");
            if (!SplitPiece(n.Text, out var type, out var state))
            {
                log.Add(DiagnosticSeverity.Warning, "component", $"Piece slot '{name}' has malformed definition; skipped.", name);
                return null;
            }
            return new PieceSlotDef(name, n.Attr("gpid"), type, state, GridAttr.Int(n, "width", 0, log), GridAttr.Int(n, "height", 0, log), nav.ToArray());
        }

        // ---- maps ----

        private void AddMap(ModuleNode n, List<string> nav, bool org)
        {
            var id = n.Attr("mapName");
            var stacking = new StackMetricsDef(6, 18, 2, 4, false);
            var stacks = new List<SetupStackDef>();
            var boardNodes = new List<ModuleNode>();
            string? setup = null;
            foreach (var c in n.Children)
            {
                switch (c.ShortName)
                {
                    case "BoardPicker":
                        foreach (var k in c.Children)
                        {
                            if (k.ShortName == "Board") boardNodes.Add(k);
                            else if (k.ShortName == "setup") setup ??= k.Text;
                            else Unknown(k);
                        }
                        break;
                    case "StackMetrics":
                        stacking = new StackMetricsDef(
                            Num(c, "exSepX", 6), Num(c, "exSepY", 18), Num(c, "unexSepX", 2), Num(c, "unexSepY", 4),
                            string.Equals(c.Attr("disabled"), "true", StringComparison.OrdinalIgnoreCase));
                        break;
                    case "SetupStack":
                        stacks.Add(new SetupStackDef(c.Attr("name"), c.Attr("owningBoard"),
                            string.Equals(c.Attr("useGridLocation"), "true", StringComparison.OrdinalIgnoreCase), c.Attr("location"),
                            Num(c, "x", 0), Num(c, "y", 0),
                            c.Children.Where(k => k.ShortName is "PieceSlot" or "CardSlot").Select(k => DecodeSlot(k, [])).OfType<PieceSlotDef>().ToArray()));
                        break;
                    default:
                        if (!Plumbing.Contains(c.ShortName)) Unknown(c);
                        break;
                }
            }

            var (ew, eh) = (Num(n, "edgeWidth", 0), Num(n, "edgeHeight", 0));
            if (n.Attr("edge") is { Length: > 0 } legacy && n.Attributes.ContainsKey("edgeWidth") == false)
            {
                var parts = legacy.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var lw) && int.TryParse(parts[1], out var lh)) (ew, eh) = (lw, lh);
            }
            var bg = RgbColor.Parse(n.Attr("backgroundcolor")) ?? new RgbColor(255, 255, 255);
            var boards = LayoutBoards(id, boardNodes, setup, ew, eh);
            _maps.Add(new MapDef(id, id, org, nav.ToArray(), boards, ew, eh, bg, stacking, stacks, n.Attr("markMoved", "Always")));
        }

        private static int Num(ModuleNode n, string key, int def) =>
            int.TryParse(n.Attr(key).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : def;

        /// <summary>
        /// Decides which boards (and where) a map uses, as BoardPicker.getDefaultSetup does: the picker's &lt;setup&gt; text
        /// ("&lt;map&gt;BoardPicker\tname[/rev]\tcol\trow..."), else a lone non-reversible board. When neither exists (VASSAL would
        /// ask the player) every board is placed left to right in declaration order. Reversed boards are not rotated.
        /// </summary>
        private IReadOnlyList<BoardDef> LayoutBoards(string mapId, List<ModuleNode> nodes, string? setup, int edgeW, int edgeH)
        {
            var byName = new Dictionary<string, ModuleNode>(StringComparer.Ordinal);
            foreach (var bn in nodes) byName.TryAdd(bn.Attr("name"), bn);

            var cells = new List<(ModuleNode node, int col, int row)>();
            if (!string.IsNullOrWhiteSpace(setup))
            {
                var d = new SequenceDecoder(setup.Trim(), '\t');
                var head = d.NextToken();
                if (!head.EndsWith("BoardPicker", StringComparison.Ordinal))
                    log.AddOnce("boardsetup:" + mapId, DiagnosticSeverity.Info, "board", $"Board setup of map '{mapId}' not recognised; boards laid out in order.", mapId);
                else
                {
                    while (d.HasMoreTokens)
                    {
                        var name = SequenceEncoder.Decode(d.NextToken(), '/').FirstOrDefault() ?? "";
                        int col = d.NextInt(0), row = d.NextInt(0);
                        if (!byName.TryGetValue(name, out var bn))
                            log.AddOnce($"boardsetup-missing:{mapId}:{name}", DiagnosticSeverity.Warning, "board", $"Board setup of map '{mapId}' names unknown board '{name}'.", mapId);
                        else if (col is < 0 or > MaxCell || row is < 0 or > MaxCell)
                            log.AddOnce($"boardsetup-pos:{mapId}:{name}", DiagnosticSeverity.Warning, "board", $"Board '{name}' has out-of-range position {col},{row}; skipped.", mapId);
                        else cells.Add((bn, col, row));
                    }
                }
            }
            if (cells.Count == 0)
            {
                if (nodes.Count == 1) cells.Add((nodes[0], 0, 0));
                else for (var i = 0; i < nodes.Count && i <= MaxCell; i++) cells.Add((nodes[i], i, 0));
            }

            var defs = new Dictionary<ModuleNode, (string? image, int w, int h, IMapGrid? grid)>();
            foreach (var (node, _, _) in cells)
            {
                if (defs.ContainsKey(node)) continue;
                var (image, w, h) = BoardSize(node);
                defs[node] = (image, w, h, GridFactory.From(node, log, new SizeD(w, h)));
            }

            var widths = new Dictionary<(int, int), int>();
            var heights = new Dictionary<(int, int), int>();
            foreach (var (node, col, row) in cells) { widths[(col, row)] = defs[node].w; heights[(col, row)] = defs[node].h; }

            var result = new List<BoardDef>();
            foreach (var (node, col, row) in cells)
            {
                double dx = edgeW, dy = edgeH;
                for (var x = 0; x < col; x++) dx += widths.GetValueOrDefault((x, row));
                for (var y = 0; y < row; y++) dy += heights.GetValueOrDefault((col, y));
                var (image, w, h, grid) = defs[node];
                result.Add(new BoardDef(node.Attr("name"), image, w, h, grid, new PointD(dx, dy)));
            }
            return result;
        }

        private (string? image, int w, int h) BoardSize(ModuleNode board)
        {
            var image = board.Attr("image");
            var attrW = Num(board, "width", 0);
            var attrH = Num(board, "height", 0);
            if (image.Length == 0) return (null, attrW, attrH);

            if (!_imageCache.TryGetValue(image, out var hit))
            {
                SizeD? size = null;
                try
                {
                    using var s = archive.OpenEntry(VmodArchive.ImagePath(image));
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    size = ImageSize.Read(image, ms.ToArray());
                }
                catch (Exception) { }
                hit = (size, image);
                _imageCache[image] = hit;
            }
            if (hit.size is { } sz) return (image, (int)Math.Round(sz.W), (int)Math.Round(sz.H));

            log.AddOnce("board-image:" + image, DiagnosticSeverity.Warning, "board",
                $"Could not read the size of board image '{image}'" + (attrW > 0 && attrH > 0 ? "; using width/height attributes." : "; size unknown (0x0)."), image);
            return (image, attrW, attrH);
        }

        // ---- window tree ----

        private WindowTreeNode BuildTree(string moduleName)
        {
            var maps = _maps.Where(m => !m.IsOrgDisplay).Select(m => new WindowTreeNode(m.Name, WindowNodeKind.Map, m, [])).ToArray();
            var orgs = Nest(_maps.Where(m => m.IsOrgDisplay), m => m.NavPath, m => new WindowTreeNode(m.Name, WindowNodeKind.OrgDisplay, m, []));
            var charts = Nest(_charts, c => c.NavPath, c => new WindowTreeNode(c.Name, WindowNodeKind.Chart, c, []));
            var docs = _docs.Select(d => new WindowTreeNode(d.Title, WindowNodeKind.Document, d, [])).ToArray();
            var setups = Nest(_setups, s => s.NavPath, s => new WindowTreeNode(s.Name, WindowNodeKind.Setup, s, []));
            return new WindowTreeNode(moduleName, WindowNodeKind.Folder, null, new[]
            {
                Folder("Maps", maps), Folder("Organisation Displays", orgs), Folder("Charts", charts),
                Folder("Rulebooks & Documents", docs), Folder("Setups", setups),
            });
        }

        private static WindowTreeNode Folder(string title, IReadOnlyList<WindowTreeNode> kids) => new(title, WindowNodeKind.Folder, null, kids);

        /// <summary>Groups leaves into nested folders named by their NavPath, preserving first-seen order.</summary>
        private static IReadOnlyList<WindowTreeNode> Nest<T>(IEnumerable<T> items, Func<T, IReadOnlyList<string>> path, Func<T, WindowTreeNode> leaf)
        {
            var root = new Dir();
            foreach (var it in items)
            {
                var d = root;
                foreach (var seg in path(it))
                {
                    if (!d.Sub.TryGetValue(seg, out var next)) { next = new Dir(); d.Sub[seg] = next; d.Order.Add((seg, next, null)); }
                    d = next;
                }
                d.Order.Add((null!, null!, leaf(it)));
            }
            return root.ToNodes();
        }

        private sealed class Dir
        {
            public readonly Dictionary<string, Dir> Sub = new(StringComparer.Ordinal);
            public readonly List<(string name, Dir dir, WindowTreeNode? leaf)> Order = new();

            public IReadOnlyList<WindowTreeNode> ToNodes() =>
                Order.Select(o => o.leaf ?? new WindowTreeNode(o.name, WindowNodeKind.Folder, null, o.dir.ToNodes())).ToArray();
        }
    }
}
