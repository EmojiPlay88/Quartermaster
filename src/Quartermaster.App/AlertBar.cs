using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// The bar under the toolbar: the "what now" banner on the left, one drawn chip per active alert on the right (severity accent, name, count).
/// Chips that do not fit collapse into a "+N" chip. Pure drawing and hit-testing; the owner supplies the data and reacts to the events.
/// </summary>
sealed class AlertBar : Panel
{
    const int ChipGap = 4, PadX = 7, MinBanner = 140; // logical pixels; the bar height scales with DPI, chip widths come from the font

    sealed record Chip(AlertHit? Hit, string Text, Rectangle Bounds);

    readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 20000 };
    readonly Font _bold;
    string _banner = "";
    string? _bannerTip;
    IReadOnlyList<AlertHit> _hits = Array.Empty<AlertHit>();
    List<Chip> _chips = new();
    int _overflow;
    Rectangle _bannerRect;
    object? _hover;
    string? _notice;
    Rectangle _noticeRect;
    static readonly object NoticeKey = new();

    /// <summary>Hot-seat: the alerts were evaluated on a filtered view, so chip tooltips say they cover visible units only.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool VisibleOnly { get; set; }

    public AlertBar()
    {
        Dock = DockStyle.Top;
        Height = LogicalToDeviceUnits(24);
        DoubleBuffered = true;
        ResizeRedraw = true;
        Visible = false;
        Name = "AlertBar";
        _bold = new Font(Font, FontStyle.Bold);
    }

    public event Action? BannerClicked;
    public event Action<AlertHit>? ChipClicked;
    public event Action<AlertHit, Point>? ChipContext;
    /// <summary>The "+N" chip was clicked: the alerts that did not fit, and where.</summary>
    public event Action<IReadOnlyList<AlertHit>, Point>? OverflowClicked;
    /// <summary>The notice chip (left of the banner) was clicked.</summary>
    public event Action? NoticeClicked;

    /// <summary>The notice drawn left of the banner (a waiting turn), or null.</summary>
    public string? NoticeText => _notice;

    /// <summary>Shows a clickable notice left of the banner ("New turn waiting from …"); null removes it.</summary>
    public void SetNotice(string? text)
    {
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
        DoLayout();
        Invalidate();
    }

    /// <summary>Chip texts currently drawn (the last may be "+N"), for smoke checks.</summary>
    public IReadOnlyList<string> ChipTexts => _chips.Select(c => c.Text).ToList();
    public string BannerText => _banner;

    public void SetBanner(string text, string? tooltip)
    {
        _banner = text;
        _bannerTip = tooltip;
        DoLayout();
        Invalidate();
    }

    public void SetHits(IReadOnlyList<AlertHit> hits)
    {
        _hits = hits;
        DoLayout();
        Invalidate();
    }

    static Color Accent(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => Color.FromArgb(200, 40, 40),
        AlertSeverity.Warning => Color.FromArgb(214, 140, 0),
        _ => Color.FromArgb(40, 100, 200),
    };

    static string Label(AlertHit h) => h.Matches.Count > 1 ? $"{h.Name} ({h.Matches.Count})" : h.Name;

    int Measure(string text) => TextRenderer.MeasureText(text, _bold, new Size(int.MaxValue, Height), TextFormatFlags.NoPadding).Width + PadX * 2 + 6;

    void DoLayout()
    {
        _chips = new();
        _overflow = 0;
        if (Width <= 0) return;
        var h = Height - 6;
        var widths = _hits.Select(x => Measure(Label(x))).ToList();
        var wMore = Measure("+00");
        var noticeW = _notice == null ? 0 : Measure(_notice);
        var left = 4 + (noticeW > 0 ? noticeW + ChipGap : 0);
        _noticeRect = noticeW > 0 ? new Rectangle(4, 3, noticeW, h) : Rectangle.Empty;
        var r = ChipLayout.Place(widths, wMore, Width - 4, ChipGap, MinBanner + left - 4);
        var placed = new List<Chip>();
        for (var i = 0; i < r.Xs.Count; i++) placed.Add(new Chip(_hits[i], Label(_hits[i]), new Rectangle(r.Xs[i], 3, widths[i], h)));
        _overflow = r.Overflow;
        if (r.Overflow > 0) placed.Add(new Chip(null, "+" + r.Overflow.ToString(CultureInfo.InvariantCulture), new Rectangle(r.OverflowX, 3, wMore, h)));
        _chips = placed;
        _bannerRect = new Rectangle(left, 0, Math.Max(0, r.BannerRight - left), Height);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = LogicalToDeviceUnits(24);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DoLayout();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Control);
        using (var pen = new Pen(SystemColors.ControlDark)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        TextRenderer.DrawText(g, _banner, Font, _bannerRect, SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
        if (_notice != null && !_noticeRect.IsEmpty)
        {
            var hotNotice = ReferenceEquals(_hover, NoticeKey);
            using (var fill = new SolidBrush(hotNotice ? SystemColors.Window : Color.FromArgb(255, 244, 200))) g.FillRectangle(fill, _noticeRect);
            using (var pen = new Pen(SystemColors.ControlDark)) g.DrawRectangle(pen, _noticeRect.X, _noticeRect.Y, _noticeRect.Width - 1, _noticeRect.Height - 1);
            using (var b = new SolidBrush(Color.FromArgb(40, 100, 200))) g.FillRectangle(b, _noticeRect.X + 1, _noticeRect.Y + 1, 4, _noticeRect.Height - 2);
            TextRenderer.DrawText(g, _notice, _bold, new Rectangle(_noticeRect.X + 5 + PadX - 2, _noticeRect.Y, _noticeRect.Width - 5 - PadX + 2, _noticeRect.Height),
                SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        foreach (var c in _chips)
        {
            var hot = ReferenceEquals(_hover, c);
            var accent = c.Hit == null ? SystemColors.ControlDark : Accent(c.Hit.Severity);
            using (var fill = new SolidBrush(hot ? SystemColors.Window : SystemColors.ControlLightLight)) g.FillRectangle(fill, c.Bounds);
            using (var pen = new Pen(SystemColors.ControlDark)) g.DrawRectangle(pen, c.Bounds.X, c.Bounds.Y, c.Bounds.Width - 1, c.Bounds.Height - 1);
            using (var b = new SolidBrush(accent)) g.FillRectangle(b, c.Bounds.X + 1, c.Bounds.Y + 1, 4, c.Bounds.Height - 2);
            TextRenderer.DrawText(g, c.Text, _bold, new Rectangle(c.Bounds.X + 5 + PadX - 2, c.Bounds.Y, c.Bounds.Width - 5 - PadX + 2, c.Bounds.Height),
                SystemColors.ControlText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    object? HitTest(Point p)
    {
        foreach (var c in _chips) if (c.Bounds.Contains(p)) return c;
        if (_notice != null && _noticeRect.Contains(p)) return NoticeKey;
        return _bannerRect.Contains(p) ? this : null;
    }

    string? TipFor(object? o) => o switch
    {
        Chip { Hit: { } h } => AlertEngine.Tooltip(h) + (VisibleOnly ? "\n(" + ModuleSession.VisibleOnlyText + ")" : "")
            + (string.IsNullOrWhiteSpace(h.Notes) ? "" : "\n\n" + h.Notes),
        Chip => string.Join(", ", _hits.Skip(_hits.Count - _overflow).Select(Label)),
        AlertBar => _bannerTip,
        _ when ReferenceEquals(o, NoticeKey) => "Click to open the turn and replay it.",
        _ => null,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var o = HitTest(e.Location);
        if (ReferenceEquals(o, _hover)) return;
        _hover = o;
        Cursor = o != null ? Cursors.Hand : Cursors.Default;
        Invalidate();
        var tip = TipFor(o);
        if (string.IsNullOrEmpty(tip)) _tip.Hide(this);
        else _tip.Show(tip, this, new Point(e.X + 12, Height + 2), 20000);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _tip.Hide(this);
        var o = HitTest(e.Location);
        if (o is Chip { Hit: { } h })
        {
            if (e.Button == MouseButtons.Left) ChipClicked?.Invoke(h);
            else if (e.Button == MouseButtons.Right) ChipContext?.Invoke(h, e.Location);
        }
        else if (o is Chip { Hit: null } && e.Button == MouseButtons.Left) OverflowClicked?.Invoke(_hits.Skip(_hits.Count - _overflow).ToList(), e.Location);
        else if (ReferenceEquals(o, NoticeKey) && e.Button == MouseButtons.Left) NoticeClicked?.Invoke();
        else if (o is AlertBar && e.Button == MouseButtons.Left) BannerClicked?.Invoke();
    }

    public void SaveScreenshot(string path)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _bold.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
