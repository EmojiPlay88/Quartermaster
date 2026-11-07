using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The replay bar of a received turn, docked above the log: Next, Play all, Stop, the step counter and the log line just replayed.
/// Play all steps on a timer so each line shows and Stop can pause it. Renders <see cref="TurnReplayModel"/>; owns no game logic.
/// </summary>
sealed class ReplayBar : Panel
{
    const int PlayIntervalMs = 300;

    readonly Button _next = new() { Text = "Next", AutoSize = true, MinimumSize = new Size(70, 24), Name = "replay.next" };
    readonly Button _playAll = new() { Text = "Play all", AutoSize = true, MinimumSize = new Size(70, 24), Name = "replay.playAll" };
    readonly Button _stop = new() { Text = "Stop", AutoSize = true, MinimumSize = new Size(70, 24), Name = "replay.stop" };
    readonly Label _title = new() { AutoSize = true, Font = new Font("Tahoma", 8.25f, FontStyle.Bold), Margin = new Padding(8, 7, 3, 3), UseMnemonic = false };
    readonly Label _step = new() { AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly OneLineLabel _line = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = PlayIntervalMs };
    TurnReplayModel? _model;

    public ReplayBar()
    {
        Dock = DockStyle.Bottom;
        Height = 34;
        Visible = false;
        Name = "ReplayBar";
        BackColor = Color.FromArgb(225, 236, 250);
        foreach (var b in new[] { _next, _playAll, _stop }) { b.BackColor = SystemColors.Control; b.UseVisualStyleBackColor = true; }
        var flow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, Padding = new Padding(4, 3, 4, 3) };
        flow.Controls.AddRange(new Control[] { _title, _next, _playAll, _stop, _step });
        Controls.Add(_line);   // fills what the buttons leave (one line, ellipsis)
        Controls.Add(flow);
        _next.Click += (_, _) => Act(() => { _timer.Stop(); _model?.Next(); });
        _playAll.Click += (_, _) => Act(() => { if (_model is { Done: false }) _timer.Start(); Sync(); });
        _stop.Click += (_, _) => Act(() => { _timer.Stop(); _model?.Stop(); Sync(); });
        _timer.Tick += (_, _) => Act(() =>
        {
            if (_model is not { Done: false } m) { _timer.Stop(); return; }
            m.Next();
            if (m.Done) _timer.Stop();
        });
    }

    /// <summary>A step threw something unexpected (the model reports replay failures itself): the owner shows it.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The model whose turn is shown (null hides the bar).</summary>
    public TurnReplayModel? Model => _model;

    /// <summary>True while Play all is stepping.</summary>
    public bool Playing => _timer.Enabled;

    public string StepText => _step.Text;
    public string LineText => _line.Text;

    public void Bind(TurnReplayModel? model)
    {
        _timer.Stop();
        if (_model != null) _model.Changed -= Sync;
        _model = model;
        if (model != null)
        {
            model.Changed += Sync;
            _title.Text = $"Turn from {model.Turn.FromName}:";
        }
        Visible = model != null;
        Sync();
    }

    void Act(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _timer.Stop();
            Failed?.Invoke(ex);
        }
    }

    void Sync()
    {
        if (IsDisposed) return;
        var m = _model;
        var running = m is { Done: false };
        _next.Enabled = running;
        _playAll.Enabled = running && !_timer.Enabled;
        _stop.Enabled = running && _timer.Enabled;
        _step.Text = m == null ? "" : TurnUiModel.StepText(m.Step, m.Count);
        _line.Text = m?.CurrentLine ?? (m == null ? "" : "Press Next or Play all to replay the turn.");
    }

    /// <summary>One line, cut with an ellipsis (a plain Label wraps instead); the full text is the tooltip.</summary>
    sealed class OneLineLabel : Control
    {
        readonly ToolTip _tip = new();

        public OneLineLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            _tip.SetToolTip(this, Text);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(8, 0, Math.Max(0, Width - 16), Height), ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _title.Font.Dispose(); }
        base.Dispose(disposing);
    }
}
