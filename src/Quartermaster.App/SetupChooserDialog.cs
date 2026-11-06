using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Lists the module's predefined setups plus "New game from module". <see cref="Selected"/> is null for the latter.</summary>
sealed class SetupChooserDialog : Form
{
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<SetupDef?> _items = new();

    public SetupDef? Selected { get; private set; }

    public SetupChooserDialog(ModuleModel model)
    {
        Text = "Start Game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(360, 260);

        _items.Add(null);
        _list.Items.Add("New game from module");
        foreach (var s in model.Setups)
        {
            _items.Add(s);
            _list.Items.Add(s.NavPath.Count > 0 ? string.Join(" / ", s.NavPath) + " / " + s.Name : s.Name);
        }
        _list.SelectedIndex = model.Setups.Count > 0 ? 1 : 0;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34, Padding = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(_list);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK;
        };
        FormClosing += (_, _) =>
        {
            Selected = _list.SelectedIndex >= 0 ? _items[_list.SelectedIndex] : null;
        };
    }
}
