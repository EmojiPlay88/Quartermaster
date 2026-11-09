using Avalonia.Controls;
using Quartermaster.Core;

namespace Quartermaster.Desktop.Shell;

/// <summary>Set turn number (at least 1) and an optional phase, as WinForms TurnDialog. Closes with a <see cref="TurnChoice"/>, or null when cancelled.</summary>
public partial class TurnDialog : Window
{
    /// <summary>For the XAML designer only.</summary>
    public TurnDialog() : this(new TurnState(1, null)) { }

    public TurnDialog(TurnState current)
    {
        InitializeComponent();
        TurnBox.Value = Math.Clamp(current.Turn, 1, 99999);
        PhaseBox.Text = current.Phase ?? "";
        Ok.Click += (_, _) => Close(Result());
        Cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => TurnBox.Focus();
    }

    internal NumericUpDown TurnNumberBox => TurnBox;
    internal TextBox PhaseTextBox => PhaseBox;

    /// <summary>What OK returns now (the phase as typed; the caller trims it).</summary>
    internal TurnChoice Result() => new((int)Math.Clamp(TurnBox.Value ?? 1, 1, 99999), PhaseBox.Text ?? "");
}
