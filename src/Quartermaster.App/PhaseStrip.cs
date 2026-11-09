using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The tracker strip under the toolbar: "Turn n", the block text, one chip per phase (done greyed, current highlighted, with the acting side),
/// and the pass / relocation notice at the right. Chips wrap onto up to 3 rows; beyond that the strip is one row and scrolls horizontally.
/// Before the game starts it shows the Start game button; with no phases only the no-phases text.
/// </summary>
sealed class PhaseStrip : Panel
{
    public const string NoPhasesText = PhaseTexts.NoPhasesText;
    const int MaxRows = 3, PadX = 6, Gap = 4, Margin0 = 4;

    enum Kind { Label, Done, Current, Upcoming }
    sealed record Item(string Text, Kind Kind, Rectangle Bounds, string? Notes = null);

    readonly Button _start = new() { Text = PhaseTexts.StartGameText, AutoSize = true, FlatStyle = FlatStyle.System, Visible = false };
    readonly Font _bold;
    readonly ToolTip _tip = new() { ShowAlways = true, UseAnimation = false, UseFading = false, AutoPopDelay = 30000 };
    string? _tipText;
    PhaseView? _view;
    List<Item> _items = new();
    int _laidWidth = -1;
    string? _noticeText;
    Rectangle _noticeRect;

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

    /// <summary>False while the game may not change (a received turn replaying): Start game is shown greyed.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool StartEnabled
    {
        get => _start.Enabled;
        set => _start.Enabled = value;
    }

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

    static List<(string Text, Kind Kind, string? Notes)> Build(PhaseView v) =>
        PhaseTexts.Strip(v).Select(c => (c.Text, c.State switch
        {
            null => Kind.Label, ChipState.Done => Kind.Done, ChipState.Current => Kind.Current, _ => Kind.Upcoming,
        }, c.Tooltip)).ToList();

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
                var (text, kind, notes) = specs[i];
                var f = kind == Kind.Current ? _bold : Font;
                var w = TextRenderer.MeasureText(text, f).Width + (kind == Kind.Label ? Gap : 2 * PadX);
                var reserve = row == 0 ? noticeW : 0;
                if (wrap && x + w > wrapWidth - reserve && x > Margin0) { row++; x = Margin0; }
                res.Add(new Item(text, kind, new Rectangle(x, 2 + row * rowH, w, rowH - 2), notes));
                x += w + Gap;
                maxX = Math.Max(maxX, x);
            }
            rows = row + 1;
            return res.ToArray();
        }

        var items = Place(avail, true, out var rows, out var maxX);
        var scroll = rows > MaxRows;
        if (scroll) items = Place(avail, false, out rows, out maxX);
        _items = items.ToList();
        // The notice lives in a fixed area at the right of the first row; it does not scroll with the chips.
        _noticeText = noticeW > 0 ? v.Notice : null;
        _noticeRect = noticeW > 0 ? new Rectangle(Math.Max(Margin0, avail - noticeW - Margin0), 2, noticeW, rowH - 2) : Rectangle.Empty;
        if (scroll && noticeW > 0) maxX += noticeW + Margin0; // the last chip can scroll clear of the notice
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        try
        {
            var p = new Point(e.X - AutoScrollPosition.X, e.Y - AutoScrollPosition.Y);
            var text = _items.FirstOrDefault(i => i.Bounds.Contains(p))?.Notes;
            if (string.IsNullOrWhiteSpace(text)) text = null;
            if (text == _tipText) return;
            _tipText = text;
            if (text == null) _tip.Hide(this);
            else _tip.Show(text, this, e.X + 12, e.Y + 18);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipText = null;
        try { _tip.Hide(this); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>Notes tooltip text of the chip with this name, for smoke checks.</summary>
    public string? NotesForSmoke(string chipText) => _items.FirstOrDefault(i => i.Text == chipText || i.Text.EndsWith(" — " + chipText, StringComparison.Ordinal))?.Notes;

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
        g.ResetTransform();
        if (_noticeText != null)
        {
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, _noticeRect);
            TextRenderer.DrawText(g, _noticeText, _bold, _noticeRect, SystemColors.HotTrack, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
