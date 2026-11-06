using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The tracker strip under the toolbar: "Turn n", the block text, one chip per phase (done greyed, current highlighted, with the acting side),
/// and the pass / relocation notice at the right. Chips wrap onto up to 3 rows; beyond that the strip is one row and scrolls horizontally.
/// Before the game starts it shows the Start game button; with no phases only the no-phases text.
/// </summary>
sealed class PhaseStrip : Panel
{
    public const string NoPhasesText = "No phases defined — add them in the Rules Workbench (Phases tab)";
    const int MaxRows = 3, PadX = 6, Gap = 4, Margin0 = 4;

    enum Kind { Label, Done, Current, Upcoming, Notice }
    sealed record Item(string Text, Kind Kind, Rectangle Bounds);

    readonly Button _start = new() { Text = "Start game ▶", AutoSize = true, FlatStyle = FlatStyle.System, Visible = false };
    readonly Font _bold;
    PhaseView? _view;
    List<Item> _items = new();
    int _laidWidth = -1;

    public PhaseStrip()
    {
        Dock = DockStyle.Top;
        Height = 28;
        AutoScroll = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        _bold = new Font(Font, FontStyle.Bold);
        _start.Click += (_, _) => StartRequested?.Invoke();
        Controls.Add(_start);
        Name = "PhaseStrip";
    }

    public event Action? StartRequested;

    public PhaseView? ViewForSmoke => _view;

    /// <summary>Texts of the laid-out items, for smoke checks.</summary>
    public IReadOnlyList<string> ItemTexts => _items.Select(i => i.Text).ToList();
    public bool StartVisible => _start.Visible;
    public int RowsForSmoke { get; private set; }

    public void SetView(PhaseView v)
    {
        _view = v;
        Relayout();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_view != null && ClientSize.Width != _laidWidth) Relayout();
    }

    List<(string Text, Kind Kind)> Build(PhaseView v)
    {
        var l = new List<(string, Kind)>();
        if (v.NoPhases) { l.Add((NoPhasesText, Kind.Label)); return l; }
        if (v.BeforeStart) l.Add(("Before turn 1", Kind.Label));
        else
        {
            l.Add(($"Turn {v.Turn}", Kind.Label));
            if (!string.IsNullOrWhiteSpace(v.StageText)) l.Add((v.StageText!, Kind.Label));
        }
        foreach (var c in v.Chips)
        {
            var text = c.State == ChipState.Current && !string.IsNullOrEmpty(v.SideName) ? $"{v.SideName} player — {c.Name}" : c.Name;
            l.Add((text, c.State switch { ChipState.Done => Kind.Done, ChipState.Current => Kind.Current, _ => Kind.Upcoming }));
        }
        return l;
    }

    void Relayout()
    {
        if (_view is not { } v) return;
        var specs = Build(v);
        var rowH = Math.Max(Font.Height, _bold.Height) + 8;
        var startW = v.BeforeStart && !v.NoPhases ? _start.PreferredSize.Width + Gap : 0;
        var noticeW = string.IsNullOrEmpty(v.Notice) ? 0 : TextRenderer.MeasureText(v.Notice, _bold).Width + 2 * PadX;
        _start.Visible = startW > 0;
        _laidWidth = ClientSize.Width;
        var avail = Math.Max(120, _laidWidth);

        Item[] Place(int wrapWidth, bool wrap, out int rows, out int maxX)
        {
            var res = new List<Item>();
            var x = Margin0;
            var row = 0;
            maxX = x;
            if (startW > 0) x += startW;
            for (var i = 0; i < specs.Count; i++)
            {
                var (text, kind) = specs[i];
                var f = kind == Kind.Current ? _bold : Font;
                var w = TextRenderer.MeasureText(text, f).Width + (kind == Kind.Label ? Gap : 2 * PadX);
                var reserve = row == 0 ? noticeW : 0;
                if (wrap && x + w > wrapWidth - reserve && x > Margin0) { row++; x = Margin0; }
                res.Add(new Item(text, kind, new Rectangle(x, 2 + row * rowH, w, rowH - 2)));
                x += w + Gap;
                maxX = Math.Max(maxX, x);
            }
            rows = row + 1;
            return res.ToArray();
        }

        var items = Place(avail, true, out var rows, out var maxX);
        var scroll = rows > MaxRows;
        if (scroll) items = Place(avail, false, out rows, out maxX);
        var items2 = items.ToList();
        if (noticeW > 0)
        {
            var nx = scroll ? maxX : Math.Max(maxX, avail - noticeW - Margin0);
            items2.Add(new Item(v.Notice!, Kind.Notice, new Rectangle(nx, 2, noticeW, rowH - 2)));
            maxX = nx + noticeW + Margin0;
        }
        _items = items2;
        RowsForSmoke = scroll ? 1 : rows;

        if (startW > 0)
        {
            _start.Location = new Point(Margin0, 2 + (rowH - 2 - _start.Height) / 2);
            _start.Height = Math.Min(_start.Height, rowH);
        }
        SuspendLayout();
        HorizontalScroll.Visible = scroll;
        AutoScrollMinSize = scroll ? new Size(maxX, 0) : Size.Empty;
        var h = (scroll ? 1 : rows) * rowH + 4 + (scroll ? SystemInformation.HorizontalScrollBarHeight : 0);
        if (Height != h) Height = h;
        ResumeLayout(false);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        foreach (var it in _items)
        {
            switch (it.Kind)
            {
                case Kind.Label:
                    TextRenderer.DrawText(g, it.Text, Font, it.Bounds, SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;
                case Kind.Notice:
                    TextRenderer.DrawText(g, it.Text, _bold, it.Bounds, SystemColors.HotTrack, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                    break;
                default:
                {
                    var cur = it.Kind == Kind.Current;
                    var done = it.Kind == Kind.Done;
                    var back = cur ? SystemColors.Highlight : done ? SystemColors.ControlLight : SystemColors.Window;
                    var fore = cur ? SystemColors.HighlightText : done ? SystemColors.GrayText : SystemColors.ControlText;
                    using (var b = new SolidBrush(back)) g.FillRectangle(b, it.Bounds);
                    ControlPaint.DrawBorder3D(g, it.Bounds, done ? Border3DStyle.Flat : Border3DStyle.Etched);
                    TextRenderer.DrawText(g, it.Text, cur ? _bold : Font, it.Bounds, fore,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                    break;
                }
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold.Dispose();
        base.Dispose(disposing);
    }
}
