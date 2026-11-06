using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>The Keyboard page: every command with its shortcut. Edits are held until the dialog is applied; conflicts block OK/Apply.</summary>
sealed class KeyboardTab(CommandRegistry registry) : SettingsTab
{
    readonly Dictionary<string, string> _eff = new();
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = true,
    };
    readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.Firebrick };
    readonly Button _set = new() { Text = "Set...", AutoSize = true };
    readonly Button _clear = new() { Text = "Clear", AutoSize = true };
    readonly Button _reset = new() { Text = "Reset", AutoSize = true };
    readonly Button _resetAll = new() { Text = "Reset all", AutoSize = true };

    public override string Title => "Keyboard";

    /// <summary>The page body.</summary>
    public Panel Host { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };

    public override Control Build()
    {
        if (Host.Controls.Count > 0) return Host;
        _list.Columns.Add("Command", 250);
        _list.Columns.Add("Shortcut", 150);
        _list.Columns.Add("Default", 100);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        bar.Controls.AddRange(new Control[] { _set, _clear, _reset, _resetAll });
        Host.Controls.Add(_list);
        Host.Controls.Add(bar);
        Host.Controls.Add(_status);
        _set.Click += (_, _) => SetSelected();
        _clear.Click += (_, _) => Assign(SelectedId(), "");
        _reset.Click += (_, _) => { if (SelectedId() is { } id && registry.Find(id) is { } d) Assign(id, Normal(d.DefaultShortcut)); };
        _resetAll.Click += (_, _) => { foreach (var d in registry.All) _eff[d.Id] = Normal(d.DefaultShortcut); Refill(); };
        _list.DoubleClick += (_, _) => SetSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        return Host;
    }

    static string Normal(string s) => CommandRegistry.TryNormalize(s, out var n) ? n : "";

    string? SelectedId() => _list.SelectedItems.Count == 1 ? (string?)_list.SelectedItems[0].Tag : null;

    void SetSelected()
    {
        if (SelectedId() is not { } id) return;
        var label = registry.Find(id)?.Label ?? id;
        var r = ShortcutCaptureDialog.Ask(Host.FindForm(), label);
        if (r != null) Assign(id, r);
    }

    void Assign(string? id, string shortcut)
    {
        if (id == null) return;
        _eff[id] = shortcut;
        Refill();
    }

    void UpdateButtons()
    {
        var has = SelectedId() != null;
        _set.Enabled = _clear.Enabled = _reset.Enabled = has;
    }

    /// <summary>Ids that share a key with another command, or hold a reserved key.</summary>
    HashSet<string> Flagged() => registry.Conflicts(_eff).SelectMany(c => c.Ids).ToHashSet();

    void Refill()
    {
        var sel = SelectedId();
        var bad = Flagged();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in registry.All)
        {
            var key = _eff.GetValueOrDefault(d.Id, "");
            var it = new ListViewItem(new[] { d.Label, key.Length == 0 ? "(none)" : key, d.DefaultShortcut }) { Tag = d.Id };
            if (bad.Contains(d.Id)) it.ForeColor = Color.Red;
            if (d.Id == sel) it.Selected = true;
            _list.Items.Add(it);
        }
        _list.EndUpdate();
        _status.Text = Problem() ?? "";
        UpdateButtons();
    }

    string? Problem()
    {
        var cs = registry.Conflicts(_eff);
        if (cs.Count == 0) return null;
        string Names(IReadOnlyList<string> ids) => string.Join(", ", ids.Select(i => registry.Find(i)?.Label ?? i));
        return string.Join("; ", cs.Select(c => CommandRegistry.IsReserved(c.Key)
            ? $"{c.Key} is reserved by the window ({Names(c.Ids)})"
            : $"{c.Key} is used by {Names(c.Ids)}"));
    }

    public override string? Validate() => Problem() is { } p ? "These shortcuts need fixing before the settings can be applied:\n" + p.Replace("; ", "\n") : null;

    public override void Load(Settings s)
    {
        _eff.Clear();
        foreach (var d in registry.All) _eff[d.Id] = registry.EffectiveShortcut(d.Id, s.ShortcutOverrides);
        Refill();
    }

    /// <summary>Writes overrides only for commands that differ from their default; overrides of unknown ids are kept.</summary>
    public override Settings Collect(Settings s)
    {
        var map = s.ShortcutOverrides.Where(kv => registry.Find(kv.Key) == null).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var d in registry.All)
            if (_eff.TryGetValue(d.Id, out var e) && e != Normal(d.DefaultShortcut)) map[d.Id] = e;
        return s with { ShortcutOverrides = map };
    }
}

/// <summary>Small modal that captures the next key press: Esc cancels, Backspace or Delete clears.</summary>
sealed class ShortcutCaptureDialog : Form
{
    public string? Result { get; private set; }

    ShortcutCaptureDialog(string label)
    {
        Text = "Set shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 80);
        Controls.Add(new Label
        {
            Text = $"Press the new shortcut for \"{label}\".\nEsc cancels; Backspace or Delete clears.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: DialogResult = DialogResult.Cancel; return true;
            case Keys.Back or Keys.Delete: Result = ""; DialogResult = DialogResult.OK; return true;
        }
        if (ShortcutParser.Format(keyData) is { } s) { Result = s; DialogResult = DialogResult.OK; }
        return true; // modifiers alone and unsupported keys are swallowed
    }

    /// <summary>Returns the captured shortcut ("" = clear), or null when cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string label)
    {
        using var d = new ShortcutCaptureDialog(label);
        return (owner == null ? d.ShowDialog() : d.ShowDialog(owner)) == DialogResult.OK ? d.Result : null;
    }
}
