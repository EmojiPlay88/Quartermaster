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
        if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // WM_NCHITTEST -> HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_image != null) e.Graphics.DrawImageUnscaled(_image, 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
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
            rows.Add((p, t, PieceDetails.For(p, t), new SKSizeI(Math.Clamp(size.Width, 24, 120), Math.Clamp(size.Height, 24, 120))));
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
            var textH = (int)((1 + r.Detail.Properties.Count) * line);
            var h = Math.Max(r.Cell.Height, textH) + Pad;
            heights.Add(h);
            height += h;
            var tw = bold.MeasureText(r.Detail.Name);
            foreach (var (k, v) in r.Detail.Properties) tw = Math.Max(tw, font.MeasureText($"{k}: {v}"));
            width = Math.Max(width, Pad * 2 + r.Cell.Width + Pad + tw);
        }
        if (extra > 0) height += (int)line + Pad;
        width = Math.Min(width, 520);

        var logicalW = (int)Math.Ceiling(width);
        var logicalH = Math.Max(height, 24);
        var bmp = new SKBitmap((int)Math.Ceiling(logicalW * scale), (int)Math.Ceiling(logicalH * scale));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0xFF, 0xFF, 0xE1));
            c.Scale(scale); // layout is in 96-dpi units; render at the monitor's pixel density
            using var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true };
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
                y += heights[i];
            }
            if (extra > 0) c.DrawText($"+ {extra} more", Pad, y + 11, SKTextAlign.Left, font, ink);
            c.ResetMatrix();
            c.DrawRect(SKRect.Create(0, 0, bmp.Width - 1, bmp.Height - 1), edge);
        }
        var form = new CounterDetailPopup { _image = bmp.ToBitmap() };
        bmp.Dispose();
        form.ClientSize = form._image.Size;
        return form;
    }
}
