using Quartermaster.Render.Search;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>An editor whose items carry a rule ref (Formulas, Tables, Phases): the workbench links it to the rulebook search.</summary>
interface IRuleRefEditor
{
    /// <summary>An item is selected and editable, so "Use as rule ref" can fill its rule ref.</summary>
    bool CanUseRuleRef { get; }

    /// <summary>Fills the selected item's rule ref with the ref <paramref name="make"/> returns for the item's name (null: nothing changes).</summary>
    bool UseRuleRef(Func<string, RuleRef?> make);

    /// <summary>Redraws the rule-ref link (the index finished, or a document changed).</summary>
    void RefreshRuleRef();

    /// <summary>Set by the workbench: runs a rulebook search ("Find in rulebook").</summary>
    Action<string>? FindInRulebook { get; set; }

    /// <summary>Set by the workbench: how a rule ref reads and whether it is a link.</summary>
    Func<RuleRef?, RefDisplay>? DescribeRef { get; set; }
}

/// <summary>The rule-ref link shown in the editors: opens the page when clicked; "(missing)" is greyed and does nothing.</summary>
static class RuleRefLink
{
    public static LinkLabel Create() => new() { AutoSize = true, Padding = new Padding(0, 5, 8, 0), LinkBehavior = LinkBehavior.HoverUnderline };

    public static void Show(LinkLabel label, RuleRef? r, Func<RuleRef?, RefDisplay>? describe)
    {
        var d = describe?.Invoke(r) ?? (r == null ? new RefDisplay(SearchPanelModel.None, false, false) : new RefDisplay($"{r.Label}, page {r.Page}", true, false));
        label.Text = d.Text;
        label.LinkArea = d.IsLink ? new LinkArea(0, d.Text.Length) : new LinkArea(0, 0);
        label.ForeColor = d.Missing ? SystemColors.GrayText : SystemColors.ControlText;
        label.Tag = d;
    }

    public static Button FindButton() => new() { Text = "Find in rulebook", AutoSize = true, Visible = false };
}

/// <summary>
/// Rulebook search results, docked under the PDF pane of the workbench: one heading per document, then its matching pages with the
/// hits in bold; documents that cannot be searched are listed greyed. Double-click or Enter opens the page; the context menu's
/// "Use as rule ref" hands the page to the selected editor item. Rows come from <see cref="SearchPanelModel"/>.
/// </summary>
sealed class SearchPanel : UserControl
{
    public const int ExpandedHeight = 190;
    public const int HeaderHeight = 24;

    readonly Label _title = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Rulebook search", AutoEllipsis = true };
    readonly Button _toggle = new() { Dock = DockStyle.Right, Width = 24, FlatStyle = FlatStyle.System, Text = "▾" };
    readonly ListBox _list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false, HorizontalScrollbar = false };
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _useRef = new("Use as rule ref");
    readonly Font _bold;
    IReadOnlyList<SearchRow> _rows = Array.Empty<SearchRow>();
    bool _collapsed;

    /// <summary>A hit row was opened (double-click or Enter): document entry and page.</summary>
    public event Action<string, int>? HitActivated;

    /// <summary>"Use as rule ref" chosen on a hit row: document entry and page.</summary>
    public event Action<string, int>? UseAsRuleRef;

    /// <summary>Asked when the context menu opens: is there an editor item that can take a rule ref?</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? CanUseRuleRef { get; set; }

    /// <summary>Collapsed or expanded by the arrow button; the host resizes the panel.</summary>
    public event Action? CollapsedChanged;

    public SearchPanel()
    {
        _bold = new Font(Font, FontStyle.Bold);
        var head = new Panel { Dock = DockStyle.Top, Height = HeaderHeight, Padding = new Padding(4, 0, 2, 0), BackColor = SystemColors.ControlLight };
        head.Controls.Add(_title);
        head.Controls.Add(_toggle);
        Controls.Add(_list);
        Controls.Add(head);
        _menu.Items.Add(_useRef);
        _list.ContextMenuStrip = _menu;

        _toggle.Click += (_, _) => Collapsed = !Collapsed;
        _list.MeasureItem += OnMeasure;
        _list.DrawItem += OnDraw;
        _list.DoubleClick += (_, _) => Activate(_list.SelectedIndex);
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            Activate(_list.SelectedIndex);
            e.Handled = e.SuppressKeyPress = true;
        };
        _list.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var i = _list.IndexFromPoint(e.Location);
            if (i >= 0) _list.SelectedIndex = i;
        };
        _list.Resize += (_, _) => _list.Invalidate();
        _menu.Opening += (_, e) =>
        {
            var hit = SelectedHit;
            _useRef.Enabled = hit != null && (CanUseRuleRef?.Invoke() ?? false);
            if (hit == null) e.Cancel = true;
        };
        _useRef.Click += (_, _) => { if (SelectedHit is { } h) UseAsRuleRef?.Invoke(h.Entry, h.Page); };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold.Dispose(); _menu.Dispose(); }
        base.Dispose(disposing);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            _list.Visible = !value;
            _toggle.Text = value ? "▸" : "▾";
            CollapsedChanged?.Invoke();
        }
    }

    /// <summary>The header's title line (status: "Indexing…", counts, the query).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public IReadOnlyList<SearchRow> Rows => _rows;

    public void ShowRows(IReadOnlyList<SearchRow> rows)
    {
        _rows = rows ?? Array.Empty<SearchRow>();
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var r in _rows) _list.Items.Add(r);
            var first = _rows.ToList().FindIndex(r => r.Kind == SearchRowKind.Hit);
            if (first >= 0) _list.SelectedIndex = first;
        }
        finally { _list.EndUpdate(); }
        if (Collapsed && _rows.Count > 0) Collapsed = false;
    }

    SearchRow? SelectedHit => _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count && _rows[_list.SelectedIndex].Kind == SearchRowKind.Hit
        ? _rows[_list.SelectedIndex] : null;

    void Activate(int index)
    {
        if (index < 0 || index >= _rows.Count || _rows[index].Kind != SearchRowKind.Hit) return;
        HitActivated?.Invoke(_rows[index].Entry, _rows[index].Page);
    }

    /// <summary>Smoke: selects the first hit row on the given page (any page when 0); false when there is none.</summary>
    public bool SelectHitForSmoke(int page)
    {
        var i = _rows.ToList().FindIndex(r => r.Kind == SearchRowKind.Hit && (page == 0 || r.Page == page));
        if (i < 0) return false;
        _list.SelectedIndex = i;
        return true;
    }

    /// <summary>Smoke: opens the selected hit as Enter does.</summary>
    public void ActivateSelectedForSmoke() => Activate(_list.SelectedIndex);

    /// <summary>Smoke: runs "Use as rule ref" on the selected hit as the menu does (false when the menu item would be disabled).</summary>
    public bool UseSelectedForSmoke()
    {
        if (SelectedHit is not { } h || !(CanUseRuleRef?.Invoke() ?? false)) return false;
        UseAsRuleRef?.Invoke(h.Entry, h.Page);
        return true;
    }

    // ---------- owner draw ----------

    const int PageColumn = 44;
    const int HitLines = 2;

    int LineHeight => Font.Height + 1;

    void OnMeasure(object? sender, MeasureItemEventArgs e)
    {
        var kind = e.Index >= 0 && e.Index < _rows.Count ? _rows[e.Index].Kind : SearchRowKind.Message;
        e.ItemHeight = kind == SearchRowKind.Hit ? LineHeight * HitLines + 4 : LineHeight + 4;
    }

    void OnDraw(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _rows.Count) return;
        var row = _rows[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0 && row.Kind == SearchRowKind.Hit;
        var g = e.Graphics;
        using (var back = new SolidBrush(selected ? SystemColors.Highlight : row.Kind == SearchRowKind.Document ? SystemColors.Control : SystemColors.Window))
            g.FillRectangle(back, e.Bounds);
        var fore = selected ? SystemColors.HighlightText : row.Kind is SearchRowKind.NotSearchable or SearchRowKind.Message ? SystemColors.GrayText : SystemColors.WindowText;
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var b = e.Bounds;
        switch (row.Kind)
        {
            case SearchRowKind.Hit:
                TextRenderer.DrawText(g, row.Text, Font, new Rectangle(b.X + 4, b.Y + 2, PageColumn - 6, LineHeight), fore, flags);
                var area = new Rectangle(b.X + PageColumn, b.Y + 2, Math.Max(10, b.Width - PageColumn - 4), LineHeight * HitLines);
                var saved = g.Save();
                g.SetClip(area);
                try { DrawRuns(g, row.Runs, area, fore); }
                finally { g.Restore(saved); }
                break;
            case SearchRowKind.Document:
                TextRenderer.DrawText(g, row.Text, _bold, new Rectangle(b.X + 4, b.Y + 2, b.Width - 8, LineHeight), fore, flags | TextFormatFlags.EndEllipsis);
                break;
            default:
                TextRenderer.DrawText(g, row.Text, Font, new Rectangle(b.X + 4, b.Y + 2, b.Width - 8, LineHeight), fore, flags | TextFormatFlags.EndEllipsis);
                break;
        }
        if ((e.State & DrawItemState.Focus) != 0 && row.Kind == SearchRowKind.Hit) e.DrawFocusRectangle();
    }

    /// <summary>Lays the runs out word by word over the lines of <paramref name="r"/>, bold where marked; the last line ends with "…" if text is left.</summary>
    void DrawRuns(Graphics g, IReadOnlyList<TextRun> runs, Rectangle r, Color fore)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
        var x = r.X;
        var line = 0;
        var space = TextRenderer.MeasureText(g, " ", Font, Size.Empty, flags).Width;
        var ellipsis = TextRenderer.MeasureText(g, "…", Font, Size.Empty, flags).Width;
        foreach (var run in runs)
        {
            var font = run.Bold ? _bold : Font;
            var parts = run.Text.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) x += space;
                var word = parts[i];
                if (word.Length == 0) continue;
                var w = TextRenderer.MeasureText(g, word, font, Size.Empty, flags).Width;
                if (x + w > r.Right && x > r.X)
                {
                    line++;
                    x = r.X;
                    if (line >= HitLines) return;
                }
                var y = r.Y + line * LineHeight;
                var last = line == HitLines - 1;
                if (last && x + w > r.Right - ellipsis)
                {
                    TextRenderer.DrawText(g, word, font, new Rectangle(x, y, Math.Max(0, r.Right - x), LineHeight), fore, flags | TextFormatFlags.EndEllipsis);
                    return;
                }
                TextRenderer.DrawText(g, word, font, new Point(x, y), fore, flags);
                x += w;
            }
        }
    }
}
