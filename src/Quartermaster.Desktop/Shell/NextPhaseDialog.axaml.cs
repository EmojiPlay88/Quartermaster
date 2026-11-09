using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Warn-only dialog shown by Next Phase when decisions are open (WinForms NextPhaseDialog). Closes with true = Continue anyway, false = Go back.
/// Double-clicking a row also goes back and sets <see cref="JumpTo"/> so the caller can take the user to that decision.
/// </summary>
public partial class NextPhaseDialog : Window
{
    readonly IReadOnlyList<Decision> _decisions;

    /// <summary>For the XAML designer only.</summary>
    public NextPhaseDialog() : this(Array.Empty<Decision>()) { }

    public NextPhaseDialog(IReadOnlyList<Decision> decisions)
    {
        _decisions = decisions;
        InitializeComponent();
        Head.Text = PhaseTexts.NextPhaseHead(decisions.Count);
        List.ItemsSource = decisions.Select(d => d.Text).ToList();
        Continue.Click += (_, _) => Close(true);
        Back.Click += (_, _) => Close(false);
        List.DoubleTapped += (_, _) => { if (ActivateRow(List.SelectedIndex)) Close(false); };
        Opened += (_, _) => Back.Focus();   // the safe default: Enter goes back
    }

    /// <summary>The decision the user double-clicked (the result is then Go back), else null.</summary>
    public Decision? JumpTo { get; private set; }

    /// <summary>Picks the row to jump to (the double-click); false when the index is out of range.</summary>
    internal bool ActivateRow(int index)
    {
        if (index < 0 || index >= _decisions.Count) return false;
        JumpTo = _decisions[index];
        return true;
    }

    internal string HeadText => Head.Text ?? "";
    internal IReadOnlyList<string> ItemTexts => List.Items.OfType<string>().ToList();
    internal IReadOnlyList<string> ButtonTexts => new[] { Continue, Back }.Select(b => b.Content as string ?? "").ToList();
    internal string? DefaultButtonText => new[] { Continue, Back }.FirstOrDefault(b => b.IsDefault)?.Content as string;
    internal string? CancelButtonText => new[] { Continue, Back }.FirstOrDefault(b => b.IsCancel)?.Content as string;
}
