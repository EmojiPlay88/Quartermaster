using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

public sealed class OcrPanelTests
{
    /// <summary>Scripted engine: Block reads return <see cref="Words"/>, single-line cell reads return <see cref="CellText"/>.</summary>
    class FakeOcr : IOcrRecognizer
    {
        public string Name => "Fake";
        public int MaxImageDimension => 10000;
        public IReadOnlyList<string> Languages { get; set; } = ["eng", "deu"];
        public string? DefaultLanguage => Languages.Count > 0 ? Languages[0] : null;
        public string UnavailableMessage => "Fake OCR has no data.";
        public IReadOnlyList<OcrWord> Words { get; set; } = Table(skipEmpty: false);
        public string CellText { get; set; } = "7";
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Languages_Used { get; } = new();

        public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
        {
            Languages_Used.Add(language);
            if (Gate is { } g) await g.Task.WaitAsync(ct);
            if (Throw != null) throw Throw;
            return layout == OcrLayout.Block ? Words : [new OcrWord(CellText, gray.Width * 0.3, gray.Height * 0.3, gray.Width * 0.4, gray.Height * 0.4)];
        }
    }

    sealed class ListOcr : FakeOcr, IOcrLanguageList
    {
        public event Action? LanguagesChanged;
        public string DisplayName(string language) => language switch { "eng" => "English", "deu" => "German", "fra" => "French", _ => language };
        public void Change(params string[] langs) { Languages = langs; LanguagesChanged?.Invoke(); }
    }

    /// <summary>2 rows x 3 columns of invented numbers, words 24 px high; optionally without the cell in row 1, column 1.</summary>
    static IReadOnlyList<OcrWord> Table(bool skipEmpty)
    {
        var words = new List<OcrWord>();
        for (var r = 0; r < 2; r++)
            for (var c = 0; c < 3; c++)
            {
                if (skipEmpty && r == 1 && c == 1) continue;
                words.Add(new OcrWord((10 + r * 3 + c).ToString(), 10 + c * 100, 10 + r * 50, 40, 24));
            }
        return words;
    }

    /// <summary>White page with black ink where each table cell is, so empty cells can be cropped and re-read.</summary>
    static SKBitmap Image()
    {
        var bmp = new SKBitmap(360, 100);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black };
        for (var r = 0; r < 2; r++)
            for (var c = 0; c < 3; c++) canvas.DrawRect(10 + c * 100 + 8, 10 + r * 50 + 6, 22, 12, ink);
        return bmp;
    }

    sealed class Rig : IDisposable
    {
        public FakeOcr Engine;
        public int OpenCalls;
        public FakeDialogs Dialogs = new();
        public ScanSessionModel Model = new();
        public SKBitmap Bmp = Image();
        public OcrPanel Panel = null!;
        public Window Win = null!;
        public System.Drawing.Rectangle? Region;
        public ScanSource Source = ScanSource.ModuleImage("invented.png");
        public int Page = 3;
        public bool NoTarget;
        public List<(string Tsv, ScanSource Source, int Page)> Imports = new();

        public Rig(Action<FakeOcr>? setup = null, bool open = false, bool list = false)
        {
            Engine = list ? new ListOcr() : new FakeOcr();
            setup?.Invoke(Engine);
            Panel = new OcrPanel(Engine, Model, Dialogs, open ? () => { OpenCalls++; return Task.CompletedTask; } : null) { Target = () => NoTarget ? null : new OcrTarget(Bmp, Region, Source, Page) };
            Panel.ImportRequested += (t, s, p) => Imports.Add((t, s, p));
            Win = new Window { Width = 500, Height = 600, Content = Panel };
            Win.Show();
        }

        public void Dispose() { Win.Close(); Bmp.Dispose(); }
    }

    static string Nl(string s) => s.Replace("\r\n", "\n");

    [AvaloniaFact]
    public async Task Read_whole_image_fills_text_and_grid()
    {
        using var rig = new Rig();
        Assert.Equal("Drag a region on the image (or read the whole image), then Read text.", rig.Panel.StatusText);
        Assert.Equal("eng", rig.Panel.SelectedLanguage);
        await rig.Panel.ReadTextAsync();
        Assert.Equal(["10", "11", "12"], rig.Panel.Cells[0]);
        Assert.Equal(["13", "14", "15"], rig.Panel.Cells[1]);
        Assert.Equal("Read 6 words; 2 rows. Check the grid, then Import into table…", rig.Panel.StatusText);
        Assert.False(rig.Panel.StatusIsProblem);
        Assert.Equal("14", rig.Panel.Grid.Cell(1, 1));
        Assert.Equal(2, rig.Panel.Grid.RowCount);
        Assert.Contains("10", rig.Panel.TextBox.Text);
        Assert.True(rig.Panel.HasGrid);
        Assert.True(rig.Panel.ImportButton.IsEnabled);
        Assert.True(rig.Panel.CopyButton.IsEnabled);
        Assert.False(rig.Panel.IsReading);
        Assert.Equal(["eng"], rig.Engine.Languages_Used.Distinct());
    }

    [AvaloniaFact]
    public async Task Read_region_status_text()
    {
        using var rig = new Rig(e => e.Gate = new TaskCompletionSource());
        rig.Region = new System.Drawing.Rectangle(10, 10, 90, 50);
        var read = rig.Panel.ReadTextAsync();
        Assert.True(rig.Panel.IsReading);
        Assert.Equal("Reading text in the region (90×50 px)…", rig.Panel.StatusText);
        Assert.False(rig.Panel.ReadButton.IsEnabled);
        Assert.True(rig.Panel.CancelButton.IsEnabled);
        rig.Engine.Gate!.SetResult();
        await read;
        rig.Region = null;
        Assert.False(rig.Panel.IsReading);
        Assert.True(rig.Panel.ReadButton.IsEnabled);

        var whole = new Rig(e => e.Gate = new TaskCompletionSource());
        using (whole)
        {
            var r2 = whole.Panel.ReadTextAsync();
            Assert.Equal("Reading text in the whole image…", whole.Panel.StatusText);
            whole.Engine.Gate!.SetResult();
            await r2;
        }
    }

    [AvaloniaFact]
    public async Task Empty_cells_reread_and_tinted()
    {
        using var rig = new Rig(e => e.Words = Table(skipEmpty: true));
        await rig.Panel.ReadTextAsync();
        Assert.Equal("7", rig.Panel.Cells[1][1]);
        Assert.True(rig.Panel.IsReread(1, 1));
        Assert.False(rig.Panel.IsReread(0, 0));
        Assert.Equal("Read 5 words; 2 rows. 1 empty cell was re-read (tinted blue). Check the grid, then Import into table…", rig.Panel.StatusText);
        Assert.Equal("7", rig.Panel.Grid.Cell(1, 1));
        Assert.Equal(Color.FromRgb(214, 234, 255), ((SolidColorBrush)rig.Panel.Grid.CellBackground(1, 1)!).Color);
        Assert.NotEqual(Color.FromRgb(214, 234, 255), ((SolidColorBrush)rig.Panel.Grid.CellBackground(0, 0)!).Color);
    }

    [AvaloniaFact]
    public async Task Some_empty_cells_message_when_reread_finds_nothing()
    {
        using var rig = new Rig(e => { e.Words = Table(skipEmpty: true); e.CellText = ""; });
        await rig.Panel.ReadTextAsync();
        Assert.EndsWith(OcrPanel.SomeEmptyText, rig.Panel.StatusText);
    }

    [AvaloniaFact]
    public async Task Edited_grid_asks_before_replacing()
    {
        using var rig = new Rig();
        await rig.Panel.ReadTextAsync();
        rig.Panel.Grid.SelectCell(0, 0);
        rig.Panel.Grid.BeginEdit(0, 0, "99");
        Assert.True(rig.Panel.Grid.IsEditing);
        rig.Dialogs.Answer = false;
        await rig.Panel.ReadTextAsync(); // commits the open edit, then asks
        Assert.Equal([(OcrPanel.ReplaceEditsQuestion, "Read text")], rig.Dialogs.Questions);
        Assert.Equal([true], rig.Dialogs.YesNoDefaultNo);
        Assert.Equal("99", rig.Panel.Cells[0][0]);
        Assert.Equal(Color.FromRgb(255, 248, 200), ((SolidColorBrush)rig.Panel.Grid.CellBackground(0, 0)!).Color);
        rig.Dialogs.Answer = true;
        await rig.Panel.ReadTextAsync();
        Assert.Equal(2, rig.Dialogs.Questions.Count);
        Assert.Equal("10", rig.Panel.Cells[0][0]);
    }

    [AvaloniaFact]
    public async Task Cancel_sets_status_and_drops_result()
    {
        using var rig = new Rig(e => e.Gate = new TaskCompletionSource());
        var read = rig.Panel.ReadTextAsync();
        rig.Panel.CancelRead("Cancelled.");
        Assert.Equal("Cancelled.", rig.Panel.StatusText);
        Assert.False(rig.Panel.IsReading);
        rig.Engine.Gate!.SetResult();
        await read;
        Assert.False(rig.Panel.HasGrid);
        Assert.Equal("Cancelled.", rig.Panel.StatusText);
        Assert.True(rig.Panel.ReadButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Stale_read_dropped_after_new_target()
    {
        using var rig = new Rig(e => e.Gate = new TaskCompletionSource());
        var gateA = rig.Engine.Gate!;
        var readA = rig.Panel.ReadTextAsync();
        rig.Panel.CancelRead(); // another image or region was chosen
        rig.Engine.Gate = new TaskCompletionSource();
        var gateB = rig.Engine.Gate;
        rig.Engine.Words = [new OcrWord("77", 10, 10, 40, 24)];
        var readB = rig.Panel.ReadTextAsync();
        Assert.True(rig.Panel.IsReading);
        gateA.SetResult();
        await readA;
        Assert.False(rig.Panel.HasGrid);              // A's result is dropped
        Assert.True(rig.Panel.IsReading);             // and A finishing does not end B
        gateB.SetResult();
        await readB;
        Assert.Equal(["77"], rig.Panel.Cells[0]);
        Assert.False(rig.Panel.IsReading);
    }

    [AvaloniaFact]
    public async Task Import_exception_is_reported_not_thrown()
    {
        using var rig = new Rig();
        rig.Panel.ImportRequested += (_, _, _) => throw new InvalidOperationException("boom");
        await rig.Panel.ReadTextAsync();
        rig.Panel.ImportButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Something went wrong: boom", rig.Panel.StatusText);
        Assert.True(rig.Panel.StatusIsProblem);
    }

    [AvaloniaFact]
    public async Task Fix_digits_toggle_rebuilds()
    {
        using var rig = new Rig(e => e.Words = [new OcrWord("1O", 10, 10, 40, 24), new OcrWord("2", 110, 10, 40, 24)]);
        await rig.Panel.ReadTextAsync();
        Assert.Equal("10", rig.Panel.Cells[0][0]);
        rig.Panel.FixBox.IsChecked = false;
        Assert.Equal("1O", rig.Panel.Cells[0][0]);
        Assert.Equal("1O", rig.Panel.Grid.Cell(0, 0));
        // with an edit, a refused rebuild puts the box back
        rig.Panel.Grid.BeginEdit(0, 1, "5"); rig.Panel.Grid.CommitEdit();
        rig.Dialogs.Answer = false;
        rig.Panel.FixBox.IsChecked = true;
        Assert.Equal(false, rig.Panel.FixBox.IsChecked);
        Assert.Equal("5", rig.Panel.Cells[0][1]);
        Assert.Equal(("Rebuild the grid from the reading? Your edits to the grid will be lost.", "Read text"), rig.Dialogs.Questions[^1]);
        rig.Dialogs.Answer = true;
        rig.Panel.FixBox.IsChecked = true;
        Assert.Equal("10", rig.Panel.Cells[0][0]);
        Assert.Equal("2", rig.Panel.Cells[0][1]);
    }

    [AvaloniaFact]
    public async Task Columns_merge_split()
    {
        using var rig = new Rig();
        await rig.Panel.ReadTextAsync();
        Assert.Equal(3, rig.Panel.Cells[0].Length);
        rig.Panel.Grid.SelectCell(0, 0);
        rig.Panel.MergeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, rig.Panel.Cells[0].Length);
        Assert.Equal("Columns 1 and 2 merged.", rig.Panel.StatusText);
        rig.Panel.Grid.SelectCell(0, 0);
        rig.Panel.SplitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(3, rig.Panel.Cells[0].Length);
        Assert.Equal("Column 1 split in two.", rig.Panel.StatusText);
        // the last column has no column to its right
        rig.Panel.Grid.SelectCell(0, 2);
        rig.Panel.MergeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Select a cell in a column that has a column to its right, then Merge joins the two.", rig.Panel.StatusText);
        // choose 2 columns
        rig.Panel.ColumnsBox.SelectedIndex = 2;
        Assert.Equal(2, rig.Panel.Cells[0].Length);
        rig.Panel.ColumnsBox.SelectedIndex = 5;
        Assert.Equal("Only 3 columns could be found: there are not enough gaps between the words.", rig.Panel.StatusText);
    }

    [AvaloniaFact]
    public async Task Import_raises_tsv_source_page()
    {
        using var rig = new Rig();
        Assert.False(rig.Panel.ImportButton.IsEnabled);
        await rig.Panel.ReadTextAsync();
        rig.Panel.Grid.BeginEdit(0, 0, "42"); // an open edit is committed first
        rig.Panel.ImportButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var (tsv, source, page) = Assert.Single(rig.Imports);
        Assert.Equal("42\t11\t12\n13\t14\t15", Nl(tsv).TrimEnd('\n'));
        Assert.Equal(rig.Source, source);
        Assert.Equal(3, page);
    }

    [AvaloniaFact]
    public async Task Copy_text_uses_clipboard()
    {
        using var rig = new Rig();
        await rig.Panel.ReadTextAsync();
        rig.Panel.CopyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("The grid was copied (tab-separated).", rig.Panel.StatusText);
        Assert.Equal("10\t11\t12\n13\t14\t15", Nl(Assert.Single(rig.Dialogs.ClipboardWrites)).TrimEnd('\n'));
        rig.Panel.Tabs.SelectedIndex = 1;
        rig.Panel.CopyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("The text was copied.", rig.Panel.StatusText);
        Assert.Equal(Nl(rig.Panel.TextBox.Text!), Nl(rig.Dialogs.ClipboardWrites[^1]));
        rig.Panel.ShowGridTab();
        Assert.Equal(0, rig.Panel.Tabs.SelectedIndex);
    }

    [AvaloniaFact]
    public void No_languages_shows_unavailable_message()
    {
        using var rig = new Rig(e => e.Languages = []);
        Assert.Equal("Fake OCR has no data.", rig.Panel.StatusText);
        Assert.True(rig.Panel.StatusIsProblem);
        Assert.False(rig.Panel.ReadButton.IsEnabled);
        Assert.False(rig.Panel.LanguageBox.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Unavailable_exception_shows_red_status()
    {
        using var rig = new Rig(e => e.Throw = new OcrUnavailableException("OCR could not be started on this computer: gone"));
        await rig.Panel.ReadTextAsync();
        Assert.Equal("OCR could not be started on this computer: gone", rig.Panel.StatusText);
        Assert.True(rig.Panel.StatusIsProblem);
        Assert.True(rig.Panel.ReadButton.IsEnabled);

        rig.Engine.Throw = new InvalidDataException("Bad image.");
        await rig.Panel.ReadTextAsync();
        Assert.Equal("Bad image.", rig.Panel.StatusText);
        Assert.True(rig.Panel.StatusIsProblem);

        rig.NoTarget = true;
        await rig.Panel.ReadTextAsync();
        Assert.Equal("Open a scan first: Open scan…, Scan this page, or an image from the document list.", rig.Panel.StatusText);
    }

    [AvaloniaFact]
    public async Task Dispose_during_read_is_quiet()
    {
        using var rig = new Rig(e => e.Gate = new TaskCompletionSource());
        var read = rig.Panel.ReadTextAsync();
        rig.Engine.Throw = new ObjectDisposedException("TesseractRecognizer"); // the engine was disposed while the read waited
        rig.Engine.Gate!.SetResult();
        await read;
        Assert.False(rig.Panel.StatusIsProblem);
        Assert.Equal("Reading text in the whole image…", rig.Panel.StatusText);
        Assert.False(rig.Panel.IsReading);
        Assert.False(rig.Panel.HasGrid);
    }

    static string[] Texts(OcrPanel p) => p.LanguageBox.Items.Select(i => i!.ToString()!).ToArray();

    [AvaloniaFact]
    public async Task Dropdown_shows_display_names_and_reads_with_tags()
    {
        using var rig = new Rig(list: true);
        Assert.Equal(["English", "German"], Texts(rig.Panel));
        rig.Panel.LanguageBox.SelectedIndex = 1;
        Assert.Equal("deu", rig.Panel.SelectedLanguage);
        await rig.Panel.ReadTextAsync();
        Assert.Equal(["deu"], rig.Engine.Languages_Used.Distinct());
    }

    [AvaloniaFact]
    public void Engine_without_language_list_shows_tags()
    {
        using var rig = new Rig();
        Assert.Equal(["eng", "deu"], Texts(rig.Panel));
    }

    [AvaloniaFact]
    public void More_languages_item_is_last_and_opens_settings()
    {
        using var rig = new Rig(open: true, list: true);
        Assert.Equal(["English", "German", "More languages…"], Texts(rig.Panel));
        rig.Panel.LanguageBox.SelectedIndex = 1;
        rig.Panel.LanguageBox.SelectedIndex = 2;
        Assert.Equal(1, rig.OpenCalls);
        Assert.Equal("deu", rig.Panel.SelectedLanguage);
        Assert.Equal(1, rig.Panel.LanguageBox.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task LanguagesChanged_refreshes_list_and_keeps_selection()
    {
        using var rig = new Rig(open: true, list: true);
        rig.Panel.LanguageBox.SelectedIndex = 1;
        ((ListOcr)rig.Engine).Change("eng", "deu", "fra");
        await Task.Delay(50);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["English", "German", "French", "More languages…"], Texts(rig.Panel));
        Assert.Equal("deu", rig.Panel.SelectedLanguage);
        Assert.Equal(0, rig.OpenCalls);
    }

    [AvaloniaFact]
    public void Removing_selected_language_selects_default()
    {
        using var rig = new Rig(list: true);
        rig.Panel.LanguageBox.SelectedIndex = 1;
        ((ListOcr)rig.Engine).Change("eng", "fra");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("eng", rig.Panel.SelectedLanguage);
    }

    [AvaloniaFact]
    public void No_more_languages_item_without_callback()
    {
        using var rig = new Rig(list: true);
        Assert.DoesNotContain("More languages…", Texts(rig.Panel));
    }

    [AvaloniaFact]
    public void Empty_engine_still_offers_more_languages()
    {
        using var rig = new Rig(e => e.Languages = [], open: true, list: true);
        Assert.Equal("Fake OCR has no data.", rig.Panel.StatusText);
        Assert.True(rig.Panel.StatusIsProblem);
        Assert.False(rig.Panel.ReadButton.IsEnabled);
        Assert.True(rig.Panel.LanguageBox.IsEnabled);
        Assert.Equal(["More languages…"], Texts(rig.Panel));
        rig.Panel.LanguageBox.SelectedIndex = 0;
        Assert.Equal(1, rig.OpenCalls);
        Assert.Equal("", rig.Panel.SelectedLanguage);
    }

    [AvaloniaFact]
    public void Language_refresh_from_another_thread_is_posted()
    {
        using var rig = new Rig(list: true);
        Task.Run(() => ((ListOcr)rig.Engine).Change("eng", "deu", "fra")).Wait();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["English", "German", "French"], Texts(rig.Panel));
    }
}
