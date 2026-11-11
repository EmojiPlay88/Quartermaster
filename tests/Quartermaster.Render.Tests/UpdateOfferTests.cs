using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests;

public class UpdateOfferTests
{
    static UpdateInfo Info(string notes = "") => new(new Version(9, 9, 9), "quartermaster-v9.9.9", notes,
        new Uri("https://example.invalid/a.zip"), new Uri("https://example.invalid/a.sha256"), 1572864, new Uri("https://example.invalid/r"));

    [Fact]
    public void Bar_text_and_actions()
    {
        Assert.Equal("Quartermaster 9.9.9 is available.", UpdateOffer.BarText(Info()));
        Assert.Equal(["What's new", "Update now", "Later", "Skip this version"], UpdateOffer.BarActions);
    }

    [Fact]
    public void Auto_check_due_rules()
    {
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(UpdateOffer.AutoCheckDue(false, false, null, now));
        Assert.False(UpdateOffer.AutoCheckDue(true, true, null, now));
        Assert.False(UpdateOffer.AutoCheckDue(true, false, now.AddHours(-1), now));
        Assert.True(UpdateOffer.AutoCheckDue(true, false, now.AddHours(-25), now));
        Assert.True(UpdateOffer.AutoCheckDue(true, false, null, now));
    }

    [Fact]
    public void Manual_texts_for_up_to_date_and_failed()
    {
        Assert.Equal($"Quartermaster {AppVersion.Text} is up to date.", UpdateOffer.ManualText(new CheckResult(CheckOutcome.UpToDate, null, null)));
        Assert.Equal("Could not check for updates.\n\nboom", UpdateOffer.ManualText(new CheckResult(CheckOutcome.Failed, null, "boom")));
        Assert.Equal("", UpdateOffer.ManualText(new CheckResult(CheckOutcome.Available, Info(), null)));
    }

    [Fact]
    public void Skipped_question_and_whats_new()
    {
        Assert.Equal("Quartermaster 9.9.9 is available, but you chose to skip it. Offer it again?", UpdateOffer.SkippedQuestion(Info()));
        Assert.Equal("What's new in Quartermaster 9.9.9", UpdateOffer.WhatsNewTitle(Info()));
        Assert.Equal("(This release has no notes.)", UpdateOffer.NoNotes);
    }

    [Fact]
    public void Download_status_formats_megabytes()
    {
        // WinForms formats with the current culture ("1.5" in English, "1,5" in German).
        Assert.Equal($"Downloading Quartermaster-9.9.9-win-x64.zip ({1.5:0.0} MB)…", UpdateOffer.DownloadStatus("Quartermaster-9.9.9-win-x64.zip", 1572864));
        Assert.Equal("Checking and unpacking…", UpdateOffer.Unpacking);
        Assert.Equal("Cancelling…", UpdateOffer.Cancelling);
        Assert.Equal("The update was not installed; nothing was changed.\n\nx", string.Format(UpdateOffer.NotInstalledFormat, "x"));
    }
}
