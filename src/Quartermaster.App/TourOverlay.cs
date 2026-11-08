using System.Drawing.Drawing2D;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The interface tour balloon: a small borderless form owned by the main window, drawn in the classic look (tooltip yellow, system colours)
/// with a pointer toward the current step's target rectangle. Back / Next / Skip tour; Esc skips. Steps whose target is not on screen are skipped.
/// </summary>
sealed class TourOverlay : Form
{
    enum Side { Below, Above, Right, Left }

    const int BodyW = 330, Arrow = 12, Pad = 10;

    readonly IReadOnlyList<TourStep> _steps;
    readonly Func<string, Rectangle?> _resolve;
    readonly Action<bool> _finished;
    readonly Button _back = new() { Text = "< &Back", FlatStyle = FlatStyle.System, AutoSize = true };
    readonly Button _next = new() { Text = "&Next >", FlatStyle = FlatStyle.System, AutoSize = true };
    readonly Button _skip = new() { Text = "&Skip tour", FlatStyle = FlatStyle.System, AutoSize = true };
    readonly Font _bold;
    int _index;
    Side _side = Side.Below;
    Rectangle _body;
    Point _tip;
    bool _done;

    /// <summary>Steps whose target is on screen right now.</summary>
    public static IReadOnlyList<TourStep> VisibleSteps(Func<string, Rectangle?> resolve) => TourSteps.Visible(id => resolve(id) is { Width: > 0, Height: > 0 });

    public TourOverlay(IReadOnlyList<TourStep> steps, Func<string, Rectangle?> resolve, Action<bool> finished)
    {
        _steps = steps;
        _resolve = resolve;
        _finished = finished;
        _bold = new Font(Font, FontStyle.Bold);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = SystemColors.Info;
        ForeColor = SystemColors.InfoText;
        Name = "TourOverlay";
        Controls.AddRange(new Control[] { _back, _next, _skip });
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => { if (_index >= _steps.Count - 1) Finish(false); else Go(_index + 1); };
        _skip.Click += (_, _) => Finish(true);
        CancelButton = _skip;
        AcceptButton = _next;
    }

    public int Index => _index;
    public int Count => _steps.Count;
    public string CurrentTitle => _steps.Count > 0 ? _steps[_index].Title : "";
    public bool IsDone => _done;

    /// <summary>Shows step <paramref name="index"/> (clamped); steps without a visible target are passed over in the direction of travel.</summary>
    public void Go(int index)
    {
        if (_steps.Count == 0) { Finish(false); return; }
        var dir = index >= _index ? 1 : -1;
        index = Math.Clamp(index, 0, _steps.Count - 1);
        while (index >= 0 && index < _steps.Count && _resolve(_steps[index].TargetId) is not { Width: > 0, Height: > 0 }) index += dir;
        if (index < 0 || index >= _steps.Count) { if (dir > 0) Finish(false); return; }
        _index = index;
        Relayout();
        Invalidate();
    }

    void Relayout()
    {
        var step = _steps[_index];
        var target = _resolve(step.TargetId)!.Value;
        // Off-screen screenshot runs: the windows are far from every screen, so the owner window stands in for the screen (else the balloon is clamped to a real monitor).
        var wa = OffscreenShots.Active && Owner is { } owner ? owner.Bounds : Screen.FromRectangle(target).WorkingArea;
        var bodyW = LogicalToDeviceUnits(BodyW);
        var arrow = LogicalToDeviceUnits(Arrow);
        var pad = LogicalToDeviceUnits(Pad);

        var textH = TextRenderer.MeasureText(step.Text, Font, new Size(bodyW - 2 * pad, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        var titleH = TextRenderer.MeasureText(step.Title, _bold).Height;
        foreach (var b in new[] { _back, _next, _skip }) b.PerformLayout();
        var btnH = Math.Max(_next.Height, _next.PreferredSize.Height);
        var bodyH = pad + titleH + pad / 2 + textH + pad + btnH + pad;

        // Wide-and-short targets get the balloon under them; tall ones get it beside them. Fall back when it would leave the screen.
        var prefer = target.Width >= target.Height * 2 ? new[] { Side.Below, Side.Above, Side.Right, Side.Left } : new[] { Side.Right, Side.Left, Side.Below, Side.Above };
        _side = prefer[0];
        foreach (var s in prefer)
        {
            var need = (s is Side.Below or Side.Above) ? bodyH + arrow : bodyW + arrow;
            var room = s switch { Side.Below => wa.Bottom - target.Bottom, Side.Above => target.Top - wa.Top, Side.Right => wa.Right - target.Right, _ => target.Left - wa.Left };
            if (room >= need) { _side = s; break; }
        }

        // Pointer tip: nearest edge of the target, centred on it (clamped into the balloon span below).
        var cx = Math.Clamp(target.Left + Math.Min(target.Width / 2, LogicalToDeviceUnits(160)), wa.Left + pad, wa.Right - pad);
        var cy = Math.Clamp(target.Top + Math.Min(target.Height / 2, LogicalToDeviceUnits(120)), wa.Top + pad, wa.Bottom - pad);
        Point tip; int bx, by;
        switch (_side)
        {
            case Side.Below: tip = new Point(cx, target.Bottom); bx = tip.X - bodyW / 4; by = tip.Y + arrow; break;
            case Side.Above: tip = new Point(cx, target.Top); bx = tip.X - bodyW / 4; by = tip.Y - arrow - bodyH; break;
            case Side.Right: tip = new Point(target.Right, cy); bx = tip.X + arrow; by = tip.Y - bodyH / 4; break;
            default: tip = new Point(target.Left, cy); bx = tip.X - arrow - bodyW; by = tip.Y - bodyH / 4; break;
        }
        bx = Math.Clamp(bx, wa.Left, Math.Max(wa.Left, wa.Right - bodyW));
        by = Math.Clamp(by, wa.Top, Math.Max(wa.Top, wa.Bottom - bodyH));

        // Form = body plus the arrow strip on the target side; the region cuts it to body + triangle.
        Rectangle form;
        switch (_side)
        {
            case Side.Below: form = new Rectangle(bx, by - arrow, bodyW, bodyH + arrow); _body = new Rectangle(0, arrow, bodyW, bodyH); break;
            case Side.Above: form = new Rectangle(bx, by, bodyW, bodyH + arrow); _body = new Rectangle(0, 0, bodyW, bodyH); break;
            case Side.Right: form = new Rectangle(bx - arrow, by, bodyW + arrow, bodyH); _body = new Rectangle(arrow, 0, bodyW, bodyH); break;
            default: form = new Rectangle(bx, by, bodyW + arrow, bodyH); _body = new Rectangle(0, 0, bodyW, bodyH); break;
        }
        _tip = new Point(tip.X - form.X, tip.Y - form.Y);
        Bounds = form;

        using var path = new GraphicsPath();
        path.AddRectangle(_body);
        path.AddPolygon(ArrowPoints(arrow));
        Region?.Dispose();
        Region = new Region(path);

        var by0 = _body.Bottom - pad - btnH;
        _skip.Location = new Point(_body.Left + pad, by0);
        _next.Location = new Point(_body.Right - pad - _next.Width, by0);
        _back.Location = new Point(_next.Left - 6 - _back.Width, by0);
        _back.Enabled = _index > 0;
        _next.Text = _index >= _steps.Count - 1 ? "&Done" : "&Next >";
        _textRect = new Rectangle(_body.Left + pad, _body.Top + pad + titleH + pad / 2, bodyW - 2 * pad, textH);
        _titleRect = new Rectangle(_body.Left + pad, _body.Top + pad, bodyW - 2 * pad, titleH);
    }

    Rectangle _textRect, _titleRect;

    Point[] ArrowPoints(int arrow)
    {
        // Base of the triangle sits on the body edge, near the span the tip points into.
        const int half = 8;
        var h = LogicalToDeviceUnits(half);
        return _side switch
        {
            Side.Below => new[] { _tip, new Point(Math.Clamp(_tip.X, _body.Left + h + 4, _body.Right - h - 4) - h, _body.Top), new Point(Math.Clamp(_tip.X, _body.Left + h + 4, _body.Right - h - 4) + h, _body.Top) },
            Side.Above => new[] { _tip, new Point(Math.Clamp(_tip.X, _body.Left + h + 4, _body.Right - h - 4) - h, _body.Bottom), new Point(Math.Clamp(_tip.X, _body.Left + h + 4, _body.Right - h - 4) + h, _body.Bottom) },
            Side.Right => new[] { _tip, new Point(_body.Left, Math.Clamp(_tip.Y, _body.Top + h + 4, _body.Bottom - h - 4) - h), new Point(_body.Left, Math.Clamp(_tip.Y, _body.Top + h + 4, _body.Bottom - h - 4) + h) },
            _ => new[] { _tip, new Point(_body.Right, Math.Clamp(_tip.Y, _body.Top + h + 4, _body.Bottom - h - 4) - h), new Point(_body.Right, Math.Clamp(_tip.Y, _body.Top + h + 4, _body.Bottom - h - 4) + h) },
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_steps.Count == 0) return;
        var g = e.Graphics;
        var step = _steps[_index];
        using var fill = new SolidBrush(SystemColors.Info);
        using var pen = new Pen(SystemColors.InfoText);
        var arrow = ArrowPoints(LogicalToDeviceUnits(Arrow));
        g.FillRectangle(fill, _body);
        g.FillPolygon(fill, arrow);
        g.DrawRectangle(pen, new Rectangle(_body.Left, _body.Top, _body.Width - 1, _body.Height - 1));
        // Arrow outline: the two slanted edges only, then a gap-closing fill over the body border where the triangle joins.
        g.DrawLine(pen, arrow[0], arrow[1]);
        g.DrawLine(pen, arrow[0], arrow[2]);
        using var join = new Pen(SystemColors.Info);
        if (_side is Side.Below or Side.Above)
        {
            var y = _side == Side.Below ? _body.Top : _body.Bottom - 1;
            g.DrawLine(join, arrow[1].X + 1, y, arrow[2].X - 1, y);
        }
        else
        {
            var x = _side == Side.Right ? _body.Left : _body.Right - 1;
            g.DrawLine(join, x, arrow[1].Y + 1, x, arrow[2].Y - 1);
        }
        TextRenderer.DrawText(g, $"{step.Title}  ({_index + 1} of {_steps.Count})", _bold, _titleRect, SystemColors.InfoText, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, step.Text, Font, _textRect, SystemColors.InfoText, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Finish(true); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Finish(bool skipped)
    {
        if (_done) return;
        _done = true;
        _finished(skipped);
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_done) { _done = true; _finished(true); } // closed from outside (owner closing): counts as skipped
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold.Dispose();
        base.Dispose(disposing);
    }
}
