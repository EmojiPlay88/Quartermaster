using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 2: the Columns, Paste table, Make block and Import target dialogs, the clipboard and the info box. Headless only.</summary>
public sealed class TableDialogsTests
{
    static Window Owner()
    {
        var w = new Window { Width = 400, Height = 300 };
        w.Show();
        return w;
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Columns_dialog_returns_trimmed_nonblank_keys()
    {
        var owner = Owner();
        var d = new ColumnsDialog(new[] { "1", "2-3", "4+" });
        Assert.Equal("Columns", d.Title);
        Assert.Equal((300d, 260d), (d.Width, d.Height));
        Assert.Equal(string.Join(Environment.NewLine, "1", "2-3", "4+"), d.Box.Text);
        var t = d.ShowDialog<IReadOnlyList<string>?>(owner);
        d.Box.Text = "  1 \r\n\r\n 2-3\n   \nsix  \n";
        Click(d.OkButton);
        Assert.Equal(new[] { "1", "2-3", "six" }, await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Columns_cancel_returns_null()
    {
        var owner = Owner();
        var d = new ColumnsDialog(new[] { "1" });
        var t = d.ShowDialog<IReadOnlyList<string>?>(owner);
        d.Box.Text = "9";
        Click(d.CancelButton);
        Assert.Null(await t);
        owner.Close();
    }

    const string Clip = "Key\tFoot\tHorse\n1-3\t1\t2\n4+\t3\t4\n";

    [AvaloniaFact]
    public void Paste_preview_follows_checkboxes()
    {
        var d = new PasteTableDialog(Clip);
        Assert.Equal("Paste table from clipboard", d.Title);
        Assert.Equal((520d, 360d), (d.Width, d.Height));
        Assert.True(d.HeaderBox.IsChecked);
        Assert.True(d.KeysBox.IsChecked);
        Assert.Equal(new[] { "Row key", "Foot", "Horse" }, d.PreviewHeaders);
        Assert.Equal(new[] { "1-3", "1", "2" }, d.PreviewRows[0]);
        Assert.True(d.OkButton.IsEnabled);

        d.KeysBox.IsChecked = false;
        Assert.Equal(new[] { "Key", "Foot", "Horse" }, d.PreviewHeaders);
        Assert.Equal(new[] { "1-3", "1", "2" }, d.PreviewRows[0]);
        Assert.Equal(2, d.PreviewRows.Count);

        d.HeaderBox.IsChecked = false;
        Assert.Equal(3, d.PreviewRows.Count);   // the header row is data now
        Assert.Equal(new[] { "Key", "Foot", "Horse" }, d.PreviewRows[0]);
        Assert.Equal(new[] { "", "", "" }, d.PreviewHeaders);
    }

    [AvaloniaFact]
    public async Task Paste_ok_returns_the_parsed_grid()
    {
        var owner = Owner();
        var d = new PasteTableDialog(Clip);
        var t = d.ShowDialog<PastedGrid?>(owner);
        Click(d.OkButton);
        var g = await t;
        Assert.Equal(new[] { "Foot", "Horse" }, g!.ColumnKeys);
        Assert.Equal(new[] { "1-3", "4+" }, g.RowKeys);
        owner.Close();
    }

    [AvaloniaFact]
    public void Paste_tsv_only_problem_disables_ok_and_shows_red_note()
    {
        var d = new PasteTableDialog("Cost\n10\n20\n", "Import into table", "Rows go to Table A.", tsvOnly: true);
        Assert.Equal("Import into table", d.Title);
        var problem = TablePaste.Problem(TablePaste.Parse("Cost\n10\n", true, true, true), true)!;
        Assert.Equal(problem, d.Note.Text);
        Assert.False(d.OkButton.IsEnabled);
        Assert.Equal(Brushes.Firebrick, d.Note.Foreground);

        d.KeysBox.IsChecked = false;   // clearing the key box clears the problem
        Assert.True(d.OkButton.IsEnabled);
        Assert.Equal("Rows go to Table A.", d.Note.Text);
        Assert.NotEqual(Brushes.Firebrick, d.Note.Foreground);

        // the same text pasted (not tsv-only) shows no problem
        var p = new PasteTableDialog("Cost\n10\n20\n");
        Assert.Equal(PasteTableDialog.DefaultNote, p.Note.Text);
    }

    [AvaloniaFact]
    public void Paste_title_and_note_texts()
    {
        var d = new PasteTableDialog(Clip);
        Assert.Equal("Paste table from clipboard", d.Title);
        Assert.Equal("OK replaces the rows, columns and cells of the selected table.", d.Note.Text);
        var i = new PasteTableDialog(Clip, "Import into table", "Rows go to a new table.", tsvOnly: true);
        Assert.Equal("Import into table", i.Title);
        Assert.Equal("Rows go to a new table.", i.Note.Text);
        Assert.Equal("First row is the column header", d.HeaderBox.Content);
        Assert.Equal("First column is the row key", d.KeysBox.Content);
        Assert.False(new PasteTableDialog("").OkButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Block_dialog_limits_and_defaults()
    {
        var owner = Owner();
        var d = new BlockDialog();
        Assert.Equal("Make block", d.Title);
        Assert.Equal((300d, 110d), (d.Width, d.Height));
        Assert.Equal((1m, 99m, 2m), (d.CountBox.Minimum, d.CountBox.Maximum, d.CountBox.Value));
        var t = d.ShowDialog<(int Count, string Label)?>(owner);
        d.CountBox.Value = 5;
        d.LabelBox.Text = "  Siege  ";
        Click(d.OkButton);
        Assert.Equal((5, "Siege"), await t);

        var c = new BlockDialog();
        var tc = c.ShowDialog<(int Count, string Label)?>(owner);
        Click(c.CancelButton);
        Assert.Null(await tc);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Import_target_buttons_and_default()
    {
        var owner = Owner();
        var d = new ImportTargetDialog("Terrain");
        Assert.Equal("Import into table", d.Title);
        Assert.Equal("Where should the scanned rows go?", d.HeadingText.Text);
        Assert.Equal("Into a new table, or into the selected table \"Terrain\" (its rows, columns and cells are replaced).", d.MessageText.Text);
        Assert.Equal("New table…", d.NewButton.Content);
        Assert.Equal("Replace \"Terrain\"", d.ReplaceButton.Content);
        Assert.Equal("Cancel", d.CancelButton.Content);
        Assert.True(d.NewButton.IsDefault);
        Assert.False(d.ReplaceButton.IsDefault);

        foreach (var (pick, want) in new (Func<ImportTargetDialog, Button>, bool?)[]
                 { (x => x.NewButton, false), (x => x.ReplaceButton, true), (x => x.CancelButton, null) })
        {
            var x = new ImportTargetDialog("Terrain");
            var t = x.ShowDialog<bool?>(owner);
            Click(pick(x));
            Assert.Equal(want, await t);
        }
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Clipboard_round_trip_headless()
    {
        var owner = Owner();
        IDialogs dialogs = new Dialogs();
        await dialogs.SetClipboardTextAsync(owner, "a\tb\nc\td");
        Assert.Equal("a\tb\nc\td", await dialogs.GetClipboardTextAsync(owner));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Yes_no_default_button_follows_defaultNo()
    {
        var owner = Owner();
        IDialogs dialogs = new Dialogs();
        var yes = dialogs.AskYesNoAsync(owner, "Delete?", "Quartermaster");
        var box = owner.OwnedWindows.OfType<Window>().Single();
        var yesBtn = FindButtons(box).Single(b => (string?)b.Content == "Yes");
        Assert.True(yesBtn.IsDefault);
        Click(yesBtn);
        Assert.True(await yes);

        var no = dialogs.AskYesNoAsync(owner, "Delete?", "Quartermaster", defaultNo: true);
        box = owner.OwnedWindows.OfType<Window>().Single();
        var noBtn = FindButtons(box).Single(b => (string?)b.Content == "No");
        Assert.True(noBtn.IsDefault);
        Click(noBtn);
        Assert.False(await no);
        owner.Close();
    }

    static IEnumerable<Button> FindButtons(Window w) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Button>();

    [AvaloniaFact]
    public async Task Quiet_dialogs_auto_confirm_import()
    {
        var owner = Owner();
        var quiet = (IDialogs)Activator.CreateInstance(typeof(Program).GetNestedType("QuietDialogs", System.Reflection.BindingFlags.NonPublic)!)!;
        var g = await quiet.PasteTableAsync(owner, "Key\tV\na\t1\nb\t2\n", "Import into table", "x", tsvOnly: true);
        Assert.Equal(new[] { "V" }, g!.ColumnKeys);
        Assert.Equal(new[] { "a", "b" }, g.RowKeys);
        Assert.Null(await quiet.PasteTableAsync(owner, "Cost\n10\n", "Import into table", "x", tsvOnly: true));   // the problem case: not confirmed
        Assert.Null(await quiet.PasteTableAsync(owner, "Key\tV\na\t1\n"));
        Assert.False(await quiet.AskImportTargetAsync(owner, "T"));
        owner.Close();
    }
}
