using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>The "layout C" formula body: text with an input box (and caption) in place of each variable, sub-rows, and the result box.</summary>
sealed class FormulaFormView : FlowLayoutPanel
{
    static readonly Color AutoColor = Color.FromArgb(0xFF, 0xFF, 0xE1);
    static readonly Font CaptionFont = new("Tahoma", 7f);
    static readonly Font CaptionItalic = new("Tahoma", 7f, FontStyle.Italic);

    sealed record Slot(TextBox Box, Label Caption, Button Reroll, string BaseCaption);

    readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Label>> _mirrors = new(StringComparer.Ordinal);
    readonly ToolTip _tip = new();
    readonly Action<string, string> _onEdit;
    readonly Action<string> _onReroll;
    bool _updating;

    public TextBox ResultBox { get; } = new()
    {
        ReadOnly = true, TextAlign = HorizontalAlignment.Right, Width = 80, BackColor = SystemColors.Window,
        Font = new Font("Tahoma", 8.25f, FontStyle.Bold), Margin = new Padding(2, 0, 2, 0), TabStop = false,
    };

    public FormulaFormView(FormulaLayoutModel layout, Action<string, string> onEdit, Action<string> onReroll)
    {
        _onEdit = onEdit;
        _onReroll = onReroll;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;

        var main = Row(layout.Main.Concat(new LayoutItem[] { new TextItem(" =") }));
        main.Controls.Add(ResultBox);
        Controls.Add(main);
        foreach (var sub in layout.SubRows)
        {
            var row = Row(new LayoutItem[] { new TextItem(sub.Label + "  →  ") }.Concat(sub.Items));
            row.Margin = new Padding(16, 6, 0, 0);
            Controls.Add(row);
        }
    }

    FlowLayoutPanel Row(IEnumerable<LayoutItem> items)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            MaximumSize = new Size(560, 0), Margin = new Padding(0, 4, 0, 0),
        };
        foreach (var item in items)
        {
            switch (item)
            {
                case TextItem t:
                    row.Controls.Add(new Label { Text = t.Text, AutoSize = true, Margin = new Padding(0, 5, 0, 0) });
                    break;
                case BoxItem b:
                    row.Controls.Add(MakeBox(b));
                    break;
                case MirrorItem m:
                    var l = new Label { AutoSize = true, Margin = new Padding(0, 5, 0, 0), BorderStyle = BorderStyle.Fixed3D, Padding = new Padding(2, 0, 2, 0) };
                    if (!_mirrors.TryGetValue(m.Variable, out var list)) _mirrors[m.Variable] = list = new();
                    list.Add(l);
                    row.Controls.Add(l);
                    break;
            }
        }
        return row;
    }

    Control MakeBox(BoxItem b)
    {
        var tb = new TextBox { Width = 64, Margin = new Padding(2, 0, 0, 0), TextAlign = HorizontalAlignment.Right };
        var cap = new Label { Text = b.Caption, AutoSize = true, Font = CaptionFont, ForeColor = SystemColors.GrayText, Margin = new Padding(2, 0, 0, 0) };
        var re = new Button { Text = "↻", Width = 22, Height = 21, Margin = new Padding(0), Visible = false, FlatStyle = FlatStyle.Standard, TabStop = false };
        _tip.SetToolTip(re, "Roll again");
        var name = b.Variable;
        tb.TextChanged += (_, _) => { if (!_updating) _onEdit(name, tb.Text); };
        re.Click += (_, _) => _onReroll(name);
        var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        top.Controls.Add(tb);
        top.Controls.Add(re);
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(1, 0, 3, 0) };
        col.Controls.Add(top);
        col.Controls.Add(cap);
        if (!_slots.ContainsKey(name)) _slots[name] = new Slot(tb, cap, re, b.Caption);
        return col;
    }

    /// <summary>Types into a box as the user would (raises the edit event). Used by smoke runs.</summary>
    public void Type(string variable, string text)
    {
        if (_slots.TryGetValue(variable, out var s)) s.Box.Text = text;
    }

    /// <summary>Pushes a run's view onto the controls without raising edit events.</summary>
    public void Apply(DialogView v)
    {
        _updating = true;
        try
        {
            foreach (var b in v.Boxes)
            {
                if (_slots.TryGetValue(b.Variable, out var s))
                {
                    if (s.Box.Text != b.Text) s.Box.Text = b.Text;
                    s.Box.BackColor = b.Background == BoxBackground.Auto ? AutoColor : SystemColors.Window;
                    s.Box.Enabled = b.Enabled;
                    s.Caption.Text = s.BaseCaption + b.CaptionSuffix;
                    s.Caption.Font = b.CaptionSuffix.Length > 0 ? CaptionItalic : CaptionFont;
                    s.Reroll.Visible = b.RerollKey != null;
                    _tip.SetToolTip(s.Box, b.Tooltip ?? "");
                }
                if (_mirrors.TryGetValue(b.Variable, out var ms))
                    foreach (var m in ms) m.Text = b.Text.Length > 0 ? b.Text : "?";
            }
            ResultBox.Text = v.ResultText;
        }
        finally { _updating = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
