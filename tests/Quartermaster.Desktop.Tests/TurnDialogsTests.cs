using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Turns;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5d task 3: the Send Turn and Who are you? dialogs, the mailbox choice, the choice and password prompts and the send wait.
/// Invented TEST players Alice/Bob, sides Red/Blue; windows are built headless and never shown.</summary>
public sealed class TurnDialogsTests
{
    static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    static readonly PlayerDef[] Players = { Alice, Bob };

    static RulesPack Pack() => new()
    {
        Sides = new List<SideDef> { new() { Id = "red", Name = "Red" }, new() { Id = "blue", Name = "Blue" } },
    };

    static void Slice()
    {
        Dispatcher.UIThread.RunJobs();
        using var slice = new CancellationTokenSource(10);
        Dispatcher.UIThread.MainLoop(slice.Token);   // DispatcherTimers fire from the main loop
    }

    [AvaloniaFact]
    public void Send_dialog_defaults_recipient_and_refuses_without_relay()
    {
        var dlg = new SendTurnDialog("Alice", Players, "p1", Pack(), new MailboxValues(false, null, null));
        Assert.Equal("Send Turn", dlg.Title);
        Assert.Equal(Bob, dlg.Recipient);
        Assert.False(dlg.UseMailbox);
        Assert.Null(dlg.TryOk());   // Off is fine
        Assert.Equal(new SendTurnChoice(Bob, false, "", ""), dlg.Choice);

        var on = new SendTurnDialog("Alice", Players, "p1", Pack(), new MailboxValues(true, "  ", null));
        Assert.Equal(MailboxService.NoRelayText, on.TryOk());
        Assert.Equal("Bob (Blue)", on.Recipients[0]);
    }

    [AvaloniaFact]
    public void Who_dialog_preselects_and_hides_mailbox_when_null()
    {
        var dlg = new WhoAreYouDialog("Which?", Players, Pack(), "p2", null);
        Assert.Equal("p2", dlg.LocalPlayerId);
        Assert.False(dlg.ShowsMailbox);
        Assert.Null(dlg.TryOk());
        Assert.Equal(new WhoChoice("p2", false, "", ""), dlg.Choice);

        var first = new WhoAreYouDialog("Which?", Players, Pack(), "nobody", new MailboxValues(false, "", ""));
        Assert.Equal("p1", first.LocalPlayerId);   // none matched: the first is ticked
        Assert.True(first.ShowsMailbox);
        Assert.Equal(new[] { "Alice (Red)", "Bob (Blue)" }, first.PlayerLabels);
    }

    [AvaloniaFact]
    public void Mailbox_boxes_follow_tick_same_tick()
    {
        var m = new MailboxOptions(TurnDialogRules.SendCaption, new MailboxValues(false, "relay.example:5000 ", "pw"));
        Assert.False(m.Use);
        Assert.False(m.RelayBox.IsEnabled);
        Assert.False(m.PasswordBox.IsEnabled);
        m.UseBox.IsChecked = true;
        Assert.True(m.RelayBox.IsEnabled);   // same tick: no dispatcher turn needed
        Assert.True(m.PasswordBox.IsEnabled);
        Assert.Equal("relay.example:5000", m.Relay);
        Assert.Equal("pw", m.Password);
        Assert.Equal(512, m.RelayBox.MaxLength);
        Assert.Equal(256, m.PasswordBox.MaxLength);
        Assert.NotEqual(default, m.PasswordBox.PasswordChar);
        m.RelayBox.Text = "";
        Assert.Equal(MailboxService.NoRelayText, m.Problem);
        m.UseBox.IsChecked = false;
        Assert.False(m.RelayBox.IsEnabled);
        Assert.Null(m.Problem);
    }

    [AvaloniaFact]
    public void Choice_dialog_returns_index_and_minus_one_on_close()
    {
        var dlg = new ChoiceDialog("Something failed.", "Send Turn", new[] { "Retry", "Close" }, 1);
        Assert.Equal("Send Turn", dlg.Title);
        Assert.Equal(new[] { "Retry", "Close" }, dlg.Buttons.Select(b => (string)b.Content!));
        Assert.True(dlg.Buttons[1].IsDefault);
        Assert.Equal(0, dlg.Pick(0));

        var closed = new ChoiceDialog("x", "y", new[] { "A", "B" }, 0);
        Assert.Equal(-1, closed.Result);   // closed without a click
    }

    [AvaloniaFact]
    public void Password_prompt_masks()
    {
        var p = new PasswordPrompt("Mailbox", "Enter the password:");
        Assert.Equal("Mailbox", p.Title);
        Assert.NotEqual(default, p.Box.PasswordChar);
        p.Box.Text = "secret";
        Assert.Equal("secret", p.Entered);
    }

    [AvaloniaFact]
    public async Task Wait_returns_false_after_limit()
    {
        var never = new TaskCompletionSource();
        var w = new SendWaitWindow(never.Task, TimeSpan.FromMilliseconds(200));
        Assert.Equal("Send Turn", w.Title);
        Assert.Equal("Sending turn…", w.LabelText);
        var shown = w.RunAsync(null);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!shown.IsCompleted && DateTime.UtcNow < until) { Slice(); }
        Assert.True(shown.IsCompleted);
        Assert.False(await shown);

        var done = new SendWaitWindow(Task.CompletedTask, TimeSpan.FromSeconds(30));
        var s2 = done.RunAsync(null);
        until = DateTime.UtcNow.AddSeconds(10);
        while (!s2.IsCompleted && DateTime.UtcNow < until) { Slice(); }
        Assert.True(await s2);
    }

    static Window Owner()
    {
        var w = new Window { Width = 400, Height = 300 };
        w.Show();   // headless: never on screen
        return w;
    }

    [AvaloniaFact]
    public void Send_dialog_mailbox_on_carries_relay_and_password()
    {
        var dlg = new SendTurnDialog("Alice", Players, "p1", Pack(), new MailboxValues(true, " relay.example:5000 ", "TEST pw"));
        Assert.True(dlg.UseMailbox);
        Assert.Null(dlg.TryOk());
        Assert.Equal(new SendTurnChoice(Bob, true, "relay.example:5000", "TEST pw"), dlg.Choice);
    }

    [AvaloniaFact]
    public async Task Real_choice_dialog_closed_without_a_choice_returns_minus_one()
    {
        IDialogs d = new Dialogs();
        var owner = Owner();
        var closed = d.ChooseAsync(owner, "Something failed.", "Send Turn", new[] { "Retry", "Close" }, 0);
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed.IsCompleted);
        owner.OwnedWindows.OfType<ChoiceDialog>().Single().Close();   // the title bar's close: no button clicked
        Assert.Equal(-1, await closed);

        var picked = d.ChooseAsync(owner, "Something failed.", "Send Turn", new[] { "Retry", "Close" }, 0);
        Dispatcher.UIThread.RunJobs();
        owner.OwnedWindows.OfType<ChoiceDialog>().Single().Pick(1);
        Assert.Equal(1, await picked);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Wait_with_owner_is_modal_and_returns_when_work_ends()
    {
        IDialogs d = new Dialogs();
        var owner = Owner();
        var work = new TaskCompletionSource();
        var shown = d.WaitForAsync(owner, work.Task, TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();
        var wait = owner.OwnedWindows.OfType<SendWaitWindow>().Single();
        Assert.Equal("Send Turn", wait.Title);
        Assert.False(shown.IsCompleted);
        work.SetResult();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!shown.IsCompleted && DateTime.UtcNow < until) { Slice(); }
        Assert.True(await shown);
        Assert.Empty(owner.OwnedWindows.OfType<SendWaitWindow>());
        owner.Close();
    }
}
