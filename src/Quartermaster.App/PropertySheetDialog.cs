namespace Quartermaster.App;

/// <summary>Lists a piece property sheet's rows as editable text boxes. OK returns the edited values in row order.</summary>
sealed class PropertySheetDialog : Form
{
    readonly List<TextBox> _boxes = new();

    public PropertySheetDialog(string title, IReadOnlyList<(string Name, string Value)> rows,
        Func<string, string?>? managedValue = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Padding = new Padding(8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        foreach (var (name, value) in rows)
        {
            var multi = value.Contains('\n');
            var box = new TextBox { Text = value.Replace("\n", "\r\n"), Width = 250, Multiline = multi, Height = multi ? 60 : 21, AcceptsReturn = multi };
            _boxes.Add(box);
            var managed = managedValue?.Invoke(name);
            if (managed != null) box.ReadOnly = true; // the ledger owns this value; the text stays as it was so OK writes it back unchanged
            grid.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            grid.Controls.Add(box);
            grid.Controls.Add(new Label { Text = managed == null ? "" : $"(managed by ledger: {managed})", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 6, 0, 0) });
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 36, Padding = new Padding(8, 4, 8, 4) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    public IReadOnlyList<string> Values => _boxes.Select(b => b.Text).ToList();
}

/// <summary>Small modal "prompt" dialog for actions that ask for a value.</summary>
sealed class InputDialog : Form
{
    readonly TextBox _box = new() { Dock = DockStyle.Top };

    InputDialog(string title, string prompt, string initial)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 96);
        _box.Text = initial;
        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 32 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 150, Top = 62, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 232, Top = 62, Width = 75 };
        Padding = new Padding(8);
        Controls.Add(ok);
        Controls.Add(cancel);
        Controls.Add(_box);
        Controls.Add(label);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The entered text, or null when cancelled.</summary>
    public static string? Ask(IWin32Window owner, string title, string prompt, string initial = "")
    {
        using var d = new InputDialog(title, prompt, initial);
        return d.ShowDialog(owner) == DialogResult.OK ? d._box.Text : null;
    }
}
