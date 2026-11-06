using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Lists the module's predefined setups plus "New game from module". <see cref="Selected"/> is null for the latter.</summary>
sealed class SetupChooserDialog : Form
{
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<SetupDef?> _items = new();

    readonly RadioButton[] _radios = new RadioButton[3];
    static readonly (Difficulty D, string Name, string Hint)[] Modes =
    {
        (Difficulty.Hardcore, "Hardcore", "Find every value in the charts and type it in."),
        (Difficulty.SemiAuto, "Semi-automatic", "Type the values in; a cheat sheet shows where each one comes from."),
        (Difficulty.FullyAuto, "Fully automatic", "The game rolls and calculates everything itself and logs it."),
    };

    public SetupDef? Selected { get; private set; }

    /// <summary>The difficulty chosen in the dialog (also written back to <see cref="Settings.LastDifficulty"/> by the caller).</summary>
    public Difficulty SelectedDifficulty { get; private set; }

    /// <param name="preselectNew">Selects "New game from module" instead of the first setup.</param>
    public SetupChooserDialog(ModuleModel model, Difficulty defaultDifficulty = Difficulty.SemiAuto, bool preselectNew = false)
    {
        SelectedDifficulty = defaultDifficulty;
        Text = "Start Game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(420, 390);

        _items.Add(null);
        _list.Items.Add("New game from module");
        foreach (var s in model.Setups)
        {
            _items.Add(s);
            _list.Items.Add(s.NavPath.Count > 0 ? string.Join(" / ", s.NavPath) + " / " + s.Name : s.Name);
        }
        _list.SelectedIndex = model.Setups.Count > 0 && !preselectNew ? 1 : 0;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34, Padding = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        var group = new GroupBox { Text = "Difficulty", Dock = DockStyle.Bottom, Height = 150 };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 6, 2) };
        for (var i = 0; i < Modes.Length; i++)
        {
            var r = new RadioButton { Text = Modes[i].Name, AutoSize = true, Checked = Modes[i].D == defaultDifficulty, Margin = new Padding(3, 4, 3, 0) };
            _radios[i] = r;
            flow.Controls.Add(r);
            flow.Controls.Add(new Label { Text = Modes[i].Hint, AutoSize = false, Width = 380, Height = 17, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 3, 0) });
        }
        group.Controls.Add(flow);
        Controls.Add(_list);
        Controls.Add(group);
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
            for (var i = 0; i < _radios.Length; i++) if (_radios[i].Checked) SelectedDifficulty = Modes[i].D;
        };
    }
}
