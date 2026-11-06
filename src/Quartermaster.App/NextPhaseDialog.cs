using Quartermaster.Core;

namespace Quartermaster.App;

/// <summary>
/// Warn-only dialog shown by Next Phase when decisions are open. OK = Continue anyway, Cancel = Go back.
/// Double-clicking a row also goes back and sets <see cref="JumpTo"/> so the caller can take the user to that decision.
/// </summary>
sealed class NextPhaseDialog : Form
{
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly IReadOnlyList<Decision> _decisions;

    public NextPhaseDialog(IReadOnlyList<Decision> decisions)
    {
        _decisions = decisions;
        Text = "Next Phase";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 260);

        var n = decisions.Count;
        var head = new Label
        {
            Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 8, 8, 0),
            Text = n == 1 ? "1 decision is still open in this phase:" : $"{n} decisions are still open in this phase:",
        };
        foreach (var d in decisions) _list.Items.Add(d.Text);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        listHost.Controls.Add(_list);

        var cont = new Button { Text = "Continue anyway", DialogResult = DialogResult.OK, Width = 120, Height = 26, FlatStyle = FlatStyle.System };
        var back = new Button { Text = "Go back", DialogResult = DialogResult.Cancel, Width = 90, Height = 26, FlatStyle = FlatStyle.System };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 0) };
        buttons.Controls.Add(back);
        buttons.Controls.Add(cont);

        Controls.Add(listHost);
        Controls.Add(head);
        Controls.Add(buttons);
        AcceptButton = back; // the safe default: Enter goes back
        CancelButton = back;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _decisions.Count) return;
            JumpTo = _decisions[_list.SelectedIndex];
            DialogResult = DialogResult.Cancel;
        };
    }

    /// <summary>The decision the user double-clicked (the result is then Cancel), else null.</summary>
    public Decision? JumpTo { get; private set; }
}
