using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Fully automatic mode: a run could not finish because some Input variables have no value. Asks once for just those and applies the
/// answers to every unfinished unit of the run. OK applies, "Per unit…" returns Retry (the caller opens the per-unit UI), Cancel leaves the run listed.
/// </summary>
sealed class MissingInputDialog : Form
{
    readonly ModuleSession _s;
    readonly IActionPort _actions;
    readonly PendingRun _run;
    readonly IReadOnlyList<MissingInput> _missing;
    readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    readonly Button _ok = new() { Text = "OK", Width = 75, Height = 25, Enabled = false };
    readonly Button _perUnit = new() { Text = "Per unit…", Width = 85, Height = 25, DialogResult = DialogResult.Retry };
    readonly Button _cancel = new() { Text = "Cancel", Width = 75, Height = 25, DialogResult = DialogResult.Cancel };
    readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = Color.Firebrick, Margin = new Padding(0, 6, 0, 0) };

    public MissingInputDialog(ModuleSession s, IActionPort actions, PendingRun run)
    {
        _s = s;
        _actions = actions;
        _run = run;
        Text = run.FormulaName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var open = run.Units.Count(u => !u.Applied);
        var missing = MissingInputs.For(s, run);
        _missing = missing;
        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(10) };
        root.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 0, 0, 8),
            Text = FormulaWindowRules.MissingHead(run.FormulaName, open, missing.Count),
        });
        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        foreach (var m in missing)
        {
            grid.Controls.Add(new Label { Text = m.Variable, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 8, 3) });
            Control input;
            if (m.Choices != null)
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };   // typing stays allowed
                combo.Items.AddRange(m.Choices.Cast<object>().ToArray());
                combo.TextChanged += (_, _) => Sync();
                input = combo;
            }
            else
            {
                var box = new TextBox { Width = 160 };
                box.TextChanged += (_, _) => Sync();
                input = box;
            }
            _inputs[m.Variable] = input;
            grid.Controls.Add(input);
        }
        root.Controls.Add(grid);
        root.Controls.Add(_message);
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        row.Controls.AddRange(new Control[] { _ok, _perUnit, _cancel });
        root.Controls.Add(row);
        Controls.Add(root);
        _ok.Click += (_, _) => Guarded(Accept);
        AcceptButton = _ok;
        CancelButton = _cancel;
        Sync();
    }

    // WinForms has always enabled OK on non-blank answers only; the engine reports out-of-range values when applying.
    void Sync() => _ok.Enabled = FormulaWindowRules.MissingOkEnabled(_inputs.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.Text, StringComparer.Ordinal), _missing, checkRange: false);

    void Accept()
    {
        var values = _inputs.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim(), StringComparer.Ordinal);
        var (applied, failed) = _actions.ApplyMissingInputs(_run, values);
        if (failed.Count == 0) { DialogResult = DialogResult.OK; return; }
        _message.Text = FormulaWindowRules.MissingFailureLine(applied, failed);
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("missing input dialog failed: " + ex);
            _message.Text = "Could not apply: " + ex.Message;
        }
    }

    /// <summary>Smoke: the variables the dialog asks for, and the text of its OK button state.</summary>
    public IReadOnlyList<string> AskedVariables => _inputs.Keys.ToList();
}
