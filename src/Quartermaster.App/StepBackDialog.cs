using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Modal Step Back dialog. List mode ("Step Back To...") shows the checkpoints newest first with a summary of what stepping back would undo;
/// confirm mode ("Step Back to Start of Phase") shows just the one target and its summary. OK = Step Back, Cancel = leave the game alone.
/// </summary>
sealed class StepBackDialog : Form
{
    readonly IReadOnlyList<CheckpointRow> _rows;
    readonly Func<CheckpointRow, string> _summary;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = false,
    };
    readonly TextBox _box = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Control,
    };

    /// <param name="rows">Checkpoints, newest first. Never empty.</param>
    /// <param name="summary">Plain-words summary of what stepping back to a row would undo.</param>
    /// <param name="confirmOnly">Show only the first row's summary (Step Back to Start of Phase).</param>
    public StepBackDialog(IReadOnlyList<CheckpointRow> rows, Func<CheckpointRow, string> summary, bool confirmOnly)
    {
        _rows = rows;
        _summary = summary;
        Text = confirmOnly ? "Step Back to Start of Phase" : "Step Back To";
        FormBorderStyle = confirmOnly ? FormBorderStyle.FixedDialog : FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(confirmOnly ? 420 : 640, confirmOnly ? 220 : 380);
        MinimumSize = confirmOnly ? Size.Empty : new Size(480, 300);

        var ok = new Button { Text = "Step Back", DialogResult = DialogResult.OK, Width = 90, Height = 26, FlatStyle = FlatStyle.System };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 26, FlatStyle = FlatStyle.System };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var boxHost = new Panel { Dock = DockStyle.Bottom, Height = confirmOnly ? 110 : 100, Padding = new Padding(8, 4, 8, 4) };
        boxHost.Controls.Add(_box);

        if (confirmOnly)
        {
            var r = rows[0];
            var head = new Label
            {
                Dock = DockStyle.Top, Height = 60, Padding = new Padding(8, 8, 8, 0),
                Text = PhaseTexts.StepBackConfirm(r),
            };
            boxHost.Dock = DockStyle.Fill;
            Controls.Add(boxHost);
            Controls.Add(head);
            Controls.Add(buttons);
            _box.Text = summary(r);
        }
        else
        {
            var widths = new[] { 50, 110, 100, 150, 80, 60 };
            for (var i = 0; i < PhaseTexts.StepBackColumns.Count; i++) _list.Columns.Add(PhaseTexts.StepBackColumns[i], widths[i]);
            foreach (var r in rows) _list.Items.Add(new ListViewItem(PhaseTexts.StepBackCells(r).ToArray()));
            // The Phase column takes whatever width the other columns leave, so the list has no blank band on the right.
            void FitPhase()
            {
                var others = _list.Columns.Cast<ColumnHeader>().Where(c => c.Index != 3).Sum(c => c.Width);
                _list.Columns[3].Width = Math.Max(150, _list.ClientSize.Width - others);
            }
            _list.Resize += (_, _) => FitPhase();
            var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 4) };
            listHost.Controls.Add(_list);
            Controls.Add(listHost);
            Controls.Add(boxHost);
            Controls.Add(buttons);
            _list.SelectedIndexChanged += (_, _) => ShowSummary();
            _list.DoubleClick += (_, _) => { if (Selected != null) DialogResult = DialogResult.OK; };
            if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        }
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The checkpoint chosen, or null when nothing is selected.</summary>
    public CheckpointRow? Selected =>
        _rows.Count == 0 ? null : _list.Items.Count == 0 ? _rows[0] : _list.SelectedIndices.Count > 0 ? _rows[_list.SelectedIndices[0]] : null;

    /// <summary>The summary text currently shown under the list.</summary>
    public string SummaryText => _box.Text;

    void ShowSummary() => _box.Text = Selected is { } r ? _summary(r) : "";
}
