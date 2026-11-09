using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Formulas;

/// <summary>
/// The "layout C" formula body, as WinForms FormulaFormView: text with an input box (and caption) in place of each variable, sub-rows, and the
/// result box. A box with choices becomes an editable drop-down; a rolled box shows "↻" (tooltip "Roll again") to roll it again.
/// </summary>
sealed class FormulaFormView : StackPanel
{
    static readonly IBrush AutoColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xE1));
    static readonly IBrush WrongColor = new SolidColorBrush(Colors.MistyRose);
    const double CaptionSize = 9.5;   // WinForms Tahoma 7pt

    sealed class Slot(TextBox box, StackPanel host, TextBlock caption, Button reroll, string baseCaption)
    {
        public TextBox Box = box;
        public ComboBox? Combo;
        public readonly StackPanel Host = host;
        public readonly TextBlock Caption = caption;
        public readonly Button Reroll = reroll;
        public readonly string BaseCaption = baseCaption;
        public Control Input => Combo ?? (Control)Box;
        public string Text => Combo != null ? Combo.Text ?? "" : Box.Text ?? "";
    }

    readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TextBlock>> _mirrors = new(StringComparer.Ordinal);
    readonly List<Control> _plain = new();
    readonly Action<string, string> _onEdit;
    readonly Action<string> _onReroll;
    bool _updating;

    public TextBox ResultBox { get; } = new()
    {
        IsReadOnly = true, TextAlignment = TextAlignment.Right, Width = 80, FontWeight = FontWeight.Bold, Margin = new Thickness(2, 0, 2, 0),
        IsTabStop = false, VerticalAlignment = VerticalAlignment.Top,
    };

    public FormulaFormView(FormulaLayoutModel layout, Action<string, string> onEdit, Action<string> onReroll)
    {
        _onEdit = onEdit;
        _onReroll = onReroll;
        Orientation = Orientation.Vertical;
        MinWidth = 540;

        var main = Row(layout.Main.Concat(new LayoutItem[] { new TextItem(" =") }));
        main.Children.Add(ResultBox);
        Children.Add(main);
        foreach (var sub in layout.SubRows)
        {
            var row = Row(new LayoutItem[] { new TextItem(sub.Label + "  →  ") }.Concat(sub.Items));
            row.Margin = new Thickness(16, 6, 0, 0);
            Children.Add(row);
        }
    }

    WrapPanel Row(IEnumerable<LayoutItem> items)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 560, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var item in items)
        {
            switch (item)
            {
                case TextItem t:
                    var text = new TextBlock { Text = t.Text, Margin = new Thickness(0, 5, 0, 0) };
                    _plain.Add(text);
                    row.Children.Add(text);
                    break;
                case BoxItem b:
                    row.Children.Add(MakeBox(b));
                    break;
                case MirrorItem m:
                    var l = new TextBlock { Padding = new Thickness(2, 0, 2, 0) };
                    if (!_mirrors.TryGetValue(m.Variable, out var list)) _mirrors[m.Variable] = list = new();
                    list.Add(l);
                    var frame = new Border { Child = l, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                    frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Classic.BorderBrush"));
                    row.Children.Add(frame);
                    break;
            }
        }
        _plain.Add(row);
        return row;
    }

    /// <summary>The view and its plain parts (rows, static text) that carry no tooltip of their own: the formula's notes go on these.</summary>
    public IEnumerable<Control> NoteTargets() => new Control[] { this }.Concat(_plain);

    Control MakeBox(BoxItem b)
    {
        var tb = new TextBox { Width = 64, Margin = new Thickness(2, 0, 0, 0), TextAlignment = TextAlignment.Right };
        var cap = new TextBlock { Text = b.Caption, FontSize = CaptionSize, Margin = new Thickness(2, 0, 0, 0) };
        cap.Bind(TextBlock.ForegroundProperty, cap.GetResourceObservable("Classic.GrayTextBrush"));
        var re = new Button { Content = "↻", Width = 22, Padding = new Thickness(0), Margin = new Thickness(0), IsVisible = false, IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(re, "Roll again");
        var name = b.Variable;
        tb.TextChanged += (_, _) => { if (!_updating) _onEdit(name, tb.Text ?? ""); };
        re.Click += (_, _) => _onReroll(name);
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(tb);
        top.Children.Add(re);
        var col = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(1, 0, 3, 0) };
        col.Children.Add(top);
        col.Children.Add(cap);
        if (!_slots.ContainsKey(name)) _slots[name] = new Slot(tb, top, cap, re, b.Caption);
        return col;
    }

    /// <summary>Types into a box as the user would (raises the edit event).</summary>
    public void Type(string variable, string text)
    {
        if (!_slots.TryGetValue(variable, out var s)) return;
        if (s.Combo != null) s.Combo.Text = text;
        else s.Box.Text = text;
    }

    /// <summary>The box's text as shown (null when there is no such box).</summary>
    public string? BoxText(string variable) => _slots.TryGetValue(variable, out var s) ? s.Text : null;

    /// <summary>True when the box is drawn as a wrong value.</summary>
    public bool BoxWrong(string variable) => _slots.TryGetValue(variable, out var s) && ReferenceEquals(BackOf(s), WrongColor);

    static IBrush? BackOf(Slot s) => s.Combo?.Background ?? s.Box.Background;

    /// <summary>Turns the slot's text box into an editable drop-down of the choices (typing is still allowed), or refreshes the list.</summary>
    void UseCombo(Slot s, string name, IReadOnlyList<string> choices)
    {
        if (s.Combo is not { } combo)
        {
            combo = new ComboBox { IsEditable = true, Width = 80, Margin = s.Box.Margin, Text = s.Box.Text };
            combo.PropertyChanged += (_, e) =>
            {
                if (e.Property == ComboBox.TextProperty && !_updating) _onEdit(name, combo.Text ?? "");
            };
            var at = s.Host.Children.IndexOf(s.Box);
            s.Host.Children.RemoveAt(at);
            s.Host.Children.Insert(at, combo);
            s.Combo = combo;
        }
        if (!combo.Items.Cast<object?>().Select(o => o?.ToString()).SequenceEqual(choices))
        {
            combo.Items.Clear();
            foreach (var c in choices) combo.Items.Add(c);
        }
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
                    if (b.Choices != null) UseCombo(s, b.Variable, b.Choices);
                    if (s.Text != b.Text)
                    {
                        if (s.Combo != null) s.Combo.Text = b.Text;
                        else s.Box.Text = b.Text;
                    }
                    IBrush? back = b.Wrong ? WrongColor : b.Background == BoxBackground.Auto ? AutoColor : null;
                    if (s.Combo != null) { if (back != null) s.Combo.Background = back; else s.Combo.ClearValue(TemplatedControl.BackgroundProperty); }
                    else { if (back != null) s.Box.Background = back; else s.Box.ClearValue(TemplatedControl.BackgroundProperty); }
                    s.Input.IsEnabled = b.Enabled;
                    s.Caption.Text = s.BaseCaption + b.CaptionSuffix;
                    s.Caption.FontStyle = b.CaptionSuffix.Length > 0 ? FontStyle.Italic : FontStyle.Normal;
                    s.Reroll.IsVisible = b.RerollKey != null;
                    ToolTip.SetTip(s.Input, string.IsNullOrEmpty(b.Tooltip) ? null : b.Tooltip);
                }
                if (_mirrors.TryGetValue(b.Variable, out var ms))
                    foreach (var m in ms) m.Text = b.Text.Length > 0 ? b.Text : "?";
            }
            ResultBox.Text = v.ResultText;
        }
        finally { _updating = false; }
    }
}
