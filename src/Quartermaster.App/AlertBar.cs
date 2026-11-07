using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// The bar under the toolbar: the "what now" banner on the left, one drawn chip per active alert on the right (severity accent, name, count).
/// Chips that do not fit collapse into a "+N" chip. Pure drawing and hit-testing; the owner supplies the data and reacts to the events.
/// </summary>
sealed class AlertBar : Panel
{
    const int ChipGap = 4, PadX = 7, MinBanner = 140;

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

    public AlertBar()
    {
        Dock = DockStyle.Top;
        Height = 24;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Visible = false;
        Name = "AlertBar";
        _bold = new Font(Font, FontStyle.Bold);
    }

    public event Action? BannerClicked;
    public event Action<AlertHit>? ChipClicked;
    public event Action<AlertHit, Point>? ChipContext;

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
        var x = Width - 4;
        var placed = new List<Chip>();
        foreach (var hit in _hits)
        {
            var text = Label(hit);
            var w = Measure(text);
            if (x - w - ChipGap < MinBanner) break;
            x -= w;
            placed.Add(new Chip(hit, text, new Rectangle(x, 3, w, h)));
            x -= ChipGap;
        }
        if (placed.Count < _hits.Count)
        {
            var wMore = Measure("+00");
            while (placed.Count > 0 && x - wMore < MinBanner) { x = placed[^1].Bounds.Right + ChipGap; placed.RemoveAt(placed.Count - 1); }
            _overflow = _hits.Count - placed.Count;
            x -= wMore;
            placed.Add(new Chip(null, "+" + _overflow.ToString(CultureInfo.InvariantCulture), new Rectangle(x, 3, wMore, h)));
            x -= ChipGap;
        }
        _chips = placed;
        _bannerRect = new Rectangle(4, 0, Math.Max(0, x - 4), Height);
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
        return _bannerRect.Contains(p) ? this : null;
    }

    string? TipFor(object? o) => o switch
    {
        Chip { Hit: { } h } => AlertEngine.Tooltip(h) + (string.IsNullOrWhiteSpace(h.Notes) ? "" : "\n\n" + h.Notes),
        Chip => string.Join(", ", _hits.Skip(_hits.Count - _overflow).Select(Label)),
        AlertBar => _bannerTip,
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
