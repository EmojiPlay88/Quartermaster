using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Fully automatic mode (WinForms MissingInputDialog): a run could not finish because some Input variables have no value. Asks once for just
/// those and applies the answers to every unfinished unit of the run. OK applies, "Per unit…" closes with PerUnit (the caller opens the
/// per-unit UI), Cancel leaves the run listed. Closes with <see cref="MissingInputResult"/> (closing the window counts as Cancel).
/// </summary>
sealed class MissingInputDialog : Window
{
    readonly IActionPort _actions;
    readonly PendingRun _run;
    readonly IReadOnlyList<MissingInput> _missing;
    readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    readonly Button _ok = new() { Content = "OK", MinWidth = 75, IsEnabled = false, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button _perUnit = new() { Content = "Per unit…", MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button _cancel = new() { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBlock _message = new() { MaxWidth = 380, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };

    public MissingInputDialog(ModuleSession s, IActionPort actions, PendingRun run)
    {
        _actions = actions;
        _run = run;
        Title = run.FormulaName;
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var open = run.Units.Count(u => !u.Applied);
        var missing = MissingInputs.For(s, run);
        _missing = missing;
        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(new TextBlock
        {
            MaxWidth = 380, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = FormulaWindowRules.MissingHead(run.FormulaName, open, missing.Count),
        });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto") };
        var r = 0;
        foreach (var m in missing)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = m.Variable, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            Grid.SetRow(label, r);
            grid.Children.Add(label);
            Control input;
            if (m.Choices != null)
            {
                var combo = new ComboBox { IsEditable = true, Width = 160, ItemsSource = m.Choices.ToList() };   // typing stays allowed
                combo.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty || e.Property == ComboBox.SelectedItemProperty) Sync(); };
                input = combo;
            }
            else
            {
                var box = new TextBox { Width = 160 };
                box.TextChanged += (_, _) => Sync();
                input = box;
            }
            Grid.SetRow(input, r);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            _inputs[m.Variable] = input;
            r++;
        }
        root.Children.Add(grid);
        root.Children.Add(_message);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(_ok);
        row.Children.Add(_perUnit);
        row.Children.Add(_cancel);
        root.Children.Add(row);
        Content = root;

        _ok.Click += (_, _) => Guarded(Accept);
        _perUnit.Click += (_, _) => Close(MissingInputResult.PerUnit);
        _cancel.Click += (_, _) => Close(MissingInputResult.Cancel);
        Sync();
    }

    string Text(string variable) => _inputs[variable] switch
    {
        ComboBox c => c.Text ?? c.SelectedItem?.ToString() ?? "",
        TextBox t => t.Text ?? "",
        _ => "",
    };

    // As WinForms: OK on non-blank answers only; the engine reports out-of-range values when applying.
    void Sync() => _ok.IsEnabled = FormulaWindowRules.MissingOkEnabled(
        _inputs.Keys.ToDictionary(k => k, k => (string?)Text(k), StringComparer.Ordinal), _missing, checkRange: false);

    void Accept()
    {
        var values = _inputs.Keys.ToDictionary(k => k, k => Text(k).Trim(), StringComparer.Ordinal);
        var (applied, failed) = _actions.ApplyMissingInputs(_run, values);
        if (failed.Count == 0) { Close(MissingInputResult.Ok); return; }
        _message.Text = FormulaWindowRules.MissingFailureLine(applied, failed);
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _message.Text = "Could not apply: " + ex.Message; }
    }

    // ---- what tests read ----

    internal IReadOnlyList<string> AskedVariables => _inputs.Keys.ToList();
    internal bool OkEnabled => _ok.IsEnabled;
    internal string MessageText => _message.Text ?? "";
    internal void Type(string variable, string text)
    {
        switch (_inputs[variable])
        {
            case ComboBox c: c.Text = text; break;
            case TextBox t: t.Text = text; break;
        }
        Sync();
    }
    internal void ClickOk() => _ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal void ClickPerUnit() => _perUnit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal void ClickCancel() => _cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
