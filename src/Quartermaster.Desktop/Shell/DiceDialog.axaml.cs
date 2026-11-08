using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The dice expression prompt, as WinForms DiceDialog: live validation with <see cref="DiceExpr"/> (the error under the box, OK off while
/// the text is not a roll); for Multi-roll it also asks how many times (1 to 20, default 3). Closes with a <see cref="DiceChoice"/>, or
/// null when cancelled.
/// </summary>
public partial class DiceDialog : Window
{
    /// <summary>For the XAML designer only.</summary>
    public DiceDialog() : this("Custom Roll", false) { }

    public DiceDialog(string title, bool multi)
    {
        InitializeComponent();
        Title = title;
        _multi = multi;
        TimesLabel.IsVisible = Times.IsVisible = multi;
        Expr.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Check(); };
        Ok.Click += (_, _) => { if (Result() is { } r) Close(r); };
        Cancel.Click += (_, _) => Close(null);
        Opened += (_, _) => { Expr.Focus(); Expr.SelectAll(); };
        Check();
    }

    readonly bool _multi;

    internal TextBox ExpressionBox => Expr;
    internal NumericUpDown TimesBox => Times;
    internal Button OkButton => Ok;
    internal string ErrorText => Error.Text ?? "";

    /// <summary>What OK returns now: the parsed dice and the times (1 unless Multi-roll), or null when the text is not a roll.</summary>
    internal DiceChoice? Result()
    {
        var (spec, _) = DiceExpr.Parse(Expr.Text);
        return spec == null ? null : new DiceChoice(spec, _multi ? (int)(Times.Value ?? 1) : 1);
    }

    void Check()
    {
        try
        {
            var (spec, err) = DiceExpr.Parse(Expr.Text);
            Error.Text = err ?? "";
            Ok.IsEnabled = spec != null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error.Text = DiceExpr.FormatError;
            Ok.IsEnabled = false;
        }
    }
}
