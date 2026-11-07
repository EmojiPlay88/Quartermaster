using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>
/// Borderless, non-activating, mouse-transparent window listing every piece of a stack at 100% with its name and properties.
/// The content is rendered once with Skia into a bitmap.
/// </summary>
sealed class CounterDetailPopup : Form
{
    const int MaxRows = 12;
    const int Pad = 6;
    Bitmap? _image;
    // Ledger value lines: pixel rectangle -> breakdown, shown with the shared tip on hover. Without any, the popup stays click-through.
    readonly List<(Rectangle Rect, Breakdown Tip)> _hot = new();
    readonly BreakdownTip _tip = new();

    /// <summary>True when the popup has ledger lines that can be hovered (it then takes mouse input).</summary>
    public bool Interactive => _hot.Count > 0;
    public event Action? PointerEntered;
    public event Action? PointerLeft;

    CounterDetailPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 && _hot.Count == 0) { m.Result = new IntPtr(-1); return; } // WM_NCHITTEST -> HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_image != null) e.Graphics.DrawImageUnscaled(_image, 0, 0);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        PointerEntered?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide();
        PointerLeft?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HoverAt(e.Location);
    }

    /// <summary>The hover code path: shows the breakdown of the ledger line under <paramref name="p"/> (client pixels), else hides it.</summary>
    void HoverAt(Point p)
    {
        foreach (var (rect, tip) in _hot)
            if (rect.Contains(p)) { _tip.Show(this, new Point(rect.Right + 6, rect.Top), tip); return; }
        _tip.Hide();
    }

    /// <summary>Smoke: hovers the first ledger line through the same path as the mouse. False when there is none.</summary>
    public bool SmokeHoverFirstLine()
    {
        if (_hot.Count == 0) return false;
        HoverAt(new Point(_hot[0].Rect.Left + 2, _hot[0].Rect.Top + 2));
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _image?.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    public void ShowAt(Point screen)
    {
        var area = Screen.FromPoint(screen).WorkingArea;
        var x = Math.Min(screen.X + 16, area.Right - Width);
        var y = Math.Min(screen.Y + 16, area.Bottom - Height);
        Location = new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
        Show();
    }

    public static CounterDetailPopup Create(ModuleSession s, GameState state, StackInstance stack, PieceRenderer pieces, float scale = 1f)
    {
        var rows = new List<(PieceInstance Piece, PieceType Type, PieceDetail Detail, SKSizeI Cell)>();
        foreach (var id in Enumerable.Reverse(stack.PieceIds).Take(MaxRows))
        {
            if (!state.Pieces.TryGetValue(id, out var p)) continue;
            var t = pieces.Types.Get(p.TypeString);
            var size = s.Images.SizeOf(t.Basic.Image);
            rows.Add((p, t, PieceDetails.For(p, t, state, s.Rules), new SKSizeI(Math.Clamp(size.Width, 24, 120), Math.Clamp(size.Height, 24, 120))));
        }
        var extra = stack.PieceIds.Count - rows.Count;

        using var font = new SKFont(SKTypeface.FromFamilyName("Tahoma") ?? SKTypeface.Default, 11);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Tahoma", SKFontStyle.Bold) ?? SKTypeface.Default, 11);
        const float line = 15;
        float width = 120;
        var height = Pad;
        var heights = new List<int>();
        foreach (var r in rows)
        {
            var textH = (int)((1 + r.Detail.Properties.Count + r.Detail.Ledger.Count) * line);
            var h = Math.Max(r.Cell.Height, textH) + Pad;
            heights.Add(h);
            height += h;
            var tw = bold.MeasureText(r.Detail.Name);
            foreach (var (k, v) in r.Detail.Properties) tw = Math.Max(tw, font.MeasureText($"{k}: {v}"));
            foreach (var l in r.Detail.Ledger) tw = Math.Max(tw, font.MeasureText($"{l.Name}: {Num(l.Value)}"));
            width = Math.Max(width, Pad * 2 + r.Cell.Width + Pad + tw);
        }
        if (extra > 0) height += (int)line + Pad;
        width = Math.Min(width, 520);

        var logicalW = (int)Math.Ceiling(width);
        var logicalH = Math.Max(height, 24);
        var hot = new List<(Rectangle, Breakdown)>();
        var bmp = new SKBitmap((int)Math.Ceiling(logicalW * scale), (int)Math.Ceiling(logicalH * scale));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0xFF, 0xFF, 0xE1));
            c.Scale(scale); // layout is in 96-dpi units; render at the monitor's pixel density
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            using var dash = SKPathEffect.CreateDash(new[] { 2f, 2f }, 0);
            using var dotted = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1, PathEffect = dash };
            using var edge = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke };
            var y = (float)Pad;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var cx = Pad + r.Cell.Width / 2f;
                pieces.Draw(c, r.Piece, r.Type, new PointD(cx, y + r.Cell.Height / 2f), scale, PieceDrawFlags.None);
                var tx = Pad * 2 + r.Cell.Width;
                var ty = y + 11;
                c.DrawText(r.Detail.Name, tx, ty, SKTextAlign.Left, bold, ink);
                foreach (var (k, v) in r.Detail.Properties)
                {
                    ty += line;
                    c.DrawText($"{k}: {v}", tx, ty, SKTextAlign.Left, font, ink);
                }
                foreach (var l in r.Detail.Ledger)
                {
                    ty += line;
                    var text = $"{l.Name}: {Num(l.Value)}";
                    c.DrawText(text, tx, ty, SKTextAlign.Left, font, ink);
                    var w = font.MeasureText(text);
                    c.DrawLine(tx, ty + 2, tx + w, ty + 2, dotted); // underline marks "hover for the breakdown"
                    var key = LedgerKey.Piece(r.Piece.Id);
                    hot.Add((new Rectangle((int)(tx * scale), (int)((ty - 11) * scale), (int)Math.Ceiling(w * scale), (int)(line * scale)),
                        LedgerBreakdown.For(state, s.Rules, s.Model, key, l.PropertyId)));
                }
                y += heights[i];
            }
            if (extra > 0) c.DrawText($"+ {extra} more", Pad, y + 11, SKTextAlign.Left, font, ink);
            c.ResetMatrix();
            c.DrawRect(SKRect.Create(0, 0, bmp.Width - 1, bmp.Height - 1), edge);
        }
        var form = new CounterDetailPopup { _image = bmp.ToBitmap() };
        bmp.Dispose();
        form.ClientSize = form._image.Size;
        form._hot.AddRange(hot);
        return form;
    }

    static string Num(double v) => LedgerTable.FormatNumber(v);
}
